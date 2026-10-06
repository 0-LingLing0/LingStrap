using System;
using Microsoft.Win32;

namespace Lingstrap.Services;

/// <summary>
/// Switches Xbox Game Bar's background recording off for this Windows user, and back on. With it on,
/// Windows hooks into every game window to be ready to record the last few minutes - per Roblox
/// window, which adds up with many accounts open. Per-user (HKCU), no admin needed; the values found
/// first are kept and put back exactly when it's switched on again.
/// </summary>
public static class GameDvrService
{
    private static readonly (string Key, string Value)[] Values =
    {
        (@"System\GameConfigStore", "GameDVR_Enabled"),
        (@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"),
    };

    public static void Apply(bool turnOff)
    {
        var s = SettingsService.Current;
        try
        {
            if (turnOff)
            {
                if (s.GameDvrBackup.Count == 0)
                {
                    foreach (var (key, value) in Values)
                    {
                        using var k = Registry.CurrentUser.OpenSubKey(key);
                        s.GameDvrBackup[$"{key}|{value}"] = k?.GetValue(value) is int current ? current : null;
                    }
                }

                foreach (var (key, value) in Values)
                {
                    using var k = Registry.CurrentUser.CreateSubKey(key);
                    k.SetValue(value, 0, RegistryValueKind.DWord);
                }
                Log.Info("Xbox Game Bar background recording turned off.");
            }
            else
            {
                foreach (var (key, value) in Values)
                {
                    using var k = Registry.CurrentUser.CreateSubKey(key);
                    if (s.GameDvrBackup.TryGetValue($"{key}|{value}", out var original) && original is { } v)
                        k.SetValue(value, v, RegistryValueKind.DWord);
                    else
                        k.DeleteValue(value, throwOnMissingValue: false);
                }
                s.GameDvrBackup.Clear();
                Log.Info("Xbox Game Bar background recording restored.");
            }

            s.DisableGameDvr = turnOff;
            SettingsService.Save();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not change Xbox Game Bar recording: {ex.Message}");
        }
    }
}
