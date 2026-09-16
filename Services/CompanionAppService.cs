using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Lingstrap.Models;

namespace Lingstrap.Services;

/// <summary>
/// Launches companion apps once a Roblox client has started, and closes whatever it started once
/// every Roblox client has exited. Runs in a detached background process (the same pattern as
/// MultiInstanceWatcher) so it keeps working even when CloseLingstrapOnLaunch exits the main app
/// the moment Roblox's window appears - never touches a process it didn't itself start.
/// </summary>
public static class CompanionAppService
{
    private const string WatcherGuardMutexName = "Lingstrap_CompanionWatcherActive";
    private const string WatcherArg = "-companionwatcher";

    /// <summary>
    /// Call once a Roblox client process has started. Starts a detached watcher that launches the
    /// enabled companion apps and closes them once every Roblox client has exited. A no-op if one
    /// is already running (multi-instance starting another client mid-session) - the existing
    /// watcher already covers it, since it watches for any RobloxPlayerBeta process by name.
    /// </summary>
    public static void OnRobloxStarted()
    {
        if (!SettingsService.Current.ManageCompanionApps) return;
        if (!SettingsService.Current.CompanionApps.Any(c => c.Enabled)) return;

        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                Log.Warn("Could not resolve own exe path - companion apps not started.");
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
            Log.Warn($"Could not start companion app watcher: {ex.Message}");
        }
    }

    /// <summary>Runs on the detached watcher process. Blocks until every Roblox client has closed.</summary>
    public static void RunWatcherAndBlock()
    {
        SettingsService.Load();

        using var guard = new Mutex(initiallyOwned: true, name: WatcherGuardMutexName, createdNew: out var isFirst);
        if (!isFirst)
        {
            Log.Info("A companion app watcher is already running - exiting immediately.");
            return;
        }

        try
        {
            var launched = new List<(int Pid, CompanionApp Config)>();

            foreach (var entry in SettingsService.Current.CompanionApps.Where(c => c.Enabled))
            {
                if (entry.StartupDelaySeconds > 0)
                    Thread.Sleep(TimeSpan.FromSeconds(entry.StartupDelaySeconds));

                var pid = LaunchOne(entry);
                if (pid is { } id) launched.Add((id, entry));
            }

            while (Process.GetProcessesByName("RobloxPlayerBeta").Length > 0)
                Thread.Sleep(2000);

            CloseAllTracked(launched);
        }
        catch (Exception ex)
        {
            Log.Error("Companion app watcher failed", ex);
        }
    }

    private static int? LaunchOne(CompanionApp entry)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = entry.ExePath,
                Arguments = entry.Arguments ?? "",
                UseShellExecute = entry.RunAsAdmin,
            };
            if (entry.RunAsAdmin) psi.Verb = "runas";

            var process = Process.Start(psi);
            if (process is null)
            {
                Log.Warn($"Companion app '{entry.Name}' did not start.");
                return null;
            }

            Log.Info($"Companion app started: {entry.Name} (PID {process.Id})");
            return process.Id;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not start companion app '{entry.Name}' ({entry.ExePath})", ex);
            return null;
        }
    }

    private static void CloseAllTracked(List<(int Pid, CompanionApp Config)> launched)
    {
        foreach (var (pid, config) in launched.Where(t => !t.Config.DontAutoClose))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) continue;

                Log.Info($"Closing companion app: {config.Name} (PID {pid})");
                process.CloseMainWindow();

                if (!process.WaitForExit(5000))
                {
                    Log.Warn($"Companion app '{config.Name}' did not close gracefully - force killing.");
                    process.Kill();
                }
            }
            catch (ArgumentException)
            {
                // Already exited on its own - nothing to do.
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not close companion app '{config.Name}': {ex.Message}");
            }
        }
    }
}
