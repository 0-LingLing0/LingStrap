using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Lingstrap.Services;

/// <summary>
/// "Let other devices go first": a Windows QoS policy that marks all Roblox traffic as background
/// (DSCP 8, "CS1" - the class meant for bulk and background traffic), so a router that honours DSCP
/// sends other devices' packets first and a farm of accounts doesn't make everyone else's ping spike.
/// It only helps when the router reads DSCP; plenty of simple ones don't.
///
/// Needs administrator rights: the policy and the setting below are machine-wide, so the work runs in
/// an elevated copy of Lingstrap (-farmnetwork on / off, together with NetworkOptimizationService)
/// and leaves a marker file for the normal app to read the state from.
/// </summary>
public static class RobloxQosService
{
    public const string PolicyName = "Lingstrap - Roblox as background traffic";
    private const int BackgroundDscp = 8;

    // On a PC that isn't in a domain, Windows only applies locally configured DSCP marking when this
    // is set - without it the policy exists but nothing gets marked.
    private const string NlaKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\QoS";
    private const string NlaValue = "Do not use NLA";

    private static string MarkerFile => Path.Combine(Paths.Root, "qos-on.txt");
    private static string NlaBackupFile => Path.Combine(Paths.Root, "qos-nla-backup.txt");

    public static bool IsOn => File.Exists(MarkerFile);

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
            Log.Info("QoS: administrator elevation was declined.");
            return false;
        }
    }

    /// <summary>Runs inside the elevated helper.</summary>
    public static void ApplyElevated(bool on)
    {
        try
        {
            if (on)
            {
                using (var key = Registry.LocalMachine.CreateSubKey(NlaKey))
                {
                    if (!File.Exists(NlaBackupFile))
                        File.WriteAllText(NlaBackupFile, key.GetValue(NlaValue) as string ?? "<missing>");
                    key.SetValue(NlaValue, "1", RegistryValueKind.String);
                }

                RunPowerShell($"Remove-NetQosPolicy -Name '{PolicyName}' -Confirm:$false -ErrorAction SilentlyContinue; " +
                              $"New-NetQosPolicy -Name '{PolicyName}' -AppPathNameMatchCondition 'RobloxPlayerBeta.exe' " +
                              $"-DSCPAction {BackgroundDscp} -NetworkProfile All | Out-Null");
                File.WriteAllText(MarkerFile, DateTime.Now.ToString("O"));
                Log.Info("QoS: Roblox traffic is now marked as background (DSCP 8).");
            }
            else
            {
                RunPowerShell($"Remove-NetQosPolicy -Name '{PolicyName}' -Confirm:$false -ErrorAction SilentlyContinue");

                if (File.Exists(NlaBackupFile))
                {
                    var original = File.ReadAllText(NlaBackupFile);
                    using var key = Registry.LocalMachine.CreateSubKey(NlaKey);
                    if (original == "<missing>") key.DeleteValue(NlaValue, throwOnMissingValue: false);
                    else key.SetValue(NlaValue, original, RegistryValueKind.String);
                    File.Delete(NlaBackupFile);
                }

                File.Delete(MarkerFile);
                Log.Info("QoS: Roblox traffic marking removed - everything is as it was.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("QoS: could not change the Roblox traffic policy", ex);
        }
    }

    private static void RunPowerShell(string command)
    {
        using var ps = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", command },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        })!;
        var error = ps.StandardError.ReadToEnd();
        ps.WaitForExit(30000);
        if (!string.IsNullOrWhiteSpace(error)) Log.Warn($"QoS: PowerShell said: {error.Trim()}");
    }
}
