namespace Lingstrap.Models;

/// <summary>The one-click quality presets.</summary>
public enum Preset
{
    BestQuality,
    Balanced,
    BestPerformance,
    Custom,
    Afk, // after Custom so existing values keep their meaning
}

public static class PresetInfo
{
    public static string Name(Preset p) => p switch
    {
        Preset.BestQuality     => "Best Quality",
        Preset.Balanced        => "Balanced",
        Preset.BestPerformance => "Best Performance",
        Preset.Afk             => "AFK",
        _                      => "Custom"
    };

    public static string Description(Preset p) => p switch
    {
        Preset.BestQuality =>
            "Everything on. Lighting, shadows and textures at full. FPS uncapped.",
        Preset.Balanced =>
            "Sensible middle. Game still looks normal, expensive effects are stripped.",
        Preset.BestPerformance =>
            "Frames over looks. Effects off, lowest render distance, FPS uncapped.",
        Preset.Afk =>
            "For idling or farming on many accounts. 3 FPS, lowest graphics, low priority and Windows efficiency mode - as little power as possible.",
        _ =>
            "Your own mix. Changing any flag or setting by hand puts you here."
    };
}
