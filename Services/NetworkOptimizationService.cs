using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using Lingstrap.Models;
using Microsoft.Win32;

namespace Lingstrap.Services;

/// <summary>
/// System-wide network tweaks aimed at lowering in-game ping - not scoped to just Roblox, since none
/// of these have a per-process equivalent the way CPU throttling did. All three require
/// administrator rights (registry under HKLM, and device power-management state), so this only ever
/// runs inside the elevated "-networkoptimize"/"-networkrestore" helper process (see App.xaml.cs),
/// never in the normal interactive one. Every change is backed up to disk before it's made, so
/// RestoreAll() can put the machine back exactly how it was rather than guessing at a generic default.
/// </summary>
public static class NetworkOptimizationService
{
    public static bool HasBackup() => File.Exists(Paths.NetworkOptimizationBackupFile);

    /// <summary>Runs every optimization, logging exactly what was found and changed on this machine (results vary by hardware/drivers).</summary>
    public static void RunAll()
    {
        var backup = new NetworkOptimizationBackup();

        DisableAdapterPowerSaving(backup);
        DisableNetworkThrottling(backup);
        DisableLatencyHurtingAdapterProperties(backup);
        DisableQosReservedBandwidth(backup);
        DisableDeliveryOptimizationPeering(backup);

        var changedAnything = backup.AdapterPower.Count > 0 || backup.AdapterParams.Count > 0
            || backup.NetworkThrottling != null || backup.QosReservedBandwidth != null || backup.DeliveryOptimization != null;
        if (!changedAnything)
        {
            Log.Warn("Network optimization made no changes at all (likely not running elevated) - not writing a backup, since there'd be nothing to restore.");
            return;
        }

        try
        {
            Paths.EnsureCreated();
            File.WriteAllText(Paths.NetworkOptimizationBackupFile,
                JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save the network optimization backup - Restore won't be able to undo this run: {ex.Message}");
        }
    }

    /// <summary>Puts back exactly what RunAll's backup recorded, then deletes the backup file so the
    /// UI knows there's nothing left to restore.</summary>
    public static void RestoreAll()
    {
        NetworkOptimizationBackup backup;
        try
        {
            var json = File.ReadAllText(Paths.NetworkOptimizationBackupFile);
            backup = JsonSerializer.Deserialize<NetworkOptimizationBackup>(json)
                ?? throw new InvalidOperationException("Backup file was empty.");
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read the network optimization backup - nothing was restored: {ex.Message}");
            return;
        }

        var failures = 0;
        failures += RestoreAdapterPowerSaving(backup);
        if (!RestoreRegistryValue(backup.NetworkThrottling,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex",
                "network packet throttle")) failures++;
        if (!RestoreRegistryValue(backup.QosReservedBandwidth,
                @"SOFTWARE\Policies\Microsoft\Windows\Psched", "NonBestEffortLimit",
                "QoS reserved bandwidth limit")) failures++;
        if (!RestoreRegistryValue(backup.DeliveryOptimization,
                @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode",
                "Delivery Optimization peering")) failures++;
        failures += RestoreAdapterParams(backup);

        if (failures > 0)
        {
            // Deliberately NOT deleting the backup on a partial failure (e.g. this somehow ran
            // without full elevation, or a device disappeared since the backup was taken) - the user's
            // only way back to their original settings is this file, so keeping it means Restore can
            // just be run again rather than leaving some settings stuck in the optimized state forever.
            Log.Warn($"Restored most settings, but {failures} step(s) failed - see the warnings above. The backup was kept so Restore can be run again.");
            return;
        }

        try
        {
            File.Delete(Paths.NetworkOptimizationBackupFile);
        }
        catch (Exception ex)
        {
            Log.Warn($"Restored the settings, but couldn't remove the old backup file: {ex.Message}");
        }

        Log.Info("Network optimization changes restored to what they were before.");
    }

    /// <summary>
    /// Unchecks "Allow the computer to turn off this device to save power" for every physical,
    /// connected network adapter. A common, silent source of extra latency (and occasional
    /// reconnects) especially on laptops - Windows can partially suspend the adapter to save battery,
    /// same idea as the per-process CPU throttling this app already disables for Roblox, just for the
    /// network adapter instead (there's no per-process equivalent for this one - it's a property of
    /// the physical device itself).
    /// </summary>
    private static void DisableAdapterPowerSaving(NetworkOptimizationBackup backup)
    {
        try
        {
            var pnpIds = new List<string>();
            using (var adapters = new ManagementObjectSearcher("root\\CIMV2",
                       "SELECT PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = True AND NetConnectionStatus = 2"))
            {
                foreach (ManagementObject adapter in adapters.Get())
                {
                    if (adapter["PNPDeviceID"] is string id && !string.IsNullOrEmpty(id))
                        pnpIds.Add(id);
                }
            }

            if (pnpIds.Count == 0)
            {
                Log.Info("Adapter power saving: no connected physical network adapters found.");
                return;
            }

            var changed = 0;
            var matched = 0;
            using (var powerDevices = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM MSPower_DeviceEnable"))
            {
                foreach (ManagementObject device in powerDevices.Get())
                {
                    var instanceName = device["InstanceName"] as string ?? "";
                    if (!pnpIds.Any(id => instanceName.StartsWith(id, StringComparison.OrdinalIgnoreCase))) continue;
                    matched++;

                    var originalEnable = device["Enable"] is bool b && b;
                    try
                    {
                        // Only recorded once the write actually succeeds - a failed Put() (e.g. not
                        // elevated) must not leave a backup entry claiming a change that never
                        // happened, or Restore would think there's something to undo when there isn't.
                        device["Enable"] = false;
                        device.Put();
                        backup.AdapterPower.Add(new AdapterPowerBackup { InstanceName = instanceName, OriginalEnable = originalEnable });
                        changed++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Could not disable power saving for adapter {instanceName}: {ex.Message}");
                    }
                }
            }

            if (changed > 0)
                Log.Info($"Adapter power saving: disabled on {changed} network adapter(s).");
            else if (matched > 0)
                Log.Info("Adapter power saving: found matching adapter(s) but couldn't change them - see the warning above.");
            else
                Log.Info("Adapter power saving: found connected adapters but no matching power-management entries for them - nothing to change.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not disable network adapter power saving: {ex.Message}");
        }
    }

    /// <summary>Returns how many of the backed-up adapters could not be restored.</summary>
    private static int RestoreAdapterPowerSaving(NetworkOptimizationBackup backup)
    {
        if (backup.AdapterPower.Count == 0) return 0;

        var remaining = new List<AdapterPowerBackup>(backup.AdapterPower);
        try
        {
            var restored = 0;
            using var powerDevices = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM MSPower_DeviceEnable");
            foreach (ManagementObject device in powerDevices.Get())
            {
                var instanceName = device["InstanceName"] as string ?? "";
                var entry = backup.AdapterPower.FirstOrDefault(a => a.InstanceName == instanceName);
                if (entry is null) continue;

                try
                {
                    device["Enable"] = entry.OriginalEnable;
                    device.Put();
                    remaining.Remove(entry);
                    restored++;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not restore power saving for adapter {instanceName}: {ex.Message}");
                }
            }
            Log.Info($"Adapter power saving: restored on {restored} network adapter(s).");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not restore network adapter power saving: {ex.Message}");
        }
        return remaining.Count;
    }

    /// <summary>
    /// Removes Windows' own default cap on non-multimedia network packet processing (MMCSS,
    /// originally added to protect audio/video playback from being starved by heavy network
    /// traffic) - by default limited to 10,000 packets/sec regardless of how fast the connection
    /// actually is. A well-documented, reversible registry tweak with real reported latency
    /// improvements in competitive games, most noticeable on faster connections. System-wide: this
    /// changes how Windows schedules network traffic for every app, not just Roblox.
    /// </summary>
    private static void DisableNetworkThrottling(NetworkOptimizationBackup backup)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", writable: true);
            var original = ReadBackup(key, "NetworkThrottlingIndex");
            key.SetValue("NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord);
            backup.NetworkThrottling = original;
            Log.Info("Disabled Windows' MMCSS network packet throttle (NetworkThrottlingIndex).");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not disable network throttling: {ex.Message}");
        }
    }

    /// <summary>
    /// Best-effort: every network adapter driver exposes its own "Advanced Properties" (the ones
    /// under the adapter's Properties > Advanced tab in Device Manager), with property names that
    /// differ per vendor - there's no standard API for "turn off Energy-Efficient Ethernet" the way
    /// there is for the power-saving checkbox above. This searches every adapter's own advertised
    /// property descriptions for wording matching known latency-hurting features (Energy-Efficient
    /// Ethernet/Green Ethernet, Interrupt Moderation) and switches whichever ones it actually finds
    /// to their driver's own "Disabled" option - so it adapts to whatever this specific machine's
    /// hardware calls things, rather than guessing a fixed property name that only some vendors use.
    /// Logs exactly what it found and changed, since results genuinely vary by hardware.
    /// </summary>
    private static void DisableLatencyHurtingAdapterProperties(NetworkOptimizationBackup backup)
    {
        const string ClassRoot = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
        string[] targetPatterns = { "energy efficient ethernet", "energy-efficient ethernet", "green ethernet", "interrupt moderation" };

        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(ClassRoot);
            if (classKey is null)
            {
                Log.Warn("Adapter advanced properties: could not open the network adapter driver class key.");
                return;
            }

            var changed = 0;
            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                if (!subKeyName.All(char.IsDigit)) continue; // only numbered adapter instances, skip "Properties" etc.

                using var adapterKey = classKey.OpenSubKey(subKeyName, writable: true);
                using var paramsKey = adapterKey?.OpenSubKey(@"Ndi\Params");
                if (paramsKey is null) continue;

                foreach (var paramName in paramsKey.GetSubKeyNames())
                {
                    using var paramKey = paramsKey.OpenSubKey(paramName);
                    var desc = paramKey?.GetValue("ParamDesc") as string ?? "";
                    if (!targetPatterns.Any(p => desc.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;

                    var disabledValue = FindDisabledEnumValue(paramKey);
                    if (disabledValue is null)
                    {
                        Log.Info($"Adapter advanced properties: found '{desc}' but couldn't find its 'Disabled' option - left as-is.");
                        continue;
                    }

                    var originalValue = adapterKey!.GetValue(paramName) as string ?? "";
                    try
                    {
                        // Same rule as the adapter power-saving backup above: only record this once
                        // the write actually goes through, so a failed SetValue doesn't leave a
                        // backup entry for a change that was never really made.
                        adapterKey.SetValue(paramName, disabledValue);
                        backup.AdapterParams.Add(new AdapterParamBackup
                        {
                            SubKeyName = subKeyName,
                            ParamName = paramName,
                            OriginalValue = originalValue,
                            Description = desc,
                        });
                        Log.Info($"Adapter advanced properties: disabled '{desc}'.");
                        changed++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Could not disable adapter property '{desc}': {ex.Message}");
                    }
                }
            }

            if (changed == 0)
                Log.Info("Adapter advanced properties: none of the known latency-hurting settings (Energy-Efficient Ethernet, Interrupt Moderation, etc.) were found on this machine's network drivers.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not adjust network adapter advanced properties: {ex.Message}");
        }
    }

    /// <summary>Returns how many of the backed-up adapter properties could not be restored.</summary>
    private static int RestoreAdapterParams(NetworkOptimizationBackup backup)
    {
        if (backup.AdapterParams.Count == 0) return 0;

        const string ClassRoot = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
        var restored = 0;
        var failed = 0;

        foreach (var entry in backup.AdapterParams)
        {
            try
            {
                using var adapterKey = Registry.LocalMachine.OpenSubKey(
                    $@"{ClassRoot}\{entry.SubKeyName}", writable: true);
                if (adapterKey is null)
                {
                    failed++;
                    continue;
                }

                adapterKey.SetValue(entry.ParamName, entry.OriginalValue);
                Log.Info($"Adapter advanced properties: restored '{entry.Description}'.");
                restored++;
            }
            catch (Exception ex)
            {
                failed++;
                Log.Warn($"Could not restore adapter property '{entry.Description}': {ex.Message}");
            }
        }

        if (restored == 0) Log.Info("Adapter advanced properties: could not restore any of them.");
        return failed;
    }

    /// <summary>
    /// The QoS Packet Scheduler reserves a slice of bandwidth (20% by default) for QoS-aware traffic
    /// that in practice almost nothing on a home PC ever claims - it's a separate, older reservation
    /// from the MMCSS packet-count throttle above, and some network setups still enforce it. Setting
    /// the reservable limit to 0% frees that slice up as ordinary best-effort bandwidth. Same
    /// Group-Policy-backed registry value Microsoft documents for scripting this centrally.
    /// </summary>
    private static void DisableQosReservedBandwidth(NetworkOptimizationBackup backup)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows\Psched", writable: true);
            var original = ReadBackup(key, "NonBestEffortLimit");
            key.SetValue("NonBestEffortLimit", 0, RegistryValueKind.DWord);
            backup.QosReservedBandwidth = original;
            Log.Info("Disabled the QoS Packet Scheduler's reserved bandwidth limit (NonBestEffortLimit=0).");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not disable QoS reserved bandwidth: {ex.Message}");
        }
    }

    /// <summary>
    /// Windows Update's Delivery Optimization can upload update chunks to (and download them from)
    /// other PCs in the background - same idea as torrenting Windows updates. That background
    /// upload/download traffic competes for the same connection a game is using and can cause ping
    /// spikes or jitter mid-session. Setting DODownloadMode to 0 (HTTP only) turns off the
    /// peer-to-peer side entirely - equivalent to switching off "Allow downloads from other PCs" in
    /// Settings, just via the same Group-Policy-backed value Microsoft documents for scripting it.
    /// </summary>
    private static void DisableDeliveryOptimizationPeering(NetworkOptimizationBackup backup)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", writable: true);
            var original = ReadBackup(key, "DODownloadMode");
            key.SetValue("DODownloadMode", 0, RegistryValueKind.DWord);
            backup.DeliveryOptimization = original;
            Log.Info("Disabled Windows Update's peer-to-peer Delivery Optimization (DODownloadMode=0, HTTP only).");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not disable Delivery Optimization peering: {ex.Message}");
        }
    }

    private static RegistryValueBackup ReadBackup(RegistryKey key, string valueName)
    {
        var existing = key.GetValue(valueName);
        return existing is int i
            ? new RegistryValueBackup { Existed = true, OriginalValue = i }
            : new RegistryValueBackup { Existed = false };
    }

    /// <summary>Returns true if there was nothing to do or the restore succeeded; false only on a
    /// genuine failure to write back a value that was actually backed up.</summary>
    private static bool RestoreRegistryValue(RegistryValueBackup? backup, string keyPath, string valueName, string label)
    {
        if (backup is null) return true;

        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(keyPath, writable: true);
            if (backup.Existed)
            {
                key.SetValue(valueName, backup.OriginalValue, RegistryValueKind.DWord);
                Log.Info($"Restored {label} to its previous value.");
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
                Log.Info($"Restored {label} by removing the value Lingstrap added (it didn't exist before).");
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not restore {label}: {ex.Message}");
            return false;
        }
    }

    private static string? FindDisabledEnumValue(RegistryKey? paramKey)
    {
        using var enumKey = paramKey?.OpenSubKey("Enum");
        if (enumKey is null) return null;

        foreach (var valueName in enumKey.GetValueNames())
        {
            if (enumKey.GetValue(valueName) is string label &&
                (label.Equals("Disabled", StringComparison.OrdinalIgnoreCase) || label.Equals("Off", StringComparison.OrdinalIgnoreCase)))
            {
                return valueName;
            }
        }
        return null;
    }
}
