using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Lingstrap.Services;

public record GeoResult(string City, string Region, string Country);

/// <summary>
/// Resolves a server IP to city/region/country/ISP via ipwho.is. Roblox reuses the same
/// datacenter IPs constantly, so every result is cached to disk permanently - never evicted.
///
/// Was ip-api.com originally, switched after finding it flatly wrong for one of Roblox's own IP
/// blocks (128.116.21.33): it reported Singapore, while the actual connection was clearly European
/// (low ping from a European client). ipwho.is reports Amsterdam for that same address - matching
/// where the traffic was really going - while still agreeing with ip-api.com on every IP where the
/// two didn't conflict, suggesting ip-api's data is simply stale/wrong for at least this block
/// rather than this being an inherent anycast-routing limitation.
/// </summary>
public static class GeoLocationService
{
    private static readonly Dictionary<string, GeoResult> Cache = new();
    private static readonly object CacheLock = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly string CacheFile = Path.Combine(Paths.Root, "GeoCache.json");
    private static bool _loaded;

    /// <summary>
    /// Two accounts under multi-instance can join servers with previously-uncached IPs close enough
    /// together that their lookups genuinely overlap - Cache is a plain Dictionary, not thread-safe
    /// for concurrent access, so every touch of it (including inside EnsureLoaded and SaveCache) goes
    /// through CacheLock. The lock is never held across the actual HTTP await - only around the
    /// dictionary reads/writes themselves.
    /// </summary>
    public static async Task<GeoResult?> LookupAsync(string ip)
    {
        EnsureLoaded();
        lock (CacheLock)
        {
            if (Cache.TryGetValue(ip, out var cached)) return cached;
        }

        try
        {
            var json = await Http.GetStringAsync($"https://ipwho.is/{ip}");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("success", out var success) || !success.GetBoolean())
                return null;

            var result = new GeoResult(
                City: Get(root, "city"),
                Region: Get(root, "region"),
                Country: Get(root, "country"));

            lock (CacheLock) { Cache[ip] = result; }
            SaveCache();
            return result;
        }
        catch (Exception ex)
        {
            Log.Warn($"Geolocation lookup failed for {ip} (offline or rate-limited): {ex.Message}");
            return null;
        }
    }

    private static string Get(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    private static void EnsureLoaded()
    {
        lock (CacheLock)
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                if (!File.Exists(CacheFile)) return;
                var raw = JsonSerializer.Deserialize<Dictionary<string, GeoResult>>(File.ReadAllText(CacheFile));
                if (raw is null) return;

                foreach (var (ip, result) in raw) Cache[ip] = result;
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not load geo cache: {ex.Message}");
            }
        }
    }

    private static void SaveCache()
    {
        try
        {
            Dictionary<string, GeoResult> snapshot;
            lock (CacheLock) { snapshot = new Dictionary<string, GeoResult>(Cache); }

            Directory.CreateDirectory(Paths.Root);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save geo cache: {ex.Message}");
        }
    }
}
