using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Lingstrap.Services;

/// <summary>
/// Decides what happens once every Roblox client has closed, for the cases where nothing would
/// otherwise happen on its own: optionally brings a Lingstrap window back up (the companion to
/// "Close Lingstrap once Roblox starts", and to launching Roblox from the browser, which never
/// shows a window at all while it's running - see App.HandleRobloxLaunch); and if reopening wasn't
/// requested and there's still no window showing, exits instead of lingering forever as an
/// invisible process with nothing left to do (it was only ever running to keep Roblox's background
/// services - Discord presence, server-info tracking - going while Roblox was open).
/// </summary>
public static class ReopenOnCloseService
{
    private const string WatcherArg = "-reopenwatcher";
    private const string WatcherGuardMutexName = "Lingstrap_ReopenWatcherActive";

    private static DispatcherTimer? _inProcessPollTimer;

    /// <summary>Call once Roblox's client process has started.</summary>
    public static void OnRobloxStarted()
    {
        if (SettingsService.Current.CloseLingstrapOnLaunch)
        {
            // This process is exiting on its own already - only worth a detached watcher if
            // reopening was actually requested (otherwise there's nothing left to do, the process
            // exiting already ends up in the same "nothing running" state the auto-exit below
            // reaches the other way).
            if (SettingsService.Current.ReopenLingstrapOnRobloxClose)
                SpawnWatcher();
            return;
        }

        StartInProcessPoll();
    }

    private static void SpawnWatcher()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                Log.Warn("Could not resolve own exe path - reopen-on-close watcher not started.");
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
            Log.Warn($"Could not start reopen-on-close watcher: {ex.Message}");
        }
    }

    /// <summary>Runs on the detached watcher process (see App.xaml.cs) - waits for every Roblox
    /// client to close, then launches a normal, visible Lingstrap instance and exits. A no-op if
    /// one is already running (multi-instance starting another client mid-session).</summary>
    public static void RunWatcherAndBlock()
    {
        using var guard = new Mutex(initiallyOwned: true, name: WatcherGuardMutexName, createdNew: out var isFirst);
        if (!isFirst)
        {
            Log.Info("A reopen-on-close watcher is already running - exiting immediately.");
            return;
        }

        while (Process.GetProcessesByName("RobloxPlayerBeta").Length > 0)
            Thread.Sleep(2000);

        LaunchFreshLingstrap();
    }

    /// <summary>
    /// When this same process is staying alive already (CloseLingstrapOnLaunch is off - either the
    /// normal main window is open, or nothing is showing at all because Roblox was launched from
    /// the browser), there's no separate process to spawn - just poll in the background for Roblox
    /// closing. If a window is already open (the normal case), neither branch below does anything -
    /// it's already exactly where it should be. Only matters when nothing is showing: then either a
    /// window comes back (if reopening was requested) or, since there's nothing left for an
    /// invisible process to be doing, this process exits.
    /// </summary>
    private static void StartInProcessPoll()
    {
        if (_inProcessPollTimer != null) return; // already watching from an earlier launch this session

        _inProcessPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _inProcessPollTimer.Tick += (_, _) =>
        {
            if (Process.GetProcessesByName("RobloxPlayerBeta").Length > 0) return;

            _inProcessPollTimer!.Stop();
            _inProcessPollTimer = null;

            if (Application.Current.Windows.Count > 0) return; // already showing - nothing to do

            if (SettingsService.Current.ReopenLingstrapOnRobloxClose)
            {
                var window = new MainWindow();
                window.Show();
            }
            else
            {
                Application.Current.Shutdown();
            }
        };
        _inProcessPollTimer.Start();
    }

    private static void LaunchFreshLingstrap()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                Log.Warn("Could not resolve own exe path - could not reopen Lingstrap after Roblox closed.");
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not reopen Lingstrap after Roblox closed: {ex.Message}");
        }
    }
}
