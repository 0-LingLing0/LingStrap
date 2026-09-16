using System.Collections.Generic;
using System.Linq;
using Lingstrap.Models;

namespace Lingstrap.Services;

/// <summary>Applies a preset's FastFlags, GBS graphics settings and process priority together.</summary>
public static class PresetService
{
    public static void Apply(Preset preset)
    {
        var s = SettingsService.Current;
        s.ActiveSavedPresetId = null;

        if (preset == Preset.Custom)
        {
            s.ActivePreset = Preset.Custom;
            SettingsService.Save();
            return;
        }

        var spec = PresetCatalog.Get(preset);

        // Clear every flag any preset could have written, then lay this one's down -
        // otherwise a flag from the previous preset that this one omits would linger.
        s.CustomFlags.RemoveAll(f => PresetCatalog.ManagedFlagNames.Contains(f.Name));
        foreach (var (name, value) in spec.Flags)
            s.CustomFlags.Add(new FastFlagEntry { Name = name, Value = value, Enabled = true });

        // Same clear-then-apply for GBS. GlobalBasicSettingsService itself defers the actual
        // write if Roblox happens to be running right now.
        GlobalBasicSettingsService.RemoveFields(PresetCatalog.ManagedGbsFields);
        foreach (var (name, field) in spec.GbsFields)
            GlobalBasicSettingsService.WriteScalar(name, field.Tag, field.Value);

        s.ProcessPriority = spec.Priority;
        s.ActivePreset = preset;
        SettingsService.Save();

        Log.Info($"Applied preset {preset}");
    }

    /// <summary>Editing anything by hand (except the renderer) drops the active preset to Custom.</summary>
    public static void MarkCustom()
    {
        var s = SettingsService.Current;
        if (s.ActivePreset == Preset.Custom && s.ActiveSavedPresetId == null) return;

        s.ActivePreset = Preset.Custom;
        s.ActiveSavedPresetId = null;
        Log.Info("Switched to Custom preset (manual edit).");
        SettingsService.Save();
    }

    /// <summary>Snapshots the currently-enabled FastFlags, the same handful of graphics settings the
    /// built-in presets manage, and the process priority into a new named SavedPreset - a "full"
    /// preset in the sense that applying it later reproduces this exact state, not just a diff from
    /// whatever's active at the time.</summary>
    public static SavedPreset SaveCurrentAsPreset(string name)
    {
        var s = SettingsService.Current;

        var flags = new Dictionary<string, string>();
        foreach (var f in s.CustomFlags.Where(f => f.Enabled))
            flags[f.Name] = f.Value;

        var gbsFields = new Dictionary<string, GbsFieldValue>();
        foreach (var fieldName in PresetCatalog.ManagedGbsFields)
        {
            var field = GlobalBasicSettingsService.GetField(fieldName);
            if (field?.RawValue != null)
                gbsFields[fieldName] = new GbsFieldValue(field.Tag, field.RawValue);
        }

        var preset = new SavedPreset
        {
            Name = name,
            Flags = flags,
            GbsFields = gbsFields,
            Priority = s.ProcessPriority,
        };

        s.SavedPresets.Add(preset);
        s.ActivePreset = Preset.Custom;
        s.ActiveSavedPresetId = preset.Id;
        SettingsService.Save();

        Log.Info($"Saved current settings as preset \"{name}\" ({flags.Count} flags).");
        return preset;
    }

    /// <summary>Applies a saved preset exactly like a built-in one, except the flag list is REPLACED
    /// wholesale rather than clear-then-lay-down over a fixed managed set - a saved preset is a
    /// complete snapshot already, including whatever the user hand-added beyond the built-in flags.</summary>
    public static void ApplySaved(SavedPreset preset)
    {
        var s = SettingsService.Current;

        s.CustomFlags.Clear();
        foreach (var (name, value) in preset.Flags)
            s.CustomFlags.Add(new FastFlagEntry { Name = name, Value = value, Enabled = true });

        GlobalBasicSettingsService.RemoveFields(PresetCatalog.ManagedGbsFields);
        foreach (var (name, field) in preset.GbsFields)
            GlobalBasicSettingsService.WriteScalar(name, field.Tag, field.Value);

        s.ProcessPriority = preset.Priority;
        s.ActivePreset = Preset.Custom;
        s.ActiveSavedPresetId = preset.Id;
        SettingsService.Save();

        Log.Info($"Applied saved preset \"{preset.Name}\".");
    }

    public static void DeleteSaved(string id)
    {
        var s = SettingsService.Current;
        s.SavedPresets.RemoveAll(p => p.Id == id);
        if (s.ActiveSavedPresetId == id) s.ActiveSavedPresetId = null;
        SettingsService.Save();
    }

    public static void RenameSaved(string id, string newName)
    {
        var preset = SettingsService.Current.SavedPresets.FirstOrDefault(p => p.Id == id);
        if (preset is null) return;

        preset.Name = newName;
        SettingsService.Save();
    }
}
