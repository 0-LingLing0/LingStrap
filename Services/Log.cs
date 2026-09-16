using System;
using System.IO;

namespace Lingstrap.Services;

/// <summary>Dead simple file logger. One file per run.</summary>
public static class Log
{
    private static readonly object Gate = new();

    /// <summary>
    /// This run's log file - exposed so UI (e.g. a "Copy log" button) can read it back. Includes the
    /// process id because Lingstrap routinely spawns several of its own watcher processes
    /// (multi-instance/Discord/activity/companion) within the same wall-clock second, which would
    /// otherwise all share one filename (computed once per process from DateTime.Now) and interleave
    /// unrelated processes' log lines into a single file with no way to tell them apart.
    /// </summary>
    public static readonly string FilePath =
        Path.Combine(Paths.Logs, $"Lingstrap_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Environment.ProcessId}.log");

    /// <summary>
    /// Every launch - the main process plus whichever watcher processes it spawns - gets its own log
    /// file (see FilePath's own doc comment for why), so a normal week of regular use can easily add
    /// up to dozens of small files with nothing ever removing them. Pruning once per process on
    /// startup, rather than on a timer, keeps this simple - it costs one directory scan even across
    /// several watcher processes starting close together, which is negligible.
    /// </summary>
    static Log()
    {
        PruneOldLogs();
    }

    private static void PruneOldLogs()
    {
        try
        {
            if (!Directory.Exists(Paths.Logs)) return;

            var cutoff = DateTime.Now.AddDays(-14);
            foreach (var file in Directory.GetFiles(Paths.Logs, "Lingstrap_*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
                catch
                {
                    // Skip files still open by another Lingstrap process (a long-running watcher) or
                    // otherwise not removable right now - it'll get picked up on a later launch.
                }
            }
        }
        catch
        {
            // Pruning must never crash the app - same rule as logging itself.
        }
    }

    public static void Info(string message)  => Write("INFO", message);
    public static void Warn(string message)  => Write("WARN", message);
    public static void Error(string message) => Write("ERR ", message);

    public static void Error(string message, Exception ex) =>
        Write("ERR ", $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.Logs);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
        System.Diagnostics.Debug.WriteLine(line);
    }
}
