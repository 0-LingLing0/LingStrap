using System.Collections.Generic;

namespace Lingstrap.Models;

/// <summary>
/// Everything Lingstrap remembers, serialised to Settings.json.
/// Add fields freely - fields missing from an older file fall back to these defaults.
/// </summary>
public class LingstrapSettings
{
    /// <summary>
    /// Bumped when fields are removed, so SettingsService can rewrite the file and drop the keys
    /// that no longer exist instead of leaving them orphaned. See SettingsService.Load.
    /// v2: removed UncapFps, TargetFps, ShowActivityPanel, MouseCursorFileName,
    /// ShiftlockCursorFileName and LastLaunch.
    /// </summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public const int CurrentSchemaVersion = 2;

    // --- Appearance ------------------------------------------------------
    /// <summary>Name of a ColorTheme in ColorThemeCatalog.All. Falls back to the first entry if unknown.</summary>
    public string AccentTheme { get; set; } = "Violet";

    // --- Presets -------------------------------------------------------
    public Preset ActivePreset { get; set; } = Preset.Balanced;

    // --- Performance ---------------------------------------------------
    public string Renderer { get; set; } = "D3D11";   // D3D11 | Vulkan | OpenGL

    // --- Behaviour -----------------------------------------------------
    public bool MultiInstance { get; set; } = false;
    public bool CloseCrashHandler { get; set; } = true;
    public bool ForceDedicatedGpu { get; set; } = false;
    public bool PinClientsToCores { get; set; } = false;
    public string ProcessPriority { get; set; } = "Normal";   // Normal | AboveNormal | High - never RealTime
    public bool CloseLingstrapOnLaunch { get; set; } = true;
    public bool ShowLoadingScreen { get; set; } = true;
    /// <summary>
    /// Which monitor Roblox's window gets moved onto after it opens, identified by its Win32 device
    /// name (e.g. "\\.\DISPLAY1") since that's stable across reboots while monitor ordering isn't.
    /// Null means leave it wherever Windows put it.
    /// </summary>
    public string? PreferredMonitorDeviceName { get; set; }

    // --- Server info ---------------------------------------------------
    /// <summary>Master toggle for the whole server-info feature - capture as well as display.</summary>
    public bool ShowServerLocation { get; set; } = true;
    /// <summary>
    /// The one-line overlay banner shown over Roblox on join. Key name predates the banner (it
    /// used to be a desktop notification) - kept as-is so existing settings files don't reset.
    /// </summary>
    public bool ShowServerNotification { get; set; } = true;
    /// <summary>How long the overlay banner stays fully visible before fading out, in seconds (1-10).</summary>
    public int OverlayBannerSeconds { get; set; } = 4;
    public bool DiscordRichPresence { get; set; } = false;

    // --- Mods ----------------------------------------------------------
    public bool ModsEnabled { get; set; } = true;
    /// <summary>Cursor slot name (CursorSlot.ToString()) -> chosen size percentage (25-200, default 100).</summary>
    public Dictionary<string, int> CursorScalePercent { get; set; } = new();

    // --- FastFlags -----------------------------------------------------
    /// <summary>Flags set by hand. Preset flags are applied on top at launch time.</summary>
    public List<FastFlagEntry> CustomFlags { get; set; } = new();

    // --- Companion Apps --------------------------------------------------
    public List<CompanionApp> CompanionApps { get; set; } = new();
    /// <summary>Master toggle - off disables the whole feature without deleting the list.</summary>
    public bool ManageCompanionApps { get; set; } = true;

    // --- Roblox install --------------------------------------------------
    /// <summary>The Roblox version hash Lingstrap last successfully installed, if any - lets a
    /// launch check for updates instead of re-deriving this from disk every time.</summary>
    public string? InstalledRobloxVersion { get; set; }
}
