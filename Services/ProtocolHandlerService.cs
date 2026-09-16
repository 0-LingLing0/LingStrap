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

        Log.Info("Registered as the roblox-player: protocol handler.");
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
