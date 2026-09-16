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
    /// v3: removed AutoCheckForUpdates (bool), replaced by UpdateMode (enum: Off/Notify/AutoInstall).
    /// </summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public const int CurrentSchemaVersion = 3;

    // --- Appearance ------------------------------------------------------
    /// <summary>Name of a ColorTheme in ColorThemeCatalog.All, or ColorThemeCatalog.CustomThemeName
    /// ("Custom") when CustomAccentColor should be used instead. Falls back to the first entry if
    /// unknown.</summary>
    public string AccentTheme { get; set; } = "Violet";

    /// <summary>The "RRGGBB" hex color picked via the Appearance page's custom color picker. Only
    /// meaningful when AccentTheme is ColorThemeCatalog.CustomThemeName.</summary>
    public string? CustomAccentColor { get; set; }

    /// <summary>True for the light theme variant, false (default) for the original fixed dark theme.</summary>
    public bool LightTheme { get; set; } = false;

    /// <summary>Scales the whole UI (not just text) via a LayoutTransform on the main window's
    /// content area. 100 is native size; the Appearance page offers roughly 80-150.</summary>
    public int FontScalePercent { get; set; } = 100;

    // --- Presets -------------------------------------------------------
    public Preset ActivePreset { get; set; } = Preset.Balanced;
    /// <summary>User-saved full presets (FastFlags + graphics settings + process priority captured
    /// together), alongside the three built-in ones.</summary>
    public List<SavedPreset> SavedPresets { get; set; } = new();
    /// <summary>Which SavedPresets entry (by Id) is currently applied, if any - tracked separately
    /// from ActivePreset since a saved custom preset still reads as Preset.Custom there, same as any
    /// other hand-edited state. Cleared whenever a built-in preset is applied or anything is edited
    /// by hand (see PresetService).</summary>
    public string? ActiveSavedPresetId { get; set; }

    // --- Performance ---------------------------------------------------
    public string Renderer { get; set; } = "D3D11";   // D3D11 | Vulkan | OpenGL

    // --- Behaviour -----------------------------------------------------
    public bool MultiInstance { get; set; } = false;
    public bool CloseCrashHandler { get; set; } = true;
    public bool ForceDedicatedGpu { get; set; } = false;
    public bool PinClientsToCores { get; set; } = false;
    public string ProcessPriority { get; set; } = "Normal";   // Normal | AboveNormal | High - never RealTime
    public bool CloseLingstrapOnLaunch { get; set; } = true;
    /// <summary>Companion to CloseLingstrapOnLaunch - brings a Lingstrap window back up once every
    /// Roblox client has closed, instead of leaving nothing running once you're done playing.</summary>
    public bool ReopenLingstrapOnRobloxClose { get; set; } = false;
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

    // --- Lingstrap updates -------------------------------------------------
    /// <summary>How Lingstrap handles a new release found on startup. Off: never checks. Notify:
    /// checks and shows what changed, letting the user choose to update. AutoInstall: checks and
    /// installs silently, closing and reopening Lingstrap on the new version.</summary>
    public UpdateCheckMode UpdateMode { get; set; } = UpdateCheckMode.Notify;
    /// <summary>The newest version a "what's new" notice has already been shown for - checked once a
    /// launch finds itself already up to date, so a version that AutoInstall installed silently (no
    /// prior prompt) still gets announced once on its first launch. Not used to gate the
    /// still-pending "update available" prompt itself - that one shows every launch until installed.</summary>
    public string? LastSeenUpdateVersion { get; set; }

    // --- Diagnostics -----------------------------------------------------
    /// <summary>Shows a small always-on-top FPS counter over the Roblox window while it's running.
    /// Needs its own elevated helper process (see FpsOverlayCoordinator) since the real-time ETW
    /// session behind it requires administrator rights - off by default so nobody gets a surprise UAC
    /// prompt on launch without asking for this.</summary>
    public bool ShowFpsOverlay { get; set; } = false;
    public FpsOverlayPosition FpsOverlayPosition { get; set; } = FpsOverlayPosition.TopRight;
    /// <summary>Set once FpsWatcherTaskService.TryCreateTask() succeeds, so OnRobloxStarted can skip
    /// spawning "schtasks /query" on every single launch just to re-confirm something already known -
    /// that extra process spawn (even a fast, non-hanging one) was adding to launch time for no
    /// benefit on the overwhelmingly common case where the task is already there.</summary>
    public bool FpsWatcherTaskConfirmed { get; set; } = false;
}

public enum UpdateCheckMode { Off, Notify, AutoInstall }

public enum FpsOverlayPosition { TopLeft, TopRight, BottomLeft, BottomRight }
