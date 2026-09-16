using System;
using System.Collections.Generic;

namespace Lingstrap.Models;

public record GbsFieldValue(string Tag, string Value);

public class PresetSpec
{
    public required Dictionary<string, string> Flags { get; init; }
    public required Dictionary<string, GbsFieldValue> GbsFields { get; init; }
    public required string Priority { get; init; }   // Normal | AboveNormal | High
}

/// <summary>The exact FastFlag/GBS/priority values each built-in preset applies.</summary>
public static class PresetCatalog
{
    /// <summary>Every FastFlag name any preset can write. Cleared before a preset lays its own down.</summary>
    public static readonly IReadOnlyList<string> ManagedFlagNames = new[]
    {
        "DFIntDebugFRMQualityLevelOverride",
        "DFFlagTextureQualityOverrideEnabled",
        "DFIntTextureQualityOverride",
        "FIntDebugForceMSAASamples",
        "FIntFRMMinGrassDistance",
        "FIntFRMMaxGrassDistance",
        "FIntGrassMovementReducedMotionFactor",
        "DFFlagDebugPauseVoxelizer",
        "FFlagDebugSkyGray",
        "DFFlagDisableDPIScale",
        "DFIntCSGLevelOfDetailSwitchingDistance",
        "DFIntCSGLevelOfDetailSwitchingDistanceL12",
        "DFIntCSGLevelOfDetailSwitchingDistanceL23",
        "DFIntCSGLevelOfDetailSwitchingDistanceL34",
    };

    /// <summary>Every GBS property any preset can write. Cleared before a preset lays its own down.</summary>
    public static readonly IReadOnlyList<string> ManagedGbsFields = new[]
    {
        "SavedQualityLevel", "MaxQualityEnabled", "VignetteEnabled", "ReducedMotion", "FramerateCap",
    };

    public static PresetSpec Get(Preset preset) => preset switch
    {
        Preset.BestPerformance => new PresetSpec
        {
            Flags = new Dictionary<string, string>
            {
                ["DFIntDebugFRMQualityLevelOverride"] = "1",
                ["DFFlagTextureQualityOverrideEnabled"] = "true",
                ["DFIntTextureQualityOverride"] = "0",
                ["FIntDebugForceMSAASamples"] = "0",
                ["FIntFRMMinGrassDistance"] = "0",
                ["FIntFRMMaxGrassDistance"] = "0",
                ["FIntGrassMovementReducedMotionFactor"] = "0",
                ["DFFlagDebugPauseVoxelizer"] = "true",
                ["FFlagDebugSkyGray"] = "true",
                ["DFFlagDisableDPIScale"] = "true",
                ["DFIntCSGLevelOfDetailSwitchingDistance"] = "50",
                ["DFIntCSGLevelOfDetailSwitchingDistanceL12"] = "100",
                ["DFIntCSGLevelOfDetailSwitchingDistanceL23"] = "150",
                ["DFIntCSGLevelOfDetailSwitchingDistanceL34"] = "200",
            },
            GbsFields = new Dictionary<string, GbsFieldValue>
            {
                ["SavedQualityLevel"] = new("token", "2"),
                ["MaxQualityEnabled"] = new("bool", "false"),
                ["VignetteEnabled"] = new("bool", "false"),
                ["ReducedMotion"] = new("bool", "true"),
                // Uncapping FPS used to go through a FastFlag (DFIntTaskSchedulerTargetFps), which
                // Roblox's own FastFlag allowlist (introduced September 2025) now silently ignores -
                // that's why presets stopped actually uncapping anything. FramerateCap is a genuine
                // GBS user setting instead (the same one Roblox's own Settings menu writes here), a
                // completely different mechanism the allowlist has no say over. -1/0 turned out to
                // mean "use Roblox's own default cap" (60fps) rather than "unlimited" - confirmed by
                // a real test where it produced 60fps, not uncapped - so a genuinely high explicit
                // number is what actually raises the cap, the same convention every FPS-unlocking
                // guide/tool for Roblox uses. Some client builds reportedly still clamp to an
                // internal ceiling around 240fps regardless - that's an engine-side limit no GBS
                // value can reach past.
                ["FramerateCap"] = new("int", "9999"),
            },
            Priority = "High",
        },

        Preset.BestQuality => new PresetSpec
        {
            Flags = new Dictionary<string, string>
            {
                ["DFIntDebugFRMQualityLevelOverride"] = "21",
                ["DFFlagTextureQualityOverrideEnabled"] = "true",
                ["DFIntTextureQualityOverride"] = "3",
                ["FIntDebugForceMSAASamples"] = "4",
                ["FIntFRMMinGrassDistance"] = "400",
                ["FIntFRMMaxGrassDistance"] = "1000",
                ["DFFlagDebugPauseVoxelizer"] = "false",
                ["FFlagDebugSkyGray"] = "false",
                ["DFFlagDisableDPIScale"] = "false",
            },
            GbsFields = new Dictionary<string, GbsFieldValue>
            {
                ["SavedQualityLevel"] = new("token", "10"),
                ["MaxQualityEnabled"] = new("bool", "true"),
                ["VignetteEnabled"] = new("bool", "true"),
                ["ReducedMotion"] = new("bool", "false"),
                // FPS cap is orthogonal to visual quality - every preset uncaps it the same way now,
                // so switching between presets for looks never silently reintroduces Roblox's 60fps
                // default (which is what happened before this, since FramerateCap being a
                // preset-managed field meant a preset that didn't mention it would clear it instead
                // of leaving it alone).
                ["FramerateCap"] = new("int", "9999"),
            },
            Priority = "Normal",
        },

        Preset.Balanced => new PresetSpec
        {
            Flags = new Dictionary<string, string>
            {
                ["DFIntDebugFRMQualityLevelOverride"] = "8",
                ["DFFlagTextureQualityOverrideEnabled"] = "true",
                ["DFIntTextureQualityOverride"] = "2",
                ["FIntDebugForceMSAASamples"] = "1",
                ["FIntFRMMinGrassDistance"] = "0",
                ["FIntFRMMaxGrassDistance"] = "100",
                ["DFFlagDebugPauseVoxelizer"] = "false",
                ["FFlagDebugSkyGray"] = "false",
                ["DFFlagDisableDPIScale"] = "true",
            },
            GbsFields = new Dictionary<string, GbsFieldValue>
            {
                ["SavedQualityLevel"] = new("token", "6"),
                ["MaxQualityEnabled"] = new("bool", "false"),
                ["VignetteEnabled"] = new("bool", "false"),
                ["ReducedMotion"] = new("bool", "false"),
                ["FramerateCap"] = new("int", "9999"),
            },
            Priority = "AboveNormal",
        },

        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Custom has no fixed preset spec."),
    };
}
