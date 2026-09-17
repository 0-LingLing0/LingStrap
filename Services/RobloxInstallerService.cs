using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lingstrap.Services;

public record RobloxPackage(string Name, string Md5, long CompressedSize, long UncompressedSize);

/// <summary>
/// Downloads and installs the Roblox client the same way Roblox's own installer (and every other
/// bootstrapper) does, for when no valid install exists or the installed one is outdated.
/// </summary>
public static class RobloxInstallerService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// Deletes the entire current Roblox install so the next launch does a fully fresh
    /// download+extract from scratch instead of reusing whatever's on disk - the manual, on-demand
    /// version of what InstallAsync's own on-failure cleanup does automatically, for when the
    /// install "succeeded" but Roblox still behaves as if something in it is broken (e.g. it crashes
    /// on startup for no obvious reason). Returns false if no install was found to delete.
    /// </summary>
    public static bool DeleteCurrentInstall()
    {
        if (RobloxLocator.FindPlayerExe() == null) return false;

        // Every installed version, not just the one that would launch next. A failed upgrade leaves
        // two folders behind (the new one that crashes on startup, and the older one kept as the
        // fallback), and deleting only the latter would hand the next launch the broken install with
        // nothing left to fall back to - the exact opposite of what asking for a reinstall means.
        var versionFolders = Directory.Exists(Paths.RobloxVersions)
            ? Directory.GetDirectories(Paths.RobloxVersions)
                .Where(dir => File.Exists(Path.Combine(dir, "RobloxPlayerBeta.exe")))
                .ToList()
            : new List<string>();

        if (versionFolders.Count == 0) return false;

        // Can't delete files a running client still has open.
        foreach (var proc in Process.GetProcessesByName("RobloxPlayerBeta"))
        {
            try { proc.Kill(); }
            catch (Exception ex) { Log.Warn($"Could not close a running Roblox client before reinstalling: {ex.Message}"); }
        }

        try
        {
            foreach (var folder in versionFolders)
            {
                Directory.Delete(folder, recursive: true);
                Log.Info($"Deleted Roblox install at {folder} for a clean reinstall on the next launch.");
            }

            SettingsService.Current.InstalledRobloxVersion = null;
            // Asking for a reinstall is the user explicitly saying "try the newest one again", so a
            // version parked earlier for crashing on startup gets another chance. Without this the
            // marker would outlive the install it was about and permanently cap them to an older
            // Roblox, with Reinstall silently doing nothing to change that.
            SettingsService.Current.FailedRobloxVersion = null;
            SettingsService.Save();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not delete the Roblox install for a clean reinstall", ex);
            return false;
        }
    }

    /// <summary>
    /// Package -> subfolder under the version root. Taken from Bloxstrap's own
    /// AppData/CommonAppData.cs and AppData/RobloxPlayerData.cs (the mapping every Roblox
    /// bootstrapper uses), not guessed.
    /// </summary>
    private static readonly Dictionary<string, string> PackageDirectoryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RobloxApp.zip"] = "",
        ["Libraries.zip"] = "",
        ["redist.zip"] = "",
        ["WebView2.zip"] = "",
        ["shaders.zip"] = "shaders",
        ["ssl.zip"] = "ssl",
        ["WebView2RuntimeInstaller.zip"] = "WebView2RuntimeInstaller",
        ["content-avatar.zip"] = "content/avatar",
        ["content-configs.zip"] = "content/configs",
        ["content-fonts.zip"] = "content/fonts",
        ["content-sky.zip"] = "content/sky",
        ["content-sounds.zip"] = "content/sounds",
        ["content-textures2.zip"] = "content/textures",
        ["content-models.zip"] = "content/models",
        ["content-textures3.zip"] = "PlatformContent/pc/textures",
        ["content-terrain.zip"] = "PlatformContent/pc/terrain",
        ["content-platform-fonts.zip"] = "PlatformContent/pc/fonts",
        ["content-platform-dictionaries.zip"] = "PlatformContent/pc/shared_compression_dictionaries",
        ["extracontent-luapackages.zip"] = "ExtraContent/LuaPackages",
        ["extracontent-translations.zip"] = "ExtraContent/translations",
        ["extracontent-models.zip"] = "ExtraContent/models",
        ["extracontent-textures.zip"] = "ExtraContent/textures",
        ["extracontent-places.zip"] = "ExtraContent/places",
    };

    private const string AppSettingsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
        "<Settings>\r\n" +
        "\t<ContentFolder>content</ContentFolder>\r\n" +
        "\t<BaseUrl>http://www.roblox.com</BaseUrl>\r\n" +
        "</Settings>\r\n";

    /// <summary>
    /// The version hash Roblox is currently serving for the Windows player, or null if the check
    /// failed. Uses its own short timeout rather than Http's shared 10-minute one (sized for large
    /// package downloads) - this is a single lightweight metadata request that should normally
    /// finish in well under a second, so on a slow or flaky connection it should fail fast and let
    /// the launch fall back to whatever's already installed, instead of stalling the entire launch
    /// for minutes on just the update check.
    /// </summary>
    public static async Task<string?> GetLatestVersionAsync()
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
            var json = await Http.GetStringAsync("https://clientsettings.roblox.com/v2/client-version/WindowsPlayer", cts.Token);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("clientVersionUpload").GetString();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not check the latest Roblox version: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Downloads and installs the given Roblox version fresh into %LOCALAPPDATA%\Roblox\Versions,
    /// reporting real download progress through the dialog. Throws RobloxInstallException with a
    /// specific reason on failure - never fails silently or with a generic message.
    /// </summary>
    public static async Task InstallAsync(string version, ILaunchProgressDialog dialog, CancellationToken cancellationToken = default)
    {
        var versionFolder = Path.Combine(Paths.RobloxVersions, version);
        Directory.CreateDirectory(versionFolder);

        var tempDir = Path.Combine(Path.GetTempPath(), $"Lingstrap-install-{version}");
        Directory.CreateDirectory(tempDir);

        try
        {
            dialog.SetStatus("Downloading Roblox");
            var packages = await GetManifestAsync(version);

            var totalBytes = packages.Sum(p => p.CompressedSize);
            long downloadedBytes = 0;
            var lastReportedPercent = -1;
            var downloadedFiles = new List<(RobloxPackage Package, string TempPath)>();

            foreach (var package in packages)
            {
                var tempPath = Path.Combine(tempDir, package.Name);
                var packageStart = downloadedBytes;

                await DownloadPackageAsync(version, package, tempPath, cancellationToken, bytesRead =>
                {
                    if (totalBytes <= 0) return;

                    // Only report on a whole-percent change: the raw callback fires per 80KB chunk
                    // (thousands of times for a full install), and each report is a blocking
                    // dispatcher hop that restarts the progress bar's 200ms animation, which makes
                    // the bar stutter and lag well behind the actual download.
                    var percent = (int)((double)(packageStart + bytesRead) / totalBytes * 100);
                    if (percent == lastReportedPercent) return;

                    lastReportedPercent = percent;
                    dialog.SetProgress(percent);
                });

                VerifyChecksum(package, tempPath);

                downloadedBytes += package.CompressedSize;
                downloadedFiles.Add((package, tempPath));
                Log.Info($"Downloaded {package.Name} ({package.CompressedSize} bytes) for Roblox {version}");
            }

            dialog.SetStatus("Installing");
            dialog.SetIndeterminate();

            foreach (var (package, tempPath) in downloadedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!PackageDirectoryMap.TryGetValue(package.Name, out var relativeDir))
                {
                    relativeDir = "";
                    Log.Warn($"Unknown Roblox package '{package.Name}' - extracting to the version root. " +
                             "Lingstrap's package map may need an entry for it.");
                }

                var targetDir = string.IsNullOrEmpty(relativeDir)
                    ? versionFolder
                    : Path.Combine(versionFolder, relativeDir.Replace('/', Path.DirectorySeparatorChar));

                Directory.CreateDirectory(targetDir);
                ExtractPackage(tempPath, targetDir);
                Log.Info($"Extracted {package.Name} -> {targetDir}");
            }

            File.WriteAllText(Path.Combine(versionFolder, "AppSettings.xml"), AppSettingsXml);

            SettingsService.Current.InstalledRobloxVersion = version;
            SettingsService.Save();

            Log.Info($"Installed Roblox {version} to {versionFolder}");
        }
        catch
        {
            // A package that already extracted successfully before a later one failed (e.g. the one
            // containing RobloxPlayerBeta.exe itself) would otherwise leave a version folder that
            // looks like a valid install to RobloxLocator - which only checks that the exe exists,
            // not that the whole install actually finished - and it would keep getting reused
            // indefinitely. Better to wipe it so the next launch attempt installs fresh instead.
            try { Directory.Delete(versionFolder, recursive: true); }
            catch (Exception cleanupEx) { Log.Warn($"Could not clean up failed install at {versionFolder}: {cleanupEx.Message}"); }
            throw;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) { Log.Warn($"Could not clean up install temp folder {tempDir}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Extracts a Roblox package zip by hand rather than via ZipFile.ExtractToDirectory: Roblox's
    /// own packages mark directory entries with a *leading* separator (e.g. "\" for the root,
    /// "\abilities\" for a subfolder) which look like a rooted/escaping path to .NET's built-in
    /// extractor, and it refuses the whole archive with "would have resulted in a file outside the
    /// specified destination directory" - a false positive, not a real path-traversal risk. This
    /// strips that leading separator before resolving each entry, while still verifying the
    /// resolved path lands inside targetDir, to catch a genuinely malicious entry instead of just
    /// disabling the check.
    /// </summary>
    private static void ExtractPackage(string zipPath, string targetDir)
    {
        var targetRoot = Path.GetFullPath(targetDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.TrimStart('/', '\\');
            if (relative.Length == 0) continue; // the root directory marker itself

            var destPath = Path.GetFullPath(Path.Combine(targetDir, relative));
            if (!destPath.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
                throw new RobloxInstallException(
                    $"Package {Path.GetFileName(zipPath)} contains an entry that would extract outside its target folder: {entry.FullName}");

            var isDirectoryEntry = entry.Name.Length == 0; // .NET's own convention: no file name component
            if (isDirectoryEntry)
            {
                Directory.CreateDirectory(destPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

    private static async Task<List<RobloxPackage>> GetManifestAsync(string version)
    {
        string text;
        try
        {
            var response = await Http.GetAsync($"https://setup.rbxcdn.com/{version}-rbxPkgManifest.txt");
            if (!response.IsSuccessStatusCode)
                throw new RobloxInstallException(
                    $"Could not download the Roblox package manifest for {version}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            text = await response.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException ex)
        {
            throw new RobloxInstallException($"Could not reach Roblox's CDN to get the package manifest: {ex.Message}");
        }

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
        var packages = new List<RobloxPackage>();

        // Line 0 is a format marker ("v0"); each package is then 4 lines: name, md5, compressed size, uncompressed size.
        for (var i = 1; i + 3 < lines.Count; i += 4)
        {
            var name = lines[i];
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue; // skips RobloxPlayerInstaller.exe

            if (!long.TryParse(lines[i + 2], out var compressed)) continue;
            long.TryParse(lines[i + 3], out var uncompressed);
            packages.Add(new RobloxPackage(name, lines[i + 1], compressed, uncompressed));
        }

        if (packages.Count == 0)
            throw new RobloxInstallException($"Roblox's package manifest for {version} was empty or unreadable.");

        return packages;
    }

    private static async Task DownloadPackageAsync(string version, RobloxPackage package, string destPath,
        CancellationToken cancellationToken, Action<long> onProgress)
    {
        HttpResponseMessage response;
        try
        {
            response = await Http.GetAsync($"https://setup.rbxcdn.com/{version}-{package.Name}",
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new RobloxInstallException($"Could not reach Roblox's CDN to download {package.Name}: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new RobloxInstallException($"Failed to download {package.Name}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            await using var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = File.Create(destPath);

            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await httpStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;
                onProgress(totalRead);
            }
        }
    }

    /// <summary>
    /// Checks a downloaded package against the MD5 the manifest gave for it. Roblox publishes these
    /// precisely so a truncated or garbled transfer can be caught before it's extracted - without
    /// this, a dropped connection mid-package extracted silently into a broken install that
    /// RobloxLocator still accepts as valid (it only checks that the exe exists), leaving Roblox to
    /// crash on startup for no visible reason. Not a security check - just corruption detection,
    /// which is all the manifest's MD5 is there for.
    /// </summary>
    private static void VerifyChecksum(RobloxPackage package, string path)
    {
        if (string.IsNullOrWhiteSpace(package.Md5)) return;

        string actual;
        try
        {
            using var stream = File.OpenRead(path);
            actual = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(stream));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not verify {package.Name}: {ex.Message}");
            return;
        }

        if (!actual.Equals(package.Md5, StringComparison.OrdinalIgnoreCase))
            throw new RobloxInstallException(
                $"{package.Name} downloaded incorrectly (checksum mismatch) - the connection likely dropped partway. Try launching again.");
    }
}
