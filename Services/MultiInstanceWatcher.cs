using System;
using System.Diagnostics;
using System.Threading;

namespace Lingstrap.Services;

/// <summary>
/// Keeps Roblox from closing older clients when a new one starts, for as long as clients are running.
///
/// Roblox stays at one window per session two ways, and both have to be beaten:
/// - ROBLOX_singletonEvent: every client waits on it, and a new client signals it, which makes every
///   older client shut down. This watcher takes that name first with an object that ISN'T an event
///   (a semaphore), so Roblox can't create the event at all - it logs "Cannot create event to secure
///   single process" and runs normally without it.
/// - "NoReload": a new client looks for one already running from the same exe path and hands its
///   launch over to it, then exits - the older client switches to the new game instead. That's
///   handled per launch by giving each extra client its own path (MultiInstancePaths).
///
/// This used to hold ROBLOX_singletonMutex and an event instead - the trick from before Roblox added
/// either of the above, which stopped working once it did.
///
/// Runs as a detached process (this app relaunched with -multiinstancewatcher) so it outlives
/// CloseLingstrapOnLaunch, and lets the name go once every client in the session has closed.
/// </summary>
public static class MultiInstanceWatcher
{
    private const string WatcherGuardMutexName = "Lingstrap_MultiInstanceWatcherActive";

    /// <summary>Whether this session's singleton event name is held by Lingstrap's placeholder -
    /// i.e. multi-instance is armed for the next client that starts.</summary>
    public static bool IsArmed()
    {
        try
        {
            using var held = Semaphore.OpenExisting(SingletonEventService.EventName);
            return true;
        }
        catch
        {
            return false; // doesn't exist, or exists as Roblox's real event
        }
    }

    /// <summary>Runs on the watcher process. Blocks until every Roblox client has closed.</summary>
    public static void RunAndBlock()
    {
        Log.Info("Multi-instance watcher started.");
        try
        {
            using var guard = new Mutex(initiallyOwned: true, name: WatcherGuardMutexName, createdNew: out var isFirst);
            if (!isFirst)
            {
                Log.Info("A multi-instance watcher is already running - exiting immediately.");
                return;
            }

            using var placeholder = TakeSingletonEventName();
            if (placeholder == null) return;

            // Spawned BEFORE the launch that's waiting on it starts RobloxPlayerBeta.exe - so wait for
            // a client to actually appear first, then for them all to close.
            var startDeadline = DateTime.UtcNow.AddSeconds(30);
            while (RobloxProcesses.Count() == 0 && DateTime.UtcNow < startDeadline)
                Thread.Sleep(500);

            while (RobloxProcesses.Count() > 0)
                Thread.Sleep(3000);
        }
        catch (Exception ex)
        {
            Log.Error("Multi-instance watcher failed", ex);
        }
        Log.Info("Multi-instance watcher exiting - all Roblox clients closed.");
    }

    /// <summary>
    /// Creates the placeholder under ROBLOX_singletonEvent. If a client already made the real event
    /// (it started without this watcher - opened outside Lingstrap, or before multi-instance was on),
    /// that event is released inside the clients holding it first; they keep running.
    /// </summary>
    private static Semaphore? TakeSingletonEventName()
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return new Semaphore(0, 1, SingletonEventService.EventName);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The name exists as something else - Roblox's real event.
                var holders = SingletonEventService.FindHolders();
                Log.Info($"Multi-instance: Roblox's single-window signal is held by PID {string.Join(", ", holders)} - releasing it.");
                SingletonEventService.Release(holders);
                Thread.Sleep(200);
            }
        }

        Log.Warn("Multi-instance: couldn't take over Roblox's single-window signal - new clients will close older ones until every Roblox is closed.");
        return null;
    }

    /// <summary>
    /// Launches the detached watcher and waits briefly for it to be armed. Never throws and never
    /// blocks a launch for long - if it doesn't work out, Roblox just runs single-instance.
    /// </summary>
    public static void EnsureWatcherRunning()
    {
        try
        {
            if (IsArmed()) return;

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                Log.Warn("Could not resolve own exe path - multi-instance watcher not started.");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "-multiinstancewatcher",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            for (var i = 0; i < 40; i++)
            {
                if (IsArmed()) return;
                Thread.Sleep(100);
            }

            Log.Warn("Multi-instance watcher did not become ready in time - launching as single instance.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not start multi-instance watcher, falling back to single instance: {ex.Message}");
        }
    }
}
