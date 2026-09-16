using System.IO;
using System.Linq;

namespace Lingstrap.Services;

/// <summary>Finds the Roblox client that Roblox's own installer already put on this machine.</summary>
public static class RobloxLocator
{
    private const string PlayerExeName = "RobloxPlayerBeta.exe";

    /// <summary>
    /// Full path to RobloxPlayerBeta.exe for the version Lingstrap last installed, or - if that's not
    /// tracked yet or no longer exists (e.g. a fresh Settings.json, or the folder was removed by
    /// hand) - whichever installed version's exe has the newest file-modified time. The tracked
    /// version is preferred because a leftover, undeleted old version folder could otherwise have a
    /// coincidentally newer timestamp than the real current one (e.g. touched by antivirus scanning)
    /// and get picked by mistake.
    /// </summary>
    public static string? FindPlayerExe()
    {
        var tracked = SettingsService.Current.InstalledRobloxVersion;
        if (!string.IsNullOrEmpty(tracked))
        {
            var trackedExe = Path.Combine(Paths.RobloxVersions, tracked, PlayerExeName);
            if (File.Exists(trackedExe)) return trackedExe;
        }

        if (!Directory.Exists(Paths.RobloxVersions)) return null;

        return Directory.GetDirectories(Paths.RobloxVersions)
            .Select(dir => Path.Combine(dir, PlayerExeName))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>The version folder (e.g. .../Versions/version-abc123) containing the client, or null.</summary>
    public static string? FindVersionFolder()
    {
        var exe = FindPlayerExe();
        return exe is null ? null : Path.GetDirectoryName(exe);
    }
}
