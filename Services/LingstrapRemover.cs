using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Lingstrap.Services;

/// <summary>
/// Removes Lingstrap from this PC: puts back what it changed in Windows and Roblox, removes its
/// shortcuts and registry entries, then deletes its whole folder once this process has exited.
/// Roblox itself is left installed.
/// </summary>
public static class LingstrapRemover
{
    /// <summary>Started elevated by <see cref="RemoveEverything"/> for the parts that need admin.</summary>
    public const string ElevatedArg = "-removeelevated";

    private const string FpsTaskName = "LingstrapFpsWatcher";

    /// <summary>Everything. Ends with Lingstrap's folder queued for deletion - the caller must exit.</summary>
    public static void RemoveEverything()
    {
        Log.Info("Removing Lingstrap from this PC.");

        // Admin-only: network adapter changes and the FPS overlay's elevated scheduled task. Only asks
        // (one UAC prompt) when there's actually something there; declining just skips these.
        if (NetworkOptimizationService.HasBackup() || FpsWatcherTaskService.TaskExists())
            RunElevatedPart();

        Try("Game Bar recording", () =>
        {
            if (SettingsService.Current.DisableGameDvr || SettingsService.Current.GameDvrBackup.Count > 0)
                GameDvrService.Apply(turnOff: false);
        });
        Try("GPU preference entries", GpuPreferenceService.RemoveAllManaged);
        Try("Roblox settings lock", () =>
        {
            GlobalBasicSettingsService.ApplyLock(false);
            DeleteFile(GlobalBasicSettingsService.FilePath + ".lingstrap-backup");
        });
        Try("Roblox FastFlags", RemoveFastFlagFiles);
        Try("Roblox link handler", RemoveProtocolHandler);
        Try("shortcuts", RemoveShortcuts);

        DeleteFolderAfterExit();
    }

    /// <summary>The admin part, run in its own elevated process.</summary>
    public static void RunElevated()
    {
        Try("network changes", () =>
        {
            if (NetworkOptimizationService.HasBackup()) NetworkOptimizationService.RestoreAll();
            RobloxQosService.RemoveLegacyMarking();
        });
        Try("FPS overlay task", () => RunHidden("schtasks.exe", $"/delete /tn \"{FpsTaskName}\" /f", wait: true));
    }

    private static void RunElevatedPart()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, ElevatedArg) { UseShellExecute = true, Verb = "runas" });
            p?.WaitForExit(60_000);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Log.Info("Administrator approval declined - network changes and the FPS task were left as they are.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not run the admin part of the removal: {ex.Message}");
        }
    }

    /// <summary>The FastFlags file Lingstrap wrote into each Roblox version folder.</summary>
    private static void RemoveFastFlagFiles()
    {
        if (!Directory.Exists(Paths.RobloxVersions)) return;
        foreach (var version in Directory.GetDirectories(Paths.RobloxVersions))
            DeleteFile(Path.Combine(version, "ClientSettings", "ClientAppSettings.json"));
    }

    /// <summary>Only while it still points at Lingstrap - if PhasmaStrap or Roblox took it over
    /// already, it's theirs and stays.</summary>
    private static void RemoveProtocolHandler()
    {
        using (var command = Registry.CurrentUser.OpenSubKey(@"Software\Classes\roblox-player\shell\open\command"))
        {
            var value = command?.GetValue("") as string;
            if (value != null && value.Contains("Lingstrap", StringComparison.OrdinalIgnoreCase))
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\roblox-player", throwOnMissingSubKey: false);
        }

        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Lingstrap.RobloxPlayer", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Lingstrap", throwOnMissingSubKey: false);
        using var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true);
        registered?.DeleteValue("Lingstrap", throwOnMissingValue: false);
    }

    private static void RemoveShortcuts()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        foreach (var folder in new[] { desktop, programs })
        {
            DeleteFile(Path.Combine(folder, "Lingstrap.lnk"));
            DeleteFile(Path.Combine(folder, ShortcutService.LaunchShortcutName + ".lnk"));
        }
    }

    /// <summary>A running exe can't delete itself, so a hidden cmd waits for this process to exit
    /// and then removes the folder (the exe, the launcher copy, settings, logs, mods, everything).</summary>
    private static void DeleteFolderAfterExit()
    {
        Log.Info("Lingstrap's folder will be deleted once it has closed.");
        var pid = Environment.ProcessId;
        var script =
            $"/c (for /l %i in (1,1,30) do (tasklist /fi \"PID eq {pid}\" | find \"{pid}\" >nul && timeout /t 1 /nobreak >nul)) " +
            $"& rmdir /s /q \"{Paths.Root}\"";
        RunHidden("cmd.exe", script, wait: false);
    }

    private static void RunHidden(string file, string args, bool wait)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true });
        if (wait) p?.WaitForExit(30_000);
    }

    private static void DeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    private static void Try(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Warn($"Removal: could not undo {what}: {ex.Message}"); }
    }
}
