using System.Collections.Generic;

namespace Lingstrap.Models;

public record GbsFieldInfo(string Label, string Description);

/// <summary>A known, documented numeric range for a GBS field - lets the editor use a slider instead of a free NumberBox.</summary>
public record GbsFieldRange(double Min, double Max, bool IsPercentage, bool IntegerSnap);

/// <summary>Friendly labels for the handful of fields worth explaining. Everything else shows its raw @name.</summary>
public static class GbsFieldCatalog
{
    /// <summary>
    /// Fields with a real, known bounded range - everything else in GlobalBasicSettings is an
    /// arbitrary Roblox-internal value with no documented schema here, so it stays a free NumberBox
    /// rather than risk clamping a legitimate value into a guessed range.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, GbsFieldRange> BoundedRanges = new Dictionary<string, GbsFieldRange>
    {
        ["SavedQualityLevel"]        = new(1, 10, IsPercentage: false, IntegerSnap: true),
        ["GraphicsQualityLevel"]     = new(1, 10, IsPercentage: false, IntegerSnap: true),
        ["MasterVolume"]             = new(0.0, 1.0, IsPercentage: true, IntegerSnap: false),
        ["MasterVolumeStudio"]       = new(0.0, 1.0, IsPercentage: true, IntegerSnap: false),
        ["PartyVoiceVolume"]         = new(0.0, 1.0, IsPercentage: true, IntegerSnap: false),
        ["VoiceChatVolume"]          = new(0.0, 1.0, IsPercentage: true, IntegerSnap: false),
        // Not percentages - sensitivity is a raw, often finely-tuned value (e.g. 0.1, or the
        // default 0.360000014), and rounding it to a whole percent point destroys exactly the
        // precision people set it for.
        ["MouseSensitivity"]         = new(0.0, 1.0, IsPercentage: false, IntegerSnap: false),
        ["GamepadCameraSensitivity"] = new(0.0, 1.0, IsPercentage: false, IntegerSnap: false),
        ["PreferredTransparency"]    = new(0.0, 1.0, IsPercentage: true, IntegerSnap: false),
        ["HapticStrength"]           = new(0.0, 1.0, IsPercentage: true, IntegerSnap: false),
    };

    public static readonly IReadOnlyDictionary<string, GbsFieldInfo> FriendlyNames = new Dictionary<string, GbsFieldInfo>
    {
        ["FramerateCap"]             = new("FPS cap", "0 or -1 = Roblox's own default cap (60). A high number like 9999 is effectively uncapped."),
        ["SavedQualityLevel"]        = new("Graphics quality", "1-10."),
        ["MaxQualityEnabled"]        = new("Auto-quality", "Off to keep your manual level."),
        ["VignetteEnabled"]          = new("Vignette", "Screen-edge darkening."),
        ["ReducedMotion"]            = new("Reduced motion", "Less UI animation."),
        ["MasterVolume"]             = new("Master volume", "0.0 - 1.0"),
        ["MouseSensitivity"]         = new("Mouse sensitivity", "Raw sensitivity value."),
    };

    /// <summary>
    /// Common settings shown expanded on the Roblox Settings page, below Annoyances. Any name also
    /// present in Annoyances is skipped there to avoid showing the same field twice.
    /// </summary>
    public static readonly IReadOnlyList<string> Shortlist = new[]
    {
        "FramerateCap", "SavedQualityLevel", "MaxQualityEnabled",
        "VignetteEnabled", "ReducedMotion",
        "MasterVolume", "MouseSensitivity", "CameraYInverted", "ChatTranslationEnabled",
    };

    /// <summary>Name -> (label, what turning it off does). Only rendered when the name is actually present in the file.</summary>
    public static readonly IReadOnlyList<(string Name, string Label, string Description)> Annoyances = new (string, string, string)[]
    {
        ("ChatTranslationEnabled",      "Auto-translate chat",        "Stops chat messages from being machine-translated."),
        ("ChatTranslationFTUXShown",    "Translation intro popup",    "Won't show the one-time translation feature popup again."),
        ("VignetteEnabled",             "Screen-edge darkening",      "Removes the vignette effect around the edge of the screen."),
        ("VignetteEnabledCustomOption", "Vignette override toggle",   "Turns off the override that lets vignette be controlled separately."),
        ("ReducedMotion",               "UI animation",               "Reduces menu and UI motion effects."),
        ("PreferredTransparency",       "UI transparency",            "Turns off translucent UI panels."),
        ("HapticStrength",              "Controller rumble",          "Disables controller vibration feedback."),
        ("CameraYInverted",             "Inverted look",              "Turns off inverted vertical camera look."),
        ("VREnabled",                   "VR mode",                    "Disables VR mode."),
    };
}
