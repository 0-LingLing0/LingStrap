using System;
using Microsoft.Win32;

namespace Lingstrap.Services;

/// <summary>Registers Lingstrap as the handler for roblox-player: launch links from the website.</summary>
public static class ProtocolHandlerService
{
    private const string ProtocolKey = @"Software\Classes\roblox-player";

    public static void Register()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            Log.Warn("Could not resolve Lingstrap's own exe path, skipping protocol registration.");
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(ProtocolKey);
        key.SetValue("", "URL:Roblox Protocol");
        key.SetValue("URL Protocol", "");

        using (var iconKey = key.CreateSubKey("DefaultIcon"))
            iconKey.SetValue("", $"{exePath},0");

        using (var commandKey = key.CreateSubKey(@"shell\open\command"))
            commandKey.SetValue("", $"\"{exePath}\" \"%1\"");

        RemoveLauncherAppRegistration();

        Log.Info("Registered as the roblox-player: protocol handler.");
    }

    /// <summary>
    /// v0.2.9's "Set as Roblox launcher" button registered Lingstrap as a choosable app for Roblox
    /// links (a ProgId, Capabilities and a RegisteredApplications entry). The button is gone, so clear
    /// what it left behind rather than keep listing a Windows "Default apps" entry nothing maintains.
    /// </summary>
    private static void RemoveLauncherAppRegistration()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Lingstrap.RobloxPlayer", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Lingstrap\Capabilities", throwOnMissingSubKey: false);
            using var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true);
            registered?.DeleteValue("Lingstrap", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Info($"Could not remove the old launcher app registration: {ex.Message}");
        }
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(ProtocolKey, throwOnMissingSubKey: false);
        Log.Info("Unregistered the roblox-player: protocol handler.");
    }

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ProtocolKey);
        return key is not null;
    }
}
