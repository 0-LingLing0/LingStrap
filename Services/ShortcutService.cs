using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Lingstrap.Services;

/// <summary>
/// Creates the "launch Roblox" shortcuts. They don't point at Lingstrap.exe itself but at
/// <see cref="LauncherExeName"/>, a copy of it whose only job is to start Lingstrap.exe with
/// <see cref="LaunchArg"/>. A plain "Lingstrap.exe -player" shortcut works when double-clicked, but
/// programs that start Roblox from a shortcut - macros like Revolution Macro - take only the target
/// path out of it and drop the arguments, which opened Lingstrap's main window instead of Roblox. A
/// file that launches Roblox with no arguments at all can't lose them.
///
/// The copy forwards to Lingstrap.exe rather than running the launch itself, so it never needs
/// updating: an update only replaces Lingstrap.exe, and the copy keeps handing over to the new one.
/// </summary>
public static class ShortcutService
{
    /// <summary>Start Roblox immediately. Same name Bloxstrap uses, so it reads familiar.</summary>
    public const string LaunchArg = "-player";

    public const string LaunchShortcutName = "Roblox (Lingstrap)";

    private const string LauncherExeName = "LingstrapPlayer.exe";
    private const string MainExeName = "Lingstrap.exe";

    /// <summary>
    /// If this process is the launcher copy, starts Lingstrap.exe next to it - passing on whatever it
    /// was given (a roblox-player: link, say), or <see cref="LaunchArg"/> when it was given nothing -
    /// and returns true so the caller exits. False for the normal Lingstrap.exe.
    /// </summary>
    public static bool ForwardIfLauncherCopy(string[] args)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) ||
            !Path.GetFileName(exePath).Equals(LauncherExeName, StringComparison.OrdinalIgnoreCase))
            return false;

        var mainExe = Path.Combine(Path.GetDirectoryName(exePath)!, MainExeName);
        // An empty argument still means "no link" - it has to launch Roblox, not reach Lingstrap.exe
        // as an argument it doesn't recognise and open the main window after all.
        var real = args.Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
        var forwarded = real.Length > 0 ? real : new[] { LaunchArg };

        try
        {
            var psi = new ProcessStartInfo { FileName = mainExe, UseShellExecute = false };
            foreach (var arg in forwarded) psi.ArgumentList.Add(arg);
            Process.Start(psi);
            Log.Info($"Launcher copy: handed over to {MainExeName} with [{string.Join(' ', forwarded)}].");
        }
        catch (Exception ex)
        {
            Log.Error($"Launcher copy could not start {mainExe}", ex);
        }

        return true;
    }

    /// <summary>
    /// The launcher copy next to Lingstrap.exe, created - or refreshed after an update - when needed.
    /// Null if it doesn't exist and couldn't be made. Only call from Lingstrap.exe itself.
    /// </summary>
    public static string? EnsureLauncherCopy()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return null;

        var launcher = Path.Combine(Path.GetDirectoryName(exePath)!, LauncherExeName);
        if (Path.GetFullPath(exePath).Equals(Path.GetFullPath(launcher), StringComparison.OrdinalIgnoreCase)) return launcher;

        // Forwarding never changes, so an old copy works fine - refreshing it just keeps it from
        // looking out of date in Explorer, and costs a 17 MB copy only once per update.
        if (File.Exists(launcher) &&
            FileVersionInfo.GetVersionInfo(launcher).FileVersion == FileVersionInfo.GetVersionInfo(exePath).FileVersion)
            return launcher;

        try
        {
            File.Copy(exePath, launcher, overwrite: true);
            Log.Info($"Created {LauncherExeName}.");
        }
        catch (Exception ex) when (File.Exists(launcher))
        {
            // In use right now (a launch is going through it) - the existing copy forwards just as well.
            Log.Info($"Kept the existing {LauncherExeName}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not create {LauncherExeName}: {ex.Message}");
            return null;
        }

        return launcher;
    }

    /// <summary>Desktop and Start menu. True if at least one was created.</summary>
    public static bool CreateLaunchShortcuts()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            Log.Warn("Could not resolve own exe path - no launch shortcut created.");
            return false;
        }

        var launcher = EnsureLauncherCopy();
        if (launcher == null) return false;

        var results = new[]
        {
            TryCreate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), LaunchShortcutName + ".lnk"), launcher, exePath),
            TryCreate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", LaunchShortcutName + ".lnk"), launcher, exePath),
        };
        return results.Any(ok => ok);
    }

    private static bool TryCreate(string shortcutPath, string target, string iconSource)
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

            Set("TargetPath", target);
            Set("Arguments", ""); // nothing to lose - see the class remarks
            Set("WorkingDirectory", Path.GetDirectoryName(target)!);
            Set("IconLocation", iconSource);
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
