using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Lingstrap.Services;

/// <summary>
/// Runs Farming > "Network for farming" elevated (-farmnetwork on / off), and cleans up after the
/// Roblox traffic marking v0.2.16 briefly shipped: a Windows QoS policy that tagged Roblox traffic as
/// background (DSCP 8) so routers would let other devices go first. It was dropped because on a
/// router that honours it, a busy connection made the farming accounts wait - a risk to keeping them
/// connected, which matters more. Every Optimize and Restore now removes it if it's still there.
/// </summary>
public static class RobloxQosService
{
    private const string PolicyName = "Lingstrap - Roblox as background traffic";
    private const string NlaKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\QoS";
    private const string NlaValue = "Do not use NLA";

    private static string MarkerFile => Path.Combine(Paths.Root, "qos-on.txt");
    private static string NlaBackupFile => Path.Combine(Paths.Root, "qos-nla-backup.txt");

    /// <summary>Whether the old marking is still installed on this PC.</summary>
    public static bool LegacyMarkingPresent => File.Exists(MarkerFile);

    /// <summary>Starts the elevated helper and waits for it. False if the UAC prompt was declined.</summary>
    public static bool RunElevated(bool on)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = on ? "-farmnetwork on" : "-farmnetwork off",
                UseShellExecute = true,
                Verb = "runas",
            });
            process?.WaitForExit();
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Log.Info("Network for farming: administrator elevation was declined.");
            return false;
        }
    }

    /// <summary>Runs inside the elevated helper: removes the old marking and what it changed.</summary>
    public static void RemoveLegacyMarking()
    {
        if (!LegacyMarkingPresent) return;
        try
        {
            using (var ps = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command",
                    $"Remove-NetQosPolicy -Name '{PolicyName}' -Confirm:$false -ErrorAction SilentlyContinue" },
                UseShellExecute = false,
                CreateNoWindow = true,
            }))
            {
                ps?.WaitForExit(30000);
            }

            if (File.Exists(NlaBackupFile))
            {
                var original = File.ReadAllText(NlaBackupFile);
                using var key = Registry.LocalMachine.CreateSubKey(NlaKey);
                if (original == "<missing>") key.DeleteValue(NlaValue, throwOnMissingValue: false);
                else key.SetValue(NlaValue, original, RegistryValueKind.String);
                File.Delete(NlaBackupFile);
            }

            File.Delete(MarkerFile);
            Log.Info("Network for farming: removed the old Roblox background-traffic marking.");
        }
        catch (Exception ex)
        {
            Log.Error("Network for farming: could not remove the old Roblox traffic marking", ex);
        }
    }
}
