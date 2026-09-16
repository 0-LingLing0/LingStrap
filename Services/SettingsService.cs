using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lingstrap.Models;

namespace Lingstrap.Services;

/// <summary>Loads and saves Settings.json. One shared instance at SettingsService.Current.</summary>
public static class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static LingstrapSettings Current { get; private set; } = new();

    private static string BackupFile => Paths.SettingsFile + ".bak";

    public static void Load()
    {
        try
        {
            if (!File.Exists(Paths.SettingsFile))
            {
                Log.Info("No settings file yet, using defaults.");
                Current = new LingstrapSettings();
                Save();
                return;
            }

            Current = TryLoadFrom(Paths.SettingsFile) ?? TryLoadFromBackup() ?? new LingstrapSettings();
            Log.Info($"Settings loaded. Preset={Current.ActivePreset}, custom flags={Current.CustomFlags.Count}");

            // Deserialising silently ignores keys that no longer exist on the model, so an older
            // file keeps carrying them until something rewrites it. Save once here to drop them.
            if (Current.SchemaVersion < LingstrapSettings.CurrentSchemaVersion)
            {
                var from = Current.SchemaVersion;
                Current.SchemaVersion = LingstrapSettings.CurrentSchemaVersion;
                Save();
                Log.Info($"Settings migrated from schema v{from} to v{LingstrapSettings.CurrentSchemaVersion} - removed fields dropped.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read settings, falling back to defaults", ex);
            Current = new LingstrapSettings();
        }
    }

    private static LingstrapSettings? TryLoadFrom(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<LingstrapSettings>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not parse {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private static LingstrapSettings? TryLoadFromBackup()
    {
        if (!File.Exists(BackupFile)) return null;

        var restored = TryLoadFrom(BackupFile);
        if (restored != null)
            Log.Warn("Settings.json was corrupted or unreadable - restored from the last good backup instead.");
        return restored;
    }

    /// <summary>
    /// Writes via a temp file plus File.Replace so a crash or force-kill mid-save can't leave a
    /// truncated, unparseable Settings.json behind - the old file only gets swapped out once the new
    /// one has been fully written to disk, and its previous contents are kept as a .bak Load() can
    /// fall back to if the main file is ever unreadable anyway (e.g. an interrupted write from
    /// before this existed, or a disk error).
    /// </summary>
    public static void Save()
    {
        try
        {
            Paths.EnsureCreated();
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            var tempFile = Paths.SettingsFile + ".tmp";
            File.WriteAllText(tempFile, json);

            if (File.Exists(Paths.SettingsFile))
                File.Replace(tempFile, Paths.SettingsFile, BackupFile);
            else
                File.Move(tempFile, Paths.SettingsFile);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
        }
    }
}
