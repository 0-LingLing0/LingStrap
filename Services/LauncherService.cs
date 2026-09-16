using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Lingstrap.Services;

/// <summary>Puts everything together: find the client, write settings, launch it - reporting progress as it goes.</summary>
public static class LauncherService
{
    private const string LaunchGuardMutexName = "Lingstrap_LaunchInProgress";

    /// <param name="launchUri">
    /// The full roblox-player: URI from the website, or empty for a bare launch
    /// (used when the user presses Launch from inside Lingstrap itself).
    /// </param>
    /// <param name="dialog">Where progress is reported - see LaunchProgressDialogFactory.Create().</param>
    public static async Task<bool> LaunchAsync(string launchUri, ILaunchProgressDialog dialog)
    {
        // Guards against a double-click on Launch, or double-clicking Play on the website (which
        // spawns a second, separate Lingstrap.exe via the protocol handler) - either would otherwise
        // run the whole pipeline twice at once: two overlapping mod-applies, two installs racing to
        // write the same version folder, or two RobloxPlayerBeta.exe processes starting. A named
        // mutex (rather than a plain static flag) catches both the same-process and cross-process
        // case the same way the other watcher guards elsewhere in this app already do.
        using var launchGuard = new Mutex(initiallyOwned: true, name: LaunchGuardMutexName, createdNew: out var isFirstLaunch);
        if (!isFirstLaunch)
        {
            Log.Warn("A launch is already in progress - ignoring this one.");
            dialog.ShowError("A launch is already in progress - please wait for it to finish.");
            return false;
        }

        var cancelled = false;
        Process? startedProcess = null;
        void OnCancel()
        {
            cancelled = true;
            if (startedProcess != null) TryKillLaunchingProcess(startedProcess);
        }
        dialog.CancelRequested += OnCancel;

        try
        {
            // Real, monotonically-increasing progress through the launch's own stages rather than
            // sitting on one indeterminate animation for the whole thing - the common case (Roblox
            // already up to date) never touches a real byte count anywhere, so without this the bar
            // just floats in place from start to finish instead of visibly going anywhere. If an
            // actual download turns out to be needed, EnsureInstalledAsync's own byte-accurate
            // progress takes over for that stretch instead (it dominates total launch time whenever
            // it happens), and the stage percentages simply resume once it's done.
            dialog.SetProgress(5);
            dialog.SetStatus("Checking for updates");

            var (playerExe, oldVersionFolderToRemove) = await EnsureInstalledAsync(dialog);
            if (playerExe is null) return false; // EnsureInstalledAsync already reported why

            if (cancelled) return false;

            var versionFolder = Path.GetDirectoryName(playerExe)!;

            dialog.SetProgress(40);
            dialog.SetStatus("Applying FastFlags");
            await Task.Run(() =>
            {
                if (SettingsService.Current.CloseCrashHandler)
                    KillCrashHandler();

                ClientAppSettingsWriter.Write(versionFolder, FastFlagsBuilder.BuildEffectiveFlags());

                // FramerateCap is deliberately not written here. It's editable on the Roblox
                // Settings page, so whatever is saved in GBS is what ships - Lingstrap never
                // silently overrides a value the user can see and change in that same UI.

                // Apply anything queued while a client was running - safe now, since this attempt
                // hasn't started a new RobloxPlayerBeta process yet.
                GlobalBasicSettingsService.FlushPending();
            });

            if (cancelled) return false;

            dialog.SetProgress(60);
            dialog.SetStatus("Applying mods");
            await Task.Run(() =>
            {
                // ModsService.Apply restores every mod/cursor file to Roblox's original, then copies
                // the current ones back over - correct for the first launch, but if another client is
                // already running from this exact install (multi-instance), that restore-then-reapply
                // would briefly swap out content files the running client may still have open, for no
                // benefit: they're already correctly in place from that first launch.
                if (Process.GetProcessesByName("RobloxPlayerBeta").Length == 0)
                {
                    ModsService.Apply(versionFolder);
                }
                else
                {
                    Log.Info("Skipped re-applying mods: another Roblox client is already running from this install.");
                }

                if (SettingsService.Current.MultiInstance)
                    MultiInstanceWatcher.EnsureWatcherRunning();

                if (SettingsService.Current.ForceDedicatedGpu)
                {
                    // The version folder (and so the exe path) changes hash after every Roblox
                    // update - clear old entries and set the current one fresh on every launch.
                    GpuPreferenceService.RemoveAllManaged();
                    GpuPreferenceService.Apply(playerExe);
                }
            });

            if (cancelled) return false;

            dialog.SetProgress(78);
            dialog.SetStatus("Starting Roblox");

            // Closing Roblox and pressing Play again within a second or two can make the new
            // RobloxPlayerBeta.exe exit almost immediately on its own, before its window ever
            // appears - the previous instance's singleton lock/GPU context/network session hasn't
            // finished being released by Windows yet when the new one starts checking for it. One
            // automatic retry after a short delay clears this reliably, instead of leaving the user
            // to notice the silent failure and press Play a second time themselves. A hang (timed
            // out without exiting) is treated as a real problem, not this - it doesn't retry.
            const int maxAttempts = 2;
            Process? process = null;
            var windowAppeared = false;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                process = await Task.Run(() => StartProcess(playerExe, versionFolder, launchUri));

                if (process is null)
                {
                    dialog.ShowError("Failed to start Roblox - see the log for details.");
                    return false;
                }

                startedProcess = process;
                if (cancelled)
                {
                    // Cancel arrived in the brief window between starting the process and this check -
                    // OnCancel above only kills startedProcess once it's actually assigned, so cover
                    // that gap here too rather than continuing to set everything up around a launch
                    // the user already tried to stop.
                    Log.Info("Launch was cancelled right as Roblox started - closing it.");
                    TryKillLaunchingProcess(process);
                    return false;
                }

                ApplyPriority(process);
                PowerThrottlingService.DisableThrottling(process);
                WireDiscordExit(process);

                Log.Info($"Launched Roblox from {playerExe}" + (attempt > 1 ? $" (retry {attempt - 1})" : ""));

                if (attempt == 1 && oldVersionFolderToRemove != null)
                {
                    try
                    {
                        Directory.Delete(oldVersionFolderToRemove, recursive: true);
                        Log.Info($"Removed old Roblox version folder {oldVersionFolderToRemove}");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Could not remove old Roblox version folder {oldVersionFolderToRemove}: {ex.Message}");
                    }
                }

                dialog.SetProgress(85);
                dialog.SetStatus("Waiting for Roblox to open");
                windowAppeared = await WaitForRobloxWindowAsync(process, () => cancelled, dialog);
                if (cancelled) return false;

                if (windowAppeared) break;

                if (process.HasExited && attempt < maxAttempts)
                {
                    Log.Warn("Roblox exited immediately after starting - likely closed and relaunched too quickly. Retrying once after a short delay.");
                    dialog.SetStatus("Roblox closed unexpectedly - retrying");
                    await Task.Delay(2000);
                    continue;
                }

                break;
            }

            if (!windowAppeared)
            {
                // Process.Start succeeding only means Roblox's process itself came into existence -
                // it can still crash moments later during its own startup (a corrupted install, a mod
                // conflict, anti-cheat deciding it doesn't like something) without .NET ever seeing
                // that as a launch failure. Companion apps, Discord presence and the activity watcher
                // are deliberately started below this point, not above it - starting them earlier
                // meant they'd fire even when Roblox itself never actually opened, which is exactly
                // backwards from what "companion" apps are for.
                string reason;
                if (process!.HasExited)
                {
                    reason = "Roblox's process exited on its own before its window ever appeared, twice in a row (it likely crashed on startup).";
                }
                else
                {
                    // Timed out without exiting - it's hung, not just slow. Leaving it running would
                    // be worse than the failed launch itself: GlobalBasicSettingsService treats any
                    // RobloxPlayerBeta process as "Roblox is running" and queues every settings change
                    // (FastFlags, FPS cap, everything) instead of writing it, forever, until that
                    // process is gone - so a single hung launch left alive would silently make every
                    // future settings change do nothing, with no visible error. Better to close it.
                    reason = "Roblox's window never appeared within 30 seconds - it appears to be hung, so it's being closed.";
                    TryKillLaunchingProcess(process!);
                }
                Log.Error(reason);
                dialog.ShowError($"{reason} Check the log for details.");
                return false;
            }

            CompanionAppService.OnRobloxStarted();
            DiscordPresenceService.OnRobloxStarted();
            ActivityCoordinator.OnRobloxStarted();
            FpsOverlayCoordinator.OnRobloxStarted();
            ReopenOnCloseService.OnRobloxStarted();

            dialog.SetProgress(100);
            dialog.CloseDialog(); // CloseDialog is a no-op if the user already cancelled/closed it
            return true;
        }
        finally
        {
            dialog.CancelRequested -= OnCancel;
        }
    }

    private static void TryKillLaunchingProcess(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not close the launching Roblox process after cancel: {ex.Message}");
        }
    }

    /// <summary>
    /// Makes sure a current Roblox install exists, downloading/updating it if not. Returns the
    /// player exe path (null on failure, after already reporting why to the dialog) and, if an
    /// update just replaced an older install, that older version's folder - removed only once the
    /// new one has actually launched successfully.
    /// </summary>
    private static async Task<(string? PlayerExe, string? OldVersionFolder)> EnsureInstalledAsync(ILaunchProgressDialog dialog)
    {
        var latestVersion = await RobloxInstallerService.GetLatestVersionAsync();
        var existingExe = await Task.Run(RobloxLocator.FindPlayerExe);
        var existingVersion = existingExe != null ? Path.GetFileName(Path.GetDirectoryName(existingExe)) : null;

        if (latestVersion == null)
        {
            // Couldn't check for updates - fine to keep using whatever's already installed, but
            // fatal if there's nothing to fall back on.
            if (existingExe != null) return (existingExe, null);

            var message = "Could not find a Roblox install, and couldn't reach Roblox to download one - check your connection and try again.";
            Log.Error(message);
            dialog.ShowError(message);
            return (null, null);
        }

        if (existingVersion == latestVersion)
            return (existingExe, null);

        Log.Info(existingVersion == null
            ? $"No Roblox install found - installing {latestVersion}."
            : $"Roblox install {existingVersion} is outdated - installing {latestVersion}.");

        try
        {
            await RobloxInstallerService.InstallAsync(latestVersion, dialog);
        }
        catch (RobloxInstallException ex)
        {
            Log.Error(ex.Message);
            dialog.ShowError(ex.Message);
            return (null, null);
        }
        catch (Exception ex)
        {
            var message = $"Could not install Roblox: {ex.Message}";
            Log.Error(message, ex);
            dialog.ShowError(message);
            return (null, null);
        }

        var newExe = Path.Combine(Paths.RobloxVersions, latestVersion, "RobloxPlayerBeta.exe");
        if (!File.Exists(newExe))
        {
            var message = $"Roblox {latestVersion} was downloaded, but RobloxPlayerBeta.exe wasn't where it should be afterwards.";
            Log.Error(message);
            dialog.ShowError(message);
            return (null, null);
        }

        var oldVersionFolder = existingVersion != null
            ? Path.Combine(Paths.RobloxVersions, existingVersion)
            : null;
        return (newExe, oldVersionFolder);
    }

    /// <summary>
    /// Polls for this specific launch's own window to appear, up to a generous timeout - Roblox is
    /// already running by the time this is called either way, so a timeout just stops waiting.
    /// Once found, relocates it to the preferred monitor if one is set (a no-op otherwise), and
    /// returns true. Returns false if the process exits on its own first (it crashed during startup)
    /// or the timeout elapses with no window - the caller treats either as a real launch failure
    /// rather than silently reporting success. Uses this exact Process rather than searching by name
    /// so multi-instance launches each only ever move their own window. Creeps the dialog's progress
    /// from 85 up to 98 over the wait instead of leaving it indeterminate - a cold Roblox launch can
    /// spend most of its visible time right here, so a bar that visibly keeps inching forward reads
    /// as "still working" far better than one that just floats in place the whole time.
    /// </summary>
    private static async Task<bool> WaitForRobloxWindowAsync(Process process, Func<bool> isCancelled, ILaunchProgressDialog dialog)
    {
        const double startPercent = 85, endPercent = 98, timeoutSeconds = 30;
        var start = DateTime.UtcNow;
        var deadline = start.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            if (isCancelled()) return false;

            process.Refresh();
            if (process.HasExited) return false;

            if (process.MainWindowHandle != IntPtr.Zero)
            {
                MonitorService.MoveToPreferredMonitor(process.MainWindowHandle);
                return true;
            }

            var elapsedFraction = (DateTime.UtcNow - start).TotalSeconds / timeoutSeconds;
            dialog.SetProgress(startPercent + (endPercent - startPercent) * Math.Clamp(elapsedFraction, 0, 1));

            await Task.Delay(250);
        }

        return false;
    }

    private static Process? StartProcess(string playerExe, string versionFolder, string launchUri)
    {
        var psi = new ProcessStartInfo
        {
            FileName = playerExe,
            UseShellExecute = false,
            WorkingDirectory = versionFolder,
        };
        if (!string.IsNullOrEmpty(launchUri))
            psi.ArgumentList.Add(launchUri);

        try
        {
            return Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to start RobloxPlayerBeta.exe", ex);
            return null;
        }
    }

    private static void ApplyPriority(Process process)
    {
        var priorityClass = SettingsService.Current.ProcessPriority switch
        {
            "High"        => ProcessPriorityClass.High,
            "AboveNormal" => ProcessPriorityClass.AboveNormal,
            _             => ProcessPriorityClass.Normal,
        };
        if (priorityClass == ProcessPriorityClass.Normal) return;

        try { process.PriorityClass = priorityClass; }
        catch (Exception ex) { Log.Warn($"Could not set process priority: {ex.Message}"); }
    }

    private static void WireDiscordExit(Process process)
    {
        if (!SettingsService.Current.DiscordRichPresence) return;

        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                // With multi-instance on, one client closing doesn't mean Roblox has "closed" -
                // only stop re-applying and clear the presence once every client has exited.
                if (Process.GetProcessesByName("RobloxPlayerBeta").Length == 0)
                    DiscordPresenceService.OnRobloxClosed();
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not watch for Roblox exit: {ex.Message}");
        }
    }

    private static void KillCrashHandler()
    {
        foreach (var proc in Process.GetProcessesByName("RobloxCrashHandler"))
        {
            try { proc.Kill(); }
            catch (Exception ex) { Log.Warn($"Could not close RobloxCrashHandler: {ex.Message}"); }
        }
    }
}
