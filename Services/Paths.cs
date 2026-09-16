using System;
using System.IO;

namespace Lingstrap.Services;

/// <summary>Every path Lingstrap writes to lives here, so nothing hardcodes a folder.</summary>
public static class Paths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lingstrap");

    public static string Logs     => Path.Combine(Root, "Logs");
    public static string Mods     => Path.Combine(Root, "Mods");       // textures, fonts, sounds (not cursors)
    public static string Cursors  => Path.Combine(Root, "Cursors");    // processed cursor slot images
    public static string Presets  => Path.Combine(Root, "Presets");    // user-made preset files
    public static string Versions => Path.Combine(Root, "Versions");   // Roblox installs

    public static string SettingsFile => Path.Combine(Root, "Settings.json");
    public static string NetworkOptimizationBackupFile => Path.Combine(Root, "NetworkOptimizationBackup.json");

    /// <summary>Where the real Roblox client (installed by Roblox's own installer) lives.</summary>
    public static string RobloxRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox");

    public static string RobloxVersions => Path.Combine(RobloxRoot, "Versions");
    public static string RobloxLogs     => Path.Combine(RobloxRoot, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Mods);
        Directory.CreateDirectory(Cursors);
        Directory.CreateDirectory(Presets);
        Directory.CreateDirectory(Versions);
    }
}
