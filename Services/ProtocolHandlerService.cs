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

        // Registered to the launcher copy rather than Lingstrap.exe. Macros (Revolution Macro, for
        // one) find "the Roblox launcher" by reading this command and run that exe with no
        // arguments - which for Lingstrap.exe means opening the settings window instead of Roblox.
        // The copy launches Roblox when given nothing and passes a website link on unchanged.
        var handler = ShortcutService.EnsureLauncherCopy() ?? exePath;

        using var key = Registry.CurrentUser.CreateSubKey(ProtocolKey);
        key.SetValue("", "URL:Roblox Protocol");
        key.SetValue("URL Protocol", "");

        using (var iconKey = key.CreateSubKey("DefaultIcon"))
            iconKey.SetValue("", $"{exePath},0");

        using (var commandKey = key.CreateSubKey(@"shell\open\command"))
            commandKey.SetValue("", $"\"{handler}\" \"%1\"");

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
