using System;
using Microsoft.Win32;

namespace Lingstrap.Services;

/// <summary>
/// Tells Windows to run RobloxPlayerBeta.exe on the high-performance (dedicated) GPU via the same
/// per-app registry key the Windows Settings "Graphics" page writes to.
/// </summary>
public static class GpuPreferenceService
{
    private const string RegistryPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    public static void Apply(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue(exePath, "GpuPreference=2;", RegistryValueKind.String);
            Log.Info($"Set high-performance GPU preference for {exePath}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not set GPU preference: {ex.Message}");
        }
    }

    /// <summary>Removes every entry Lingstrap has ever set under Roblox's Versions folder - old
    /// version hashes linger there after an update otherwise.</summary>
    public static void RemoveAllManaged()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
            if (key is null) return;

            foreach (var name in key.GetValueNames())
            {
                if (name.StartsWith(Paths.RobloxVersions, StringComparison.OrdinalIgnoreCase))
                    key.DeleteValue(name, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not remove GPU preference entries: {ex.Message}");
        }
    }
}
