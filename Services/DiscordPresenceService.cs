using System;
using System.Diagnostics;
using System.Threading;
using DiscordRPC;
using Lingstrap.Models;

namespace Lingstrap.Services;

/// <summary>
/// Shows "Playing Lingstrap" on Discord instead of Roblox's own Discord integration.
///
/// Mitigation, not a guaranteed fix: Discord's own built-in Roblox detection overrides this once
/// RobloxPlayerBeta.exe starts, and Lingstrap can't disable that detection - only out-compete it
/// by re-applying its own payload every few seconds for as long as Roblox is running, plus
/// immediately on server join/leave. Discord may still occasionally flip back to its own
/// detection in the window between re-applies - a shorter interval narrows that window but can't
/// close it completely, since Discord's own re-detection isn't on a schedule Lingstrap controls.
///
/// When CloseLingstrapOnLaunch will exit this process shortly after Roblox starts, the re-apply
/// loop instead runs in a detached watcher process (the same pattern as CompanionAppService and
/// MultiInstanceWatcher) so it keeps working for the rest of the Roblox session. Otherwise it runs
/// right here, in-process, tied to this window's own lifetime - running both at once would mean
/// two separate processes holding two separate Discord RPC connections under the same client ID,
/// which can itself cause the presence to flicker between them.
/// </summary>
public static class DiscordPresenceService
{
    private const string ClientId = "1548993478438944878";
    private const int ReapplyIntervalMs = 3000;
    private const string WatcherArg = "-discordwatcher";
    private const string WatcherGuardMutexName = "Lingstrap_DiscordWatcherActive";

    private static DiscordRpcClient? _client;
    private static Timer? _reapplyTimer;
    private static RichPresence? _currentPresence;

    public static void Start()
    {
        if (_client is not null) return;

        try
        {
            _client = new DiscordRpcClient(ClientId)
            {
                // The library's own change-detection would otherwise skip re-sending a payload
                // that looks identical to the last one *Lingstrap* set - but Discord's own Roblox
                // detection changes the presence externally without the library knowing, so an
                // "identical" resend is exactly what's needed to stomp it back.
                SkipIdenticalPresence = false,
            };
            _client.Initialize();
            SetIdle();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not start Discord Rich Presence: {ex.Message}");
        }
    }

    public static void Stop()
    {
        StopReapplyTimer();
        _client?.Dispose();
        _client = null;
    }

    public static void SetIdle() => Apply(new RichPresence
    {
        Details = "Playing Lingstrap",
        State = "In the launcher",
        Timestamps = Timestamps.Now,
    });

    public static async void SetPlaying(string placeId)
    {
        var name = await RobloxPlaceNameService.GetNameAsync(placeId);
        Apply(new RichPresence
        {
            Details = "Playing Lingstrap",
            State = name != null ? $"In a game ({name})" : "In a game",
            Timestamps = Timestamps.Now,
            // Some Discord clients weight a presence's own party/instance data over their built-in
            // game-detection heuristics - worth setting even though it's not guaranteed to matter.
            Party = new Party { ID = $"lingstrap-{placeId}", Size = 1, Max = 1 },
        });
    }

    /// <summary>
    /// Call once Roblox's client process has started. If this window will stay open for the whole
    /// session, the re-apply timer runs right here. If CloseLingstrapOnLaunch means this process
    /// is about to exit, spawns a detached watcher instead so re-applying and join/leave updates
    /// keep running for the rest of the session.
    /// </summary>
    public static void OnRobloxStarted()
    {
        if (!SettingsService.Current.DiscordRichPresence) return;

        if (!SettingsService.Current.CloseLingstrapOnLaunch)
        {
            StartReapplyTimer();
            return;
        }

        SpawnWatcher();
    }

    /// <summary>Call once Roblox's client process has closed - the in-process timer path only (the
    /// detached watcher handles its own cleanup independently).</summary>
    public static void OnRobloxClosed()
    {
        StopReapplyTimer();
        SetIdle();
    }

    private static void StartReapplyTimer()
    {
        if (_client is null) return;
        StopReapplyTimer();
        _reapplyTimer = new Timer(_ => Reapply(), null, ReapplyIntervalMs, ReapplyIntervalMs);
    }

    private static void StopReapplyTimer()
    {
        _reapplyTimer?.Dispose();
        _reapplyTimer = null;
    }

    private static void Reapply()
    {
        if (_currentPresence is null) return;
        try { _client?.SetPresence(_currentPresence); }
        catch (Exception ex) { Log.Warn($"Could not re-apply Discord presence: {ex.Message}"); }
    }

    private static void Apply(RichPresence presence)
    {
        _currentPresence = presence;
        try { _client?.SetPresence(presence); }
        catch (Exception ex) { Log.Warn($"Could not set Discord presence: {ex.Message}"); }
    }

    private static void SpawnWatcher()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                Log.Warn("Could not resolve own exe path - Discord presence watcher not started.");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = WatcherArg,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not start Discord presence watcher: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs on the detached watcher process. Connects to Discord, tails the Roblox log directly
    /// for join/leave (independent of whatever the interactive process's own ActivityCoordinator
    /// is doing, so nothing gets double-logged), and blocks until every Roblox client has closed.
    /// </summary>
    public static void RunWatcherAndBlock()
    {
        SettingsService.Load();

        using var guard = new Mutex(initiallyOwned: true, name: WatcherGuardMutexName, createdNew: out var isFirst);
        if (!isFirst)
        {
            Log.Info("A Discord presence watcher is already running - exiting immediately.");
            return;
        }

        Start();
        if (_client is null) return; // couldn't connect - nothing more to do

        ActivityWatcherService.Joined += OnWatcherJoined;
        ActivityWatcherService.Left += OnWatcherLeft;
        ActivityWatcherService.Start();

        StartReapplyTimer();

        while (Process.GetProcessesByName("RobloxPlayerBeta").Length > 0)
            Thread.Sleep(2000);

        ActivityWatcherService.Joined -= OnWatcherJoined;
        ActivityWatcherService.Left -= OnWatcherLeft;
        ActivityWatcherService.Stop();

        StopReapplyTimer();
        Stop();
    }

    private static void OnWatcherJoined(ActivityEntry entry) => SetPlaying(entry.PlaceId);
    private static void OnWatcherLeft() => SetIdle();
}
