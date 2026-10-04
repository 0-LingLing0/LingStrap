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
            iconKey.SetValue("", $"{handler},0"); // some tools read the launcher's path from here too

        using (var commandKey = key.CreateSubKey(@"shell\open\command"))
            commandKey.SetValue("", $"\"{handler}\" \"%1\"");

        Log.Info("Registered as the roblox-player: protocol handler.");
    }

    private const string AppName = "Lingstrap";
    private const string ProgId = "Lingstrap.RobloxPlayer";
    private const string CapabilitiesKey = @"Software\Lingstrap\Capabilities";

    /// <summary>
    /// The "Set as Roblox launcher" button. Beyond Register(), makes Lingstrap a real choosable app
    /// for roblox-player links - listed in Windows' "open with" prompt and Settings > Default apps,
    /// like a browser - and clears a choice Windows remembered from such a prompt before, which
    /// overrides the plain registration and is what some tools read to find "the launcher".
    /// Returns the launcher file that ended up registered.
    /// </summary>
    public static string? SetAsRobloxLauncher()
    {
        Register();
        var launcher = ShortcutService.EnsureLauncherCopy() ?? Environment.ProcessPath;
        if (launcher == null) return null;

        using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            prog.SetValue("", "Roblox (Lingstrap)");
            prog.SetValue("URL Protocol", "");
            using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue("", $"{launcher},0");
            using (var cmd = prog.CreateSubKey(@"shell\open\command")) cmd.SetValue("", $"\"{launcher}\" \"%1\"");
        }

        using (var caps = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
        {
            caps.SetValue("ApplicationName", AppName);
            caps.SetValue("ApplicationDescription", "Roblox bootstrapper");
            using var urls = caps.CreateSubKey("URLAssociations");
            urls.SetValue("roblox-player", ProgId);
        }

        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            registered.SetValue(AppName, CapabilitiesKey);

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\roblox-player\UserChoice", throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            // Windows protects this key on some machines - the Default apps page opened afterwards
            // lets the user make the same choice by hand.
            Log.Info($"Could not clear the remembered roblox-player choice: {ex.Message}");
        }

        Log.Info($"Set {launcher} as the Roblox launcher.");
        return launcher;
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
