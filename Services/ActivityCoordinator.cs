using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Lingstrap.Models;

namespace Lingstrap.Services;

/// <summary>Wires a detected join to geolocation, the games API, history, notifications and Discord presence.</summary>
public static class ActivityCoordinator
{
    private const string WatcherArg = "-activitywatcher";
    private const string WatcherGuardMutexName = "Lingstrap_ActivityWatcherActive";

    private static bool _started;
    private static Mutex? _watcherGuard;
    private static Timer? _exitPollTimer;

    public static void Start()
    {
        if (_started) return;
        _started = true;
        ActivityWatcherService.Joined += OnJoined;
        ActivityWatcherService.Left += OnLeft;
        ActivityWatcherService.Start();
    }

    public static void Stop()
    {
        if (!_started) return;
        _started = false;
        ActivityWatcherService.Joined -= OnJoined;
        ActivityWatcherService.Left -= OnLeft;
        ActivityWatcherService.Stop();
    }

    /// <summary>
    /// Call once Roblox's client process has started. If this window is staying open for the whole
    /// session, Start() (already called from MainWindow.StartBackgroundServices) covers it - joins
    /// keep being detected right here. But when CloseLingstrapOnLaunch means this process is about
    /// to exit (the common case - it exits within a second or two of Roblox's window appearing,
    /// long before the player actually finishes loading into a server), this window's own
    /// ActivityCoordinator dies before the join it exists to catch ever happens. That's why the
    /// overlay banner never showed: DiscordPresenceService and CompanionAppService already spawn a
    /// detached watcher process for exactly this reason - this is the same fix, for this feature.
    /// </summary>
    public static void OnRobloxStarted()
    {
        if (!SettingsService.Current.ShowServerLocation) return;
        if (!SettingsService.Current.CloseLingstrapOnLaunch) return;

        SpawnWatcher();
    }

    private static void SpawnWatcher()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                Log.Warn("Could not resolve own exe path - activity watcher not started.");
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
            Log.Warn($"Could not start activity watcher: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs on the detached watcher process (see App.xaml.cs). Reuses the exact same Start() this
    /// window itself uses - including the overlay banner - so behaviour is identical either way.
    /// Unlike DiscordPresenceService's and CompanionAppService's watchers, this one does NOT block:
    /// OverlayBannerWindow is a real WPF window and needs an actual Dispatcher pumping messages to
    /// render, so this just wires everything up and lets App.xaml.cs's normal startup continue on
    /// into WPF's own message loop (ShutdownMode="OnExplicitShutdown" keeps the app alive with no
    /// MainWindow until the timer below shuts it down). Returns false if this process should exit
    /// immediately instead - e.g. another activity watcher is already running.
    /// </summary>
    public static bool StartDetachedWatcher()
    {
        SettingsService.Load();

        _watcherGuard = new Mutex(initiallyOwned: true, name: WatcherGuardMutexName, createdNew: out var isFirst);
        if (!isFirst)
        {
            Log.Info("An activity watcher is already running - exiting immediately.");
            return false;
        }

        Start();

        _exitPollTimer = new Timer(_ =>
        {
            if (Process.GetProcessesByName("RobloxPlayerBeta").Length > 0) return;

            _exitPollTimer?.Dispose();
            Stop();
            System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
        }, null, 2000, 2000);

        return true;
    }

    private static void OnLeft()
    {
        if (SettingsService.Current.DiscordRichPresence)
            DiscordPresenceService.SetIdle();
    }

    private static async void OnJoined(ActivityEntry entry)
    {
        ActivityHistoryService.Add(entry);

        if (SettingsService.Current.DiscordRichPresence)
            DiscordPresenceService.SetPlaying(entry.PlaceId);

        var geo = await GeoLocationService.LookupAsync(entry.ServerIp);
        if (geo is not null)
        {
            ActivityHistoryService.UpdateByJobId(entry.JobId, e =>
            {
                e.City = geo.City;
                e.Region = geo.Region;
                e.Country = geo.Country;
            });
        }

        var s = SettingsService.Current;
        Log.Info($"Join handled: ShowServerLocation={s.ShowServerLocation} ShowServerNotification={s.ShowServerNotification}");

        if (!s.ShowServerNotification)
        {
            Log.Info("Overlay banner skipped: \"Show an overlay banner on join\" is off.");
            return;
        }

        // On a cold launch, Roblox's log can report the join several seconds before its window
        // actually exists - trying the lookup once right here would just fail every time. Give it
        // some room to show up instead of skipping the banner outright.
        var windowDeadline = DateTime.UtcNow.AddSeconds(15);
        while (!RobloxWindowLocator.HasAnyReadyClientWindow() && DateTime.UtcNow < windowDeadline)
            await Task.Delay(250);

        // Re-read the enriched entry so the overlay reflects the geo lookup if it succeeded.
        var history = ActivityHistoryService.Load();
        var latest = history.Find(e => e.JobId == entry.JobId) ?? entry;
        var locationText = latest.BuildLocationText();
        System.Windows.Application.Current.Dispatcher.Invoke(() => Views.OverlayBannerWindow.TryShow(locationText, entry.SourceLogFile));
    }
}
