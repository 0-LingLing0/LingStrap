using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Lingstrap.Models;

namespace Lingstrap.Services;

/// <summary>
/// Shows a small FPS/ping chip over the Roblox window while it's running. FPS needs its own
/// separate, elevated process regardless of CloseLingstrapOnLaunch - RobloxFpsTracker's real-time
/// ETW session requires administrator rights, and the main Lingstrap process never runs elevated
/// (see app.manifest) - so that path goes through FpsWatcherTaskService's scheduled task rather than
/// a plain "runas" launch, specifically so this doesn't mean a fresh UAC prompt on every single
/// Roblox launch. Ping (RobloxPingTracker, a plain ICMP echo) needs no admin rights at all, so a
/// ping-only overlay spawns the same detached watcher process directly instead, skipping the whole
/// elevation dance.
/// </summary>
public static class FpsOverlayCoordinator
{
    private const string WatcherGuardMutexName = "Lingstrap_FpsOverlayWatcherActive";

    private static Mutex? _watcherGuard;
    private static RobloxFpsTracker? _tracker;
    private static RobloxPingTracker? _pingTracker;
    private static Views.DxFpsOverlayWindow? _overlay;
    private static Timer? _exitPollTimer;
    private static volatile bool _uiUpdatePending;
    private static double _latestFps;

    private static class Native
    {
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    }

    /// <summary>Call once Roblox's client process has started. Trusts the cached
    /// FpsWatcherTaskConfirmed flag rather than re-querying "schtasks /query" here every single
    /// launch - that extra process spawn was adding real time to every launch for a fact that's
    /// already known once the task has been created successfully. Falls back to one real check (and
    /// caches the result if it passes) for anyone whose task already existed from before this flag
    /// existed at all, so upgrading doesn't silently turn the overlay off for them.</summary>
    public static void OnRobloxStarted()
    {
        var showFps = SettingsService.Current.ShowFpsOverlay;
        var showPing = SettingsService.Current.ShowPingOverlay;
        if (!showFps && !showPing) return;

        if (!showFps)
        {
            // Ping alone needs no elevation - just start the same detached watcher process directly,
            // the way the other unelevated watchers (activitywatcher, discordwatcher) already do.
            SpawnUnelevatedWatcher();
            return;
        }

        if (!SettingsService.Current.FpsWatcherTaskConfirmed)
        {
            if (!FpsWatcherTaskService.TaskExists())
            {
                Log.Warn("FPS overlay: scheduled task isn't set up yet - skipping this launch. Toggle " +
                         "\"Show FPS while Roblox is running\" off and back on from the Behaviour page to set it up.");
                return;
            }

            SettingsService.Current.FpsWatcherTaskConfirmed = true;
            SettingsService.Save();
        }

        FpsWatcherTaskService.RunTask();
    }

    private static void SpawnUnelevatedWatcher()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            Log.Warn("Ping overlay: could not resolve own exe path - not showing.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exePath, "-fpswatcher") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Warn($"Ping overlay: could not start the watcher process: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs on the detached, elevated watcher process (see App.xaml.cs). Finds Roblox's window,
    /// shows the chip, starts the ETW tracker, and shuts itself down once every Roblox client has
    /// closed - the same overall shape as ActivityCoordinator.StartDetachedWatcher. Returns false if
    /// this process should exit immediately instead (another instance is already running, or
    /// Roblox's window never showed up).
    /// </summary>
    public static bool StartDetachedWatcher()
    {
        SettingsService.Load();

        var showFps = SettingsService.Current.ShowFpsOverlay;
        var showPing = SettingsService.Current.ShowPingOverlay;
        if (!showFps && !showPing)
        {
            Log.Info("FPS/ping overlay watcher started with both stats off - exiting immediately.");
            return false;
        }

        _watcherGuard = new Mutex(initiallyOwned: true, name: WatcherGuardMutexName, createdNew: out var isFirst);
        if (!isFirst)
        {
            Log.Info("An FPS overlay watcher is already running - exiting immediately.");
            return false;
        }

        // Without this, Windows classifies this whole process (unfocused, no user input, no window
        // the user is actively interacting with) as background work and puts it into EcoQoS
        // ("Efficiency Mode") - which doesn't just slow down CPU-bound work, it throttles the
        // process's own message pump, so BeginInvoke'd UI updates back up and only actually get
        // drained roughly once a second regardless of how often RobloxFpsTracker computes a fresh
        // value. This is the exact same throttling PowerThrottlingService already exists to disable
        // for Roblox itself - the fpswatcher process needs the same opt-out for its own UI thread.
        PowerThrottlingService.DisableThrottling(Process.GetCurrentProcess());

        // Subscribing before Start() matters: this process can take a couple of seconds to spin up
        // (schtasks activation for the elevated FPS case, or a plain Process.Start for ping-only),
        // during which the join can already complete - subscribing only once ShowOverlay ran (after
        // the window-find loop too) meant that join's event fired to zero listeners and got lost for
        // the rest of the session. TryGetLastKnownServerIp then covers the other side of the same
        // race: a join that finished even before Start() itself began tailing (Start() deliberately
        // skips a log's existing content, so it alone would never surface that join at all).
        if (showPing)
        {
            ActivityWatcherService.Joined += OnJoined;
            ActivityWatcherService.Start();
            var catchUpIp = ActivityWatcherService.TryGetLastKnownServerIp();
            Log.Info($"Ping overlay: catch-up scan found {(catchUpIp ?? "nothing")}.");
            if (catchUpIp is { } ip) StartPingTracker(ip);
        }

        (Rect Rect, IntPtr Hwnd)? found = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (found == null && DateTime.UtcNow < deadline)
        {
            found = RobloxWindowLocator.FindClientRectForLogFile(null);
            if (found == null) Thread.Sleep(250);
        }

        if (found is not { } f)
        {
            Log.Warn("FPS overlay: Roblox's window could not be found - not showing.");
            return false;
        }

        ShowOverlay(f.Hwnd, f.Rect, showFps, showPing);

        _exitPollTimer = new Timer(_ =>
        {
            if (Process.GetProcessesByName("RobloxPlayerBeta").Length > 0) return;

            _exitPollTimer?.Dispose();
            _tracker?.Stop();
            _pingTracker?.Stop();
            if (showPing) ActivityWatcherService.Stop();
            Application.Current.Dispatcher.Invoke(() =>
            {
                _overlay?.Dispose();
                Application.Current.Shutdown();
            });
        }, null, 2000, 2000);

        return true;
    }

    private static void ShowOverlay(IntPtr hwnd, Rect rect, bool showFps, bool showPing)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);

