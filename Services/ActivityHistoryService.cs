using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Lingstrap.Models;

namespace Lingstrap.Services;

/// <summary>
/// Small persisted list of recent joins, newest first. There's no history UI any more - this is
/// what holds a join while the geo lookup trickles in, so the overlay banner can be built from the
/// enriched entry rather than the bare one.
/// </summary>
public static class ActivityHistoryService
{
    private const int MaxEntries = 50;
    private static readonly string FilePath = Path.Combine(Paths.Root, "ActivityHistory.json");

    public static List<ActivityEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<ActivityEntry>();
            return JsonSerializer.Deserialize<List<ActivityEntry>>(File.ReadAllText(FilePath)) ?? new List<ActivityEntry>();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not load activity history: {ex.Message}");
            return new List<ActivityEntry>();
        }
    }

    public static void Add(ActivityEntry entry)
    {
        var list = Load();
        list.Insert(0, entry);
        if (list.Count > MaxEntries)
            list.RemoveRange(MaxEntries, list.Count - MaxEntries);

        try
        {
            Directory.CreateDirectory(Paths.Root);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save activity history: {ex.Message}");
        }
    }

    /// <summary>Patches the entry with this JobId in place - used as async lookups (geo, place info, player count) trickle in.</summary>
    public static void UpdateByJobId(string jobId, Action<ActivityEntry> mutate)
    {
        var list = Load();
        var entry = list.Find(e => e.JobId == jobId);
        if (entry is null) return;

        mutate(entry);

        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save activity history: {ex.Message}");
        }
    }
}
