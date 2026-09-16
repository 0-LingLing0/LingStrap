using System.Linq;
using Lingstrap.Models;

namespace Lingstrap.Services;

/// <summary>Applies a preset's FastFlags, GBS graphics settings and process priority together.</summary>
public static class PresetService
{
    public static void Apply(Preset preset)
    {
        var s = SettingsService.Current;

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
        if (SettingsService.Current.ActivePreset == Preset.Custom) return;

        SettingsService.Current.ActivePreset = Preset.Custom;
        Log.Info("Switched to Custom preset (manual edit).");
        SettingsService.Save();
    }
}
