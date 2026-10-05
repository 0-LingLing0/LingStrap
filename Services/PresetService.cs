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

        if (preset == Preset.Afk) EnterAfkExtras(s);
        else LeaveAfkExtras(s);

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
        s.OneCorePerClient = spec.OneCorePerClient;
        s.MemoryLimitMb = spec.MemoryLimitMb;
        s.SmallWindows = spec.SmallWindows;
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

    /// <summary>
    /// Makes exactly the preset's flags active - every flag in it on, with its value, and every other
    /// flag off - without deleting anything from the list.
    ///
    /// This used to clear the list and rebuild it from the preset. But a saved preset only records
    /// the flags that were ENABLED when it was saved, so applying one permanently deleted every flag
    /// the user had switched off to keep for later, plus anything added since the preset was saved.
    /// Turning the others off reproduces the snapshot's effect just as exactly, and they're still
    /// there to switch back on.
    /// </summary>
    public static void ApplySaved(SavedPreset preset)
    {
        var s = SettingsService.Current;
        LeaveAfkExtras(s);

        foreach (var flag in s.CustomFlags)
        {
            if (preset.Flags.TryGetValue(flag.Name, out var value))
            {
                flag.Value = value;
                flag.Enabled = true;
            }
            else
            {
                flag.Enabled = false;
            }
        }

        foreach (var (name, value) in preset.Flags)
        {
            if (s.CustomFlags.All(f => f.Name != name))
                s.CustomFlags.Add(new FastFlagEntry { Name = name, Value = value, Enabled = true });
        }

        GlobalBasicSettingsService.RemoveFields(PresetCatalog.ManagedGbsFields);
        foreach (var (name, field) in preset.GbsFields)
            GlobalBasicSettingsService.WriteScalar(name, field.Tag, field.Value);

        s.ProcessPriority = preset.Priority;
        // Saved presets don't record the AFK limits; leaving them on would make a normal setup stutter.
        s.OneCorePerClient = false;
        s.MemoryLimitMb = 0;
        s.SmallWindows = false;
        s.ActivePreset = Preset.Custom;
        s.ActiveSavedPresetId = preset.Id;
        SettingsService.Save();

        Log.Info($"Applied saved preset \"{preset.Name}\".");
    }

    /// <summary>Switches off Roblox's own settings an AFK client doesn't need, remembering each one's
    /// value first. Applying AFK again while it's already on keeps the original snapshot - otherwise
    /// the second time would record the switched-off values as the ones to go back to.</summary>
    private static void EnterAfkExtras(LingstrapSettings s)
    {
        if (s.AfkSavedGbs.Count == 0)
        {
            foreach (var name in PresetCatalog.AfkExtraGbsFields.Keys)
            {
                var field = GlobalBasicSettingsService.GetField(name);
                s.AfkSavedGbs[name] = field?.RawValue != null ? new GbsFieldValue(field.Tag, field.RawValue) : null;
            }
        }

        foreach (var (name, field) in PresetCatalog.AfkExtraGbsFields)
            GlobalBasicSettingsService.WriteScalar(name, field.Tag, field.Value);
    }

    /// <summary>Puts back what EnterAfkExtras switched off. A no-op unless AFK was the last preset.</summary>
    private static void LeaveAfkExtras(LingstrapSettings s)
    {
        if (s.AfkSavedGbs.Count == 0) return;

        foreach (var (name, original) in s.AfkSavedGbs)
        {
            if (original == null) GlobalBasicSettingsService.RemoveFields(new[] { name });
            else GlobalBasicSettingsService.WriteScalar(name, original.Tag, original.Value);
        }

        s.AfkSavedGbs.Clear();
        Log.Info("Restored the Roblox settings the AFK preset had switched off.");
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
