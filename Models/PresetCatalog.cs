using System;
using System.Collections.Generic;

namespace Lingstrap.Models;

public record GbsFieldValue(string Tag, string Value);

public class PresetSpec
{
    public required Dictionary<string, string> Flags { get; init; }
    public required Dictionary<string, GbsFieldValue> GbsFields { get; init; }
    public required string Priority { get; init; }   // Low | BelowNormal | Normal | AboveNormal | High
    public bool OneCorePerClient { get; init; }
    public int MemoryLimitMb { get; init; }
    public bool SmallWindows { get; init; }
    public int MemoryBoostMb { get; init; }
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

    /// <summary>
    /// Roblox's own in-game settings the AFK preset also switches off: sound, fullscreen, the HUD
    /// overlays, profilers and the like - nothing an unattended farming client needs. Kept out of
    /// ManagedGbsFields on purpose: those get cleared back to Roblox's defaults whenever a preset
    /// changes, which would reset someone's volume or chat just for switching between Balanced and
    /// Best Quality. PresetService snapshots these before AFK applies and puts them back after.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, GbsFieldValue> AfkExtraGbsFields = new Dictionary<string, GbsFieldValue>
    {
        ["GraphicsQualityLevel"] = new("int", "1"),
        ["MasterVolume"] = new("float", "0"),
        ["VoiceChatVolume"] = new("float", "0"),
        ["PartyVoiceVolume"] = new("float", "0"),
        ["Fullscreen"] = new("bool", "false"),
        ["StartMaximized"] = new("bool", "false"),
        ["ChatVisible"] = new("bool", "false"),
        ["PlayerListVisible"] = new("bool", "false"),
        ["PlayerNamesEnabled"] = new("bool", "false"),
        ["BadgeVisible"] = new("bool", "false"),
        ["PerformanceStatsVisible"] = new("bool", "false"),
        ["OnScreenProfilerEnabled"] = new("bool", "false"),
        ["MicroProfilerWebServerEnabled"] = new("bool", "false"),
        ["ChatTranslationEnabled"] = new("bool", "false"),
        ["HapticStrength"] = new("float", "0"),
        ["ReadAloud"] = new("bool", "false"),
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

        Preset.Afk => new PresetSpec
        {
            // Best Performance's lowest-everything flags; the difference is the frame cap and priority.
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
                ["DFIntCSGLevelOfDetailSwitchingDistance"] = "0",
                ["DFIntCSGLevelOfDetailSwitchingDistanceL12"] = "0",
                ["DFIntCSGLevelOfDetailSwitchingDistanceL23"] = "0",
                ["DFIntCSGLevelOfDetailSwitchingDistanceL34"] = "0",
            },
            GbsFields = new Dictionary<string, GbsFieldValue>
            {
                ["SavedQualityLevel"] = new("token", "1"),
                ["MaxQualityEnabled"] = new("bool", "false"),
                ["VignetteEnabled"] = new("bool", "false"),
                ["ReducedMotion"] = new("bool", "true"),
                // Rendering is most of what an idle client costs, so frames are the big saving.
                ["FramerateCap"] = new("int", "3"),
            },
            // Low also puts each client into Windows' efficiency mode - see LauncherService.
            Priority = "Low",
            // Sized for ~15 clients on an 8 GB PC: 15 x 300 MB leaves Windows its few GB.
            OneCorePerClient = true,
            MemoryLimitMb = 300,
            SmallWindows = true,
            MemoryBoostMb = 500,
        },

        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Custom has no fixed preset spec."),
    };
}
