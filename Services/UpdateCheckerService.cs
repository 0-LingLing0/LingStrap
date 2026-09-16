using System;
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

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public enum UpdateStatus { UpToDate, UpdateAvailable, CheckFailed }

    public record UpdateCheckResult(UpdateStatus Status, string? LatestVersion, string? ReleaseUrl, string? Message);

    static UpdateCheckerService()
    {
        // GitHub's API rejects requests with no User-Agent at all.
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Lingstrap", CurrentVersion().ToString(3)));
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public static async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        try
        {
            var response = await Http.GetAsync($"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest");

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

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var releaseUrl = doc.RootElement.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() : null;

            var latest = ParseVersion(tagName);
            if (latest is null)
            {
                Log.Warn($"Update check: could not parse a version number out of release tag '{tagName}'.");
                return new UpdateCheckResult(UpdateStatus.CheckFailed, null, null, $"Couldn't understand the release tag '{tagName}'.");
            }

            var current = CurrentVersion();
            if (latest > current)
            {
                Log.Info($"Update check: a newer version is available ({latest.ToString(3)} > {current.ToString(3)}).");
                return new UpdateCheckResult(UpdateStatus.UpdateAvailable, latest.ToString(3), releaseUrl, null);
            }

            Log.Info($"Update check: already up to date ({current.ToString(3)}).");
            return new UpdateCheckResult(UpdateStatus.UpToDate, current.ToString(3), null, null);
        }
        catch (Exception ex)
        {
            Log.Warn($"Update check failed: {ex.Message}");
            return new UpdateCheckResult(UpdateStatus.CheckFailed, null, null, "Couldn't reach GitHub - check your connection.");
        }
    }

    private static Version CurrentVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);

    /// <summary>Release tags are typically "v0.2.0" or "0.2.0" - strip a leading "v" before parsing.</summary>
    private static Version? ParseVersion(string tag)
    {
        var trimmed = tag.TrimStart('v', 'V');
        return Version.TryParse(trimmed, out var v) ? v : null;
    }
}
