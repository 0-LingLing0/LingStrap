using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Lingstrap.Setup;

/// <summary>
/// Downloads and installs the latest published Lingstrap release from GitHub - the whole point of
/// this tiny setup exe is that it's the only thing that needs to be sent around; everything else is
/// fetched fresh from GitHub Releases, the same way Lingstrap itself bootstraps a Roblox install.
///
/// This class never hardcodes a version - it always asks GitHub for whatever is newest at run time,
/// so a single build of LingstrapSetup.exe keeps working for every future Lingstrap release forever.
/// That guarantee depends entirely on <see cref="AssetName"/> and <see cref="BinaryReleaseTag"/>
/// below never changing - the CI workflow (.github/workflows/build.yml) must keep publishing under
/// these exact same names on every future release, or every copy of this exe already handed out
/// stops finding the binary.
/// </summary>
public static class InstallerService
{
    private const string RepoOwner = "0-LingLing0";
    private const string RepoName = "LingStrap";
    private const string AssetName = "Lingstrap.exe";

    /// <summary>Lingstrap.exe itself is never attached to the public version-tag release (so that
    /// release page only ever shows this installer) - it lives in a separate, always-overwritten
    /// pre-release under this fixed tag instead. Permanent contract - see the class remarks above.</summary>
    private const string BinaryReleaseTag = "lingstrap-binary";

    public static string InstallDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lingstrap");

    public static string InstalledExePath => Path.Combine(InstallDir, "Lingstrap.exe");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    static InstallerService()
    {
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LingstrapSetup", "1.0"));
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public record ReleaseInfo(string Version, string DownloadUrl, long Size);

    public static async Task<ReleaseInfo> GetLatestReleaseAsync()
    {
        // The public release just tells us the human-facing version number to show while installing.
        using var latest = await FetchReleaseAsync("latest", "No Lingstrap release is published yet - ask whoever sent you this installer to publish one first.");
        var version = latest.RootElement.GetProperty("tag_name").GetString() ?? "unknown";

        // The actual exe lives in a separate, always-overwritten pre-release so the release page
        // above never shows anything but this installer.
        using var binary = await FetchReleaseAsync($"tags/{BinaryReleaseTag}", "The Lingstrap.exe binary release hasn't been published yet.");

        if (!binary.RootElement.TryGetProperty("assets", out var assets) || assets.GetArrayLength() == 0)
            throw new InstallException("The Lingstrap.exe binary release has no downloadable files attached.");

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (!string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase)) continue;

            var url = asset.GetProperty("browser_download_url").GetString()!;
            var size = asset.GetProperty("size").GetInt64();
            return new ReleaseInfo(version, url, size);
        }

        throw new InstallException($"The binary release doesn't include a '{AssetName}' file.");
    }

    private static async Task<JsonDocument> FetchReleaseAsync(string releasePathSuffix, string notFoundMessage)
    {
        HttpResponseMessage response;
        try
        {
            response = await Http.GetAsync($"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/{releasePathSuffix}");
        }
        catch (HttpRequestException ex)
        {
            throw new InstallException($"Could not reach GitHub - check your connection. ({ex.Message})");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InstallException(notFoundMessage);
        if (!response.IsSuccessStatusCode)
            throw new InstallException($"GitHub returned an error ({(int)response.StatusCode}).");

        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
    }

    public static async Task DownloadAndInstallAsync(ReleaseInfo release, Action<long, long> onProgress)
    {
        Directory.CreateDirectory(InstallDir);
        CloseRunningLingstrap();

        var tempPath = InstalledExePath + ".download";

        HttpResponseMessage response;
        try
        {
            response = await Http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (HttpRequestException ex)
        {
            throw new InstallException($"Could not reach GitHub to download the update: {ex.Message}");
        }

        if (!response.IsSuccessStatusCode)
            throw new InstallException($"Download failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        try
        {
            await using (var httpStream = await response.Content.ReadAsStreamAsync())
            await using (var fileStream = File.Create(tempPath))
            {
                var buffer = new byte[81920];
                long totalRead = 0;
                int read;
                while ((read = await httpStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    totalRead += read;
                    onProgress(totalRead, release.Size);
                }
            }

            File.Copy(tempPath, InstalledExePath, overwrite: true);
        }
        catch (IOException ex)
        {
            throw new InstallException($"Could not write {InstalledExePath} - is Lingstrap still open? ({ex.Message})");
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
        }

        CreateStartMenuShortcut();
    }

    /// <summary>Asks any already-running Lingstrap to close normally (so it still saves its settings
    /// via its own OnClosed handler) before falling back to a hard kill if it doesn't respond.</summary>
    private static void CloseRunningLingstrap()
    {
        foreach (var proc in Process.GetProcessesByName("Lingstrap"))
        {
            try
            {
                if (proc.CloseMainWindow())
                    proc.WaitForExit(5000);
                if (!proc.HasExited)
                    proc.Kill();
            }
            catch
            {
                // Best effort - a leftover process just means the overwrite below may fail instead.
            }
        }
    }

    private static void CreateStartMenuShortcut()
    {
        try
        {
            var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
            var shortcutPath = Path.Combine(startMenu, "Programs", "Lingstrap.lnk");

            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable.");
            var shell = Activator.CreateInstance(shellType)!;
            var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath })!;
            var shortcutType = shortcut.GetType();

            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { InstalledExePath });
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { InstallDir });
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { InstalledExePath });
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        catch
        {
            // A missing shortcut isn't worth failing the whole install over.
        }
    }
}

public class InstallException : Exception
{
    public InstallException(string message) : base(message) { }
}