        _overlay = new Views.DxFpsOverlayWindow(hwnd, showFps, showPing);
        // Physical pixels, not the device-independent `rect` passed in (that one's fine for the log
        // line it came from, but this raw Win32 window positions itself via SetWindowPos, which
        // expects real screen pixels - feeding it DIU values undershoots on any scaled display.
        _overlay.Reposition(RobloxWindowLocator.GetClientRectPhysicalQuiet(hwnd) ?? rect);
        _overlay.StartFollowing();

        if (showFps)
        {
            _tracker = new RobloxFpsTracker((int)pid);
            // RobloxFpsTracker reports up to 60 times a second, from a background thread. Queuing
            // every single one onto the UI thread with BeginInvoke let them pile up faster than the
            // dispatcher could drain them, so the chip kept showing an increasingly stale, laggy
            // number instead of "what's happening right now" - Roblox's own counter never falls
            // behind like that since it draws directly on its own render thread. Coalescing to "at
            // most one update in flight, always carrying the latest value" means a queued-up backlog
            // can never build - the UI thread always ends up painting whatever the newest number
            // was, dropping any older ones in between instead of laboriously catching up through all
            // of them.
            _tracker.FpsUpdated += fps =>
            {
                _latestFps = fps;
                if (_uiUpdatePending) return;
                _uiUpdatePending = true;
                // Plain Normal priority, not Render - Render-priority work only actually runs right
                // before WPF renders a frame, and WPF backs off rendering entirely once nothing's
                // invalidating the display. Since the whole point of this callback IS what would
                // invalidate the display (the text changing), queuing it at Render priority meant it
                // could sit waiting for a render tick that had no reason to happen, which is almost
                // certainly why this stalled to about once every half second instead of running
                // continuously - Normal priority just runs as soon as the dispatcher's message queue
                // gets to it, with no dependency on the render pipeline's own idle/wake behavior.
                Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    _uiUpdatePending = false;
                    _overlay?.UpdateFps(_latestFps);
                }));
            };
            _tracker.Start();
        }

        // The Joined subscription and any catch-up call already happened back in
        // StartDetachedWatcher, before this method ever ran - nothing else to wire up here for ping.
    }

    private static void OnJoined(ActivityEntry entry)
    {
        try
        {
            Log.Info($"Ping overlay: join event received, ip={entry.ServerIp}");
            if (!string.IsNullOrEmpty(entry.ServerIp)) StartPingTracker(entry.ServerIp);
        }
        catch (Exception ex)
        {
            // This runs synchronously inside ActivityWatcherService's own Joined?.Invoke, under its
            // FilesLock - an uncaught exception here would otherwise surface as a generic "could not
            // read <file>" warning from PollFile's own try/catch, hiding what actually went wrong.
            Log.Warn($"Ping overlay: OnJoined threw: {ex}");
        }
    }

    /// <summary>(Re)targets the ping tracker at a server IP - called for the very first server this
    /// process becomes aware of (either the live Joined event or the TryGetLastKnownServerIp catch-up)
    /// and again every time the player changes servers afterwards.</summary>
    private static void StartPingTracker(string serverIp)
    {
        Log.Info($"Ping overlay: tracking server {serverIp} (overlay ready: {_overlay != null}).");
        _pingTracker?.Stop();
        _pingTracker = new RobloxPingTracker(serverIp);
        // A plain 1/sec ICMP echo - no risk of the FPS tracker's pile-up problem, so this just
        // marshals straight to the UI thread on every update instead of needing to coalesce.
        _pingTracker.PingUpdated += ms => Application.Current.Dispatcher.BeginInvoke(
            DispatcherPriority.Normal, new Action(() =>
            {
                if (_overlay is null) Log.Warn("Ping overlay: update arrived with no overlay window yet.");
                _overlay?.UpdatePing(ms);
            }));
        _pingTracker.Start();
    }
}
