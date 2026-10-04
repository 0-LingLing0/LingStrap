using System;
using System.IO;
using System.Reflection;

namespace Lingstrap.Services;

/// <summary>
/// Creates the "launch Roblox" shortcuts: Lingstrap started with <see cref="LaunchArg"/>, which goes
/// straight into a launch (loading screen, settings, mods, Roblox) instead of opening the main window -
/// the same thing other bootstrappers' Roblox shortcuts do.
/// </summary>
public static class ShortcutService
{
    /// <summary>Start Roblox immediately. Same name Bloxstrap uses, so it reads familiar.</summary>
    public const string LaunchArg = "-player";

    public const string LaunchShortcutName = "Roblox (Lingstrap)";

    /// <summary>Desktop and Start menu. True if at least one was created.</summary>
    public static bool CreateLaunchShortcuts()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            Log.Warn("Could not resolve own exe path - no launch shortcut created.");
            return false;
        }

        var desktop = TryCreate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), LaunchShortcutName + ".lnk"), exePath);
        var startMenu = TryCreate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", LaunchShortcutName + ".lnk"), exePath);
        return desktop || startMenu;
    }

    private static bool TryCreate(string shortcutPath, string exePath)
    {
        try
        {
            // WScript.Shell through late binding, the same way LingstrapSetup makes its shortcuts -
            // avoids pulling in a COM interop reference for one call.
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable.");
            var shell = Activator.CreateInstance(shellType)!;
            var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath })!;
            var shortcutType = shortcut.GetType();

            void Set(string property, string value) =>
                shortcutType.InvokeMember(property, BindingFlags.SetProperty, null, shortcut, new object[] { value });

            Set("TargetPath", exePath);
            Set("Arguments", LaunchArg);
            Set("WorkingDirectory", Path.GetDirectoryName(exePath)!);
            Set("IconLocation", exePath);
            Set("Description", "Launch Roblox through Lingstrap");
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);

            Log.Info($"Created launch shortcut: {shortcutPath}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not create launch shortcut {shortcutPath}: {ex.Message}");
            return false;
        }
    }
}
