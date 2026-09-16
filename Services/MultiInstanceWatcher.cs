using System;
using System.Diagnostics;
using System.Threading;

namespace Lingstrap.Services;

/// <summary>
/// Roblox refuses to start a second copy unless these two named kernel objects already exist
/// when it checks. A detached watcher process (this app relaunched with -multiinstancewatcher)
/// creates and holds them until every Roblox process has exited, then lets them go.
/// </summary>
public static class MultiInstanceWatcher
{
    private const string MutexName = "ROBLOX_singletonMutex";
    private const string EventName = "ROBLOX_singletonEvent";
    private const string WatcherGuardMutexName = "Lingstrap_MultiInstanceWatcherActive";

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

            using var mutex = new Mutex(initiallyOwned: true, name: MutexName);
            using var evt = new EventWaitHandle(initialState: true, mode: EventResetMode.ManualReset, name: EventName);

            // This watcher is spawned (from EnsureWatcherRunning, below) BEFORE the launch that's
            // waiting on it actually starts RobloxPlayerBeta.exe - checking "is Roblox still
            // running" immediately here would see zero processes, since none has started yet, and
            // fall straight through: the mutex/event get disposed within milliseconds of being
            // created, right as EnsureWatcherRunning is still polling for them. Wait for at least
            // one Roblox process to actually appear first, THEN watch for them all to close.
            var startDeadline = DateTime.UtcNow.AddSeconds(30);
            while (Process.GetProcessesByName("RobloxPlayerBeta").Length == 0 && DateTime.UtcNow < startDeadline)
                Thread.Sleep(500);

            while (Process.GetProcessesByName("RobloxPlayerBeta").Length > 0)
                Thread.Sleep(3000);
        }
        catch (Exception ex)
        {
            Log.Error("Multi-instance watcher failed", ex);
        }
        Log.Info("Multi-instance watcher exiting - all Roblox clients closed.");
    }

    /// <summary>
    /// Launches the detached watcher and waits briefly for it to be ready. Never throws and
    /// never blocks a launch for long - if it doesn't work out, Roblox just runs single-instance.
    /// </summary>
    public static void EnsureWatcherRunning()
    {
        try
        {
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

            for (var i = 0; i < 20; i++)
            {
                if (Mutex.TryOpenExisting(MutexName, out var handle))
                {
                    handle.Dispose();
                    return;
                }
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
