using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Lingstrap.Services;

/// <summary>
/// Checks GitHub Releases for a newer Lingstrap version than the one currently running. Requires the
/// repo (or at least its releases) to be public - GitHub's API returns 404 for a private repo's
/// releases to an unauthenticated request, and a distributed app can't safely carry a personal
/// access token to get around that.
/// </summary>
public static class UpdateCheckerService
{
    private const string RepoOwner = "0-LingLing0";
    private const string RepoName = "LingStrap";
    private const string SetupAssetName = "LingstrapSetup.exe";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public enum UpdateStatus { UpToDate, UpdateAvailable, CheckFailed }

    public record UpdateCheckResult(UpdateStatus Status, string? LatestVersion, string? ReleaseUrl, string? Message,
        string? ReleaseNotes = null, string? SetupDownloadUrl = null);

    static UpdateCheckerService()
    {
        // GitHub's API rejects requests with no User-Agent at all.
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Lingstrap", CurrentVersionText));
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public static async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        try
        {
            // Its own short limit rather than the client's 5-minute one, which is sized for downloading
            // the installer. With AutoInstall this check runs before any window exists, so on a network
            // that silently drops packets - a captive portal, a firewall - double-clicking Lingstrap
            // showed nothing at all for up to five minutes. This is one small JSON request.
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await Http.GetAsync($"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest", timeout.Token);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Log.Info("Update check: GitHub returned 404 - the repo is private or has no published releases yet.");
                return new UpdateCheckResult(UpdateStatus.CheckFailed, null, null,
                    "Couldn't check - the repository is either private or doesn't have a release published yet.");
            }

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"Update check: GitHub API returned {(int)response.StatusCode} {response.StatusCode}.");
                return new UpdateCheckResult(UpdateStatus.CheckFailed, null, null, $"GitHub returned an error ({(int)response.StatusCode}).");
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            using var doc = JsonDocument.Parse(json);
            var tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var releaseUrl = doc.RootElement.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() : null;
            var releaseNotes = doc.RootElement.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() : null;

            string? setupUrl = null;
            if (doc.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (!string.Equals(asset.GetProperty("name").GetString(), SetupAssetName, StringComparison.OrdinalIgnoreCase)) continue;
                    setupUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

            var latest = ParseVersion(tagName);
            if (latest is null)
            {
                Log.Warn($"Update check: could not parse a version number out of release tag '{tagName}'.");
                return new UpdateCheckResult(UpdateStatus.CheckFailed, null, null, $"Couldn't understand the release tag '{tagName}'.");
            }

            var current = CurrentVersion();
            if (latest > current)
            {
                Log.Info($"Update check: a newer version is available ({Format(latest)} > {Format(current)}).");
                return new UpdateCheckResult(UpdateStatus.UpdateAvailable, Format(latest), releaseUrl, null,
                    string.IsNullOrWhiteSpace(releaseNotes) ? "(No release notes provided.)" : releaseNotes, setupUrl);
            }

            Log.Info($"Update check: already up to date ({Format(current)}, latest release {Format(latest)}).");
            // Release notes included here too (not just the UpdateAvailable branch below) so a
            // caller can show a "here's what's new" notice on the first launch of a version that
            // was installed silently (UpdateCheckMode.AutoInstall never prompts beforehand) - but
            // only when they ARE this version's notes. A build newer than the latest release (a
            // local test build) would otherwise announce the previous release's notes as its own,
            // and mark its version as announced before its real notes were ever published.
            var notesForThisVersion = latest == current;
            return new UpdateCheckResult(UpdateStatus.UpToDate, Format(current), releaseUrl, null,
                !notesForThisVersion ? null
                : string.IsNullOrWhiteSpace(releaseNotes) ? "(No release notes provided.)" : releaseNotes);
        }
        catch (Exception ex)
        {
            Log.Warn($"Update check failed: {ex.Message}");
            return new UpdateCheckResult(UpdateStatus.CheckFailed, null, null, "Couldn't reach GitHub - check your connection.");
        }
    }

    /// <summary>
    /// Downloads LingstrapSetup.exe (the same tiny installer people are handed to install Lingstrap
    /// in the first place) to a temp file and launches it, then returns so the caller can shut this
    /// process down - the setup exe's own install logic already closes any running Lingstrap and
    /// replaces its exe once this process is out of the way, so no in-process self-replace is needed
    /// here (an already-running exe can't overwrite itself on Windows anyway).
    /// </summary>
    public static async Task<bool> DownloadAndLaunchSetupAsync(string setupDownloadUrl)
    {
        try
        {
            var response = await Http.GetAsync(setupDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"Update install: downloading LingstrapSetup.exe failed with HTTP {(int)response.StatusCode}.");
                return false;
            }

            var tempPath = Path.Combine(Path.GetTempPath(), $"LingstrapSetup-{Guid.NewGuid():N}.exe");
            await using (var httpStream = await response.Content.ReadAsStreamAsync())
            await using (var fileStream = File.Create(tempPath))
            {
                await httpStream.CopyToAsync(fileStream);
            }

            Process.Start(new ProcessStartInfo { FileName = tempPath, UseShellExecute = true });
            Log.Info("Update install: launched LingstrapSetup.exe to install the new version.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Update install failed: {ex.Message}");
            return false;
        }
    }

    private static Version CurrentVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0, 0);

    /// <summary>This build's version as people should see it - see Format.</summary>
    public static string CurrentVersionText => Format(CurrentVersion());

    /// <summary>
    /// "0.2.7" for a feature release, "0.2.7.3" for a fix release. This used to be ToString(3)
    /// everywhere, which dropped the fourth number: every fix release displayed as the release it
    /// fixed, and because the "what's new" notice is keyed on this same text, a fix release that
    /// AutoInstall put on silently was never announced at all - it looked already seen.
    /// </summary>
    private static string Format(Version version) => version.ToString(version.Revision > 0 ? 4 : 3);

    /// <summary>Release tags are typically "v0.2.0" or "0.2.0" - strip a leading "v" before parsing.
    /// Missing parts count as 0, the way the assembly version stores them, so "0.2.7" equals this
    /// build's 0.2.7.0 rather than comparing as older than it.</summary>
    private static Version? ParseVersion(string tag)
    {
        var trimmed = tag.TrimStart('v', 'V');
        if (!Version.TryParse(trimmed, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }
}
