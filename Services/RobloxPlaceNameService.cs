using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Lingstrap.Services;

/// <summary>Resolves a Roblox place id to its game's display name, via Roblox's own public API.</summary>
public static class RobloxPlaceNameService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly Dictionary<string, string> Cache = new();
    private static readonly object CacheLock = new();

    /// <summary>The game's display name for this place, or null if the lookup failed. Cached for the rest of this process's lifetime.</summary>
    public static async Task<string?> GetNameAsync(string placeId)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(placeId, out var cached)) return cached;
        }

        try
        {
            var universeJson = await Http.GetStringAsync($"https://apis.roblox.com/universes/v1/places/{placeId}/universe");
            using var universeDoc = JsonDocument.Parse(universeJson);
            if (!universeDoc.RootElement.TryGetProperty("universeId", out var universeIdEl))
                return null;

            var gamesJson = await Http.GetStringAsync($"https://games.roblox.com/v1/games?universeIds={universeIdEl.GetInt64()}");
            using var gamesDoc = JsonDocument.Parse(gamesJson);
            var data = gamesDoc.RootElement.GetProperty("data");
            var name = data.GetArrayLength() > 0 && data[0].TryGetProperty("name", out var n) ? n.GetString() : null;

            if (name != null)
                lock (CacheLock) { Cache[placeId] = name; }

            return name;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not resolve name for place {placeId}: {ex.Message}");
            return null;
        }
    }
}
