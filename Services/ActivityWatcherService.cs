using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Lingstrap.Models;

namespace Lingstrap.Services;

/// <summary>
/// Tails every active Roblox client's log file and raises events for each game join it finds.
/// With multi-instance, more than one RobloxPlayerBeta.exe can be running at once, each writing
/// its own log file - "the newest one" flip-flops between them as both stay active, so each file
/// is tracked independently rather than following a single "current file" pointer.
/// </summary>
public static class ActivityWatcherService
{
    // Roblox logs this the instant a join starts - but the IP here is an internal rendezvous
    // address (typically a private 10.x range) that can never be geolocated. It's only useful to
    // correlate everything else to this job, not as the address to actually look up.
    private static readonly Regex JoinPattern = new(
        @"! Joining game '(?<jobid>[0-9a-f-]{36})' place (?<placeid>\d+) at (?<ip>[0-9.]+)",
        RegexOptions.Compiled);

    // Logged a moment later (typically ~150ms), once the real game connection is established.
    // This IP is the actual, public server address - the one worth geolocating.
    private static readonly Regex ServerIdPattern = new(
        @"serverId: (?<ip>[0-9.]+)\|(?<port>\d+)",
        RegexOptions.Compiled);

    // Roblox logs this exact line when leaving a game server, whether by choice or disconnect.
    private static readonly Regex LeavePattern = new(
        @"\[FLog::SingleSurfaceApp\] leaveUGCGameInternal",
        RegexOptions.Compiled);

    public static event Action<ActivityEntry>? Joined;
    public static event Action? Left;

    /// <summary>
    /// One-off, non-tailing lookup for "what server is already joined, right now" - unlike Start(),
    /// which deliberately skips a log file's existing content so a long-running watcher doesn't
    /// re-announce a stale join, this reads the whole file. Meant for a watcher that can start after
    /// the join it cares about already happened (the FPS/ping overlay's elevated process, which can
    /// take a couple of seconds to spin up via schtasks) and would otherwise never see it via the
    /// live Joined event at all this session.
    /// </summary>
    public static string? TryGetLastKnownServerIp()
    {
        try
        {
            if (!Directory.Exists(Paths.RobloxLogs)) return null;

            var newest = Directory.GetFiles(Paths.RobloxLogs, "*.log")
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null) return null;

            using var fs = new FileStream(newest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var text = reader.ReadToEnd();

            var matches = ServerIdPattern.Matches(text);
            return matches.Count > 0 ? matches[^1].Groups["ip"].Value : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read last known server IP: {ex.Message}");
            return null;
        }
    }

    private static Timer? _timer;

    /// <summary>Per-log-file read position and any join still waiting for its real server address.</summary>
    private class FileState
    {
        public long Position;
        public ActivityEntry? PendingJoin;
        public Timer? PendingJoinTimeoutTimer;
    }

    private static readonly object FilesLock = new();
    private static readonly Dictionary<string, FileState> Files = new();

    public static void Start()
    {
        if (_timer is not null) return;

        try
        {
            if (Directory.Exists(Paths.RobloxLogs))
            {
                // Skip every existing client's history - only report activity from now on, exactly
                // as before, just applied per file instead of to a single "newest" one.
                foreach (var file in Directory.GetFiles(Paths.RobloxLogs, "*.log"))
                    Files[file] = new FileState { Position = new FileInfo(file).Length };
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not seed activity watcher: {ex.Message}");
        }

        _timer = new Timer(_ => Poll(), null, 1500, 1500);
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;

        lock (FilesLock)
        {
            foreach (var state in Files.Values)
                state.PendingJoinTimeoutTimer?.Dispose();
            Files.Clear();
        }
    }

    private static void Poll()
    {
        try
        {
            if (!Directory.Exists(Paths.RobloxLogs)) return;
            var currentFiles = new HashSet<string>(Directory.GetFiles(Paths.RobloxLogs, "*.log"));

            lock (FilesLock)
            {
                // A brand new client's log file - skip straight to its current length, same as any
                // file already known at Start(): only report what happens in it from here on.
                foreach (var file in currentFiles)
                {
                    if (!Files.ContainsKey(file))
                        Files[file] = new FileState { Position = SafeLength(file) };
                }

                // Roblox doesn't touch a client's log again once that client has closed - drop
                // anything no longer on disk so this dictionary doesn't grow across a long session.
                foreach (var stale in Files.Keys.Where(f => !currentFiles.Contains(f)).ToList())
                {
                    Files[stale].PendingJoinTimeoutTimer?.Dispose();
                    Files.Remove(stale);
                }
            }

            foreach (var file in currentFiles)
                PollFile(file);
        }
        catch (Exception ex)
        {
            Log.Warn($"Activity log tail failed: {ex.Message}");
        }
    }

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; }
        catch { return 0; }
    }

    private static void PollFile(string file)
    {
        FileState state;
        lock (FilesLock)
        {
            if (!Files.TryGetValue(file, out state!)) return;
        }

        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (fs.Length < state.Position) state.Position = 0; // rotated/truncated
            if (fs.Length <= state.Position) return;             // nothing new in this file

            fs.Seek(state.Position, SeekOrigin.Begin);
            var buffer = new byte[fs.Length - state.Position];
            var read = fs.Read(buffer, 0, buffer.Length);
            var text = Encoding.UTF8.GetString(buffer, 0, read);

            var lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0) return; // no complete line yet, wait for more

            var complete = text[..(lastNewline + 1)];
            state.Position += Encoding.UTF8.GetByteCount(complete);

            foreach (var line in complete.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var join = JoinPattern.Match(line);
                if (join.Success)
                {
                    var entry = new ActivityEntry
                    {
                        Timestamp = DateTime.Now,
                        SessionStart = DateTime.Now,
                        JobId = join.Groups["jobid"].Value,
                        PlaceId = join.Groups["placeid"].Value,
                        ServerIp = join.Groups["ip"].Value, // placeholder - the real address below usually arrives within milliseconds
                        SourceLogFile = file,
                    };

                    Log.Info($"Join detected: place={entry.PlaceId} job={entry.JobId} rendezvous-ip={entry.ServerIp} " +
                             $"file={Path.GetFileName(file)} - waiting briefly for the real server address " +
                             $"(handlers attached: {Joined?.GetInvocationList().Length ?? 0})");

                    QueuePendingJoin(state, entry);
                    continue;
                }

                var serverId = ServerIdPattern.Match(line);
                if (serverId.Success)
                {
                    lock (FilesLock)
                    {
                        if (state.PendingJoin != null)
                        {
                            var publicIp = serverId.Groups["ip"].Value;
                            var port = serverId.Groups["port"].Value;
                            Log.Info($"Real server address resolved for job={state.PendingJoin.JobId}: {publicIp}:{port} (rendezvous was {state.PendingJoin.ServerIp})");

                            state.PendingJoin.ServerIp = publicIp;
                            state.PendingJoin.ServerPort = port;
                            FlushPendingJoinNoLock(state);
                        }
                    }
                    continue;
                }

                if (LeavePattern.IsMatch(line))
                    Left?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read {Path.GetFileName(file)}: {ex.Message}");
        }
    }

    private static void QueuePendingJoin(FileState state, ActivityEntry entry)
    {
        lock (FilesLock)
        {
            // A new join in this same file always supersedes whatever was still pending for it
            // (e.g. a fast rejoin) - flush the old one now on its rendezvous address rather than
            // losing it silently.
            if (state.PendingJoin != null) FlushPendingJoinNoLock(state);

            state.PendingJoin = entry;
            state.PendingJoinTimeoutTimer?.Dispose();
            state.PendingJoinTimeoutTimer = new Timer(_ =>
            {
                lock (FilesLock)
                {
                    if (state.PendingJoin != entry) return;
                    Log.Info($"No real server address seen within 3s for job={entry.JobId} - using the rendezvous address {entry.ServerIp} as-is.");
                    FlushPendingJoinNoLock(state);
                }
            }, null, 3000, Timeout.Infinite);
        }
    }

    /// <summary>Caller must hold FilesLock.</summary>
    private static void FlushPendingJoinNoLock(FileState state)
    {
        var entry = state.PendingJoin;
        state.PendingJoin = null;
        state.PendingJoinTimeoutTimer?.Dispose();
        state.PendingJoinTimeoutTimer = null;

        if (entry != null) Joined?.Invoke(entry);
    }
}
