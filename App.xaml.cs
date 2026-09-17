using System;
using System.Threading.Tasks;
using System.Windows;
using Lingstrap.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Lingstrap;

public partial class App : Application
{
    /// <summary>Raw command line arguments, minus the executable path.</summary>
    public static string[] LaunchArgs { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// True when Lingstrap was started by clicking Play on the Roblox website
    /// (the roblox-player: protocol handler), rather than opened by the user.
    /// </summary>
    public static bool IsRobloxLaunch { get; private set; }

    /// <summary>
    /// True for the detached watcher modes (-multiinstancewatcher, -companionwatcher,
    /// -discordwatcher). None of them load Settings.json before running, so OnExit must not save
    /// over it with an untouched, default-constructed SettingsService.Current - that would
    /// silently wipe the real settings file if this process happens to exit around the same time
    /// as the interactive one.
    /// </summary>
    private static bool _isWatcherMode;

    /// <summary>Set when this process was launched by AppearanceView's Apply-triggered restart
    /// (-windowpos:left,top) - the old window's screen position, so the replacement reopens in the
    /// same spot instead of the XAML-default centered placement if the user had moved it. Also used
    /// as the signal that this is that kind of restart, so MainWindow lands back on the Appearance
    /// page instead of Home.</summary>
    public static System.Windows.Point? WindowPositionOverride { get; private set; }

    /// <summary>Set alongside WindowPositionOverride (-windowsize:width,height) - the old window's
    /// size, so the replacement reopens at the same size instead of snapping back to the XAML-default
    /// dimensions if the user had resized it.</summary>
    public static System.Windows.Size? WindowSizeOverride { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error("Unhandled exception: " + (args.ExceptionObject as Exception)?.ToString());
        };

        LaunchArgs = e.Args;
        IsRobloxLaunch = LaunchArgs.Length > 0 &&
                         LaunchArgs[0].StartsWith("roblox", StringComparison.OrdinalIgnoreCase);

        foreach (var arg in LaunchArgs)
        {
            if (arg.StartsWith("-windowpos:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = arg["-windowpos:".Length..].Split(',');
                if (parts.Length == 2
                    && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var left)
                    && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var top))
                {
                    WindowPositionOverride = new System.Windows.Point(left, top);
                }
            }
            else if (arg.StartsWith("-windowsize:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = arg["-windowsize:".Length..].Split(',');
                if (parts.Length == 2
                    && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var width)
                    && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var height))
                {
                    WindowSizeOverride = new System.Windows.Size(width, height);
                }
            }
        }

        Paths.EnsureCreated();
        Log.Info($"Lingstrap starting. args=[{string.Join(' ', LaunchArgs)}]");

        if (LaunchArgs.Length > 0 && LaunchArgs[0].Equals("-multiinstancewatcher", StringComparison.OrdinalIgnoreCase))
        {
            _isWatcherMode = true;
            MultiInstanceWatcher.RunAndBlock();
            Shutdown();
            return;
        }

        if (LaunchArgs.Length > 0 && LaunchArgs[0].Equals("-companionwatcher", StringComparison.OrdinalIgnoreCase))
        {
            _isWatcherMode = true;
            CompanionAppService.RunWatcherAndBlock();
            Shutdown();
            return;
        }

        if (LaunchArgs.Length > 0 && LaunchArgs[0].Equals("-discordwatcher", StringComparison.OrdinalIgnoreCase))
        {
            _isWatcherMode = true;
            DiscordPresenceService.RunWatcherAndBlock();
            Shutdown();
            return;
        }

        if (LaunchArgs.Length > 0 && LaunchArgs[0].Equals("-networkoptimize", StringComparison.OrdinalIgnoreCase))
        {
            // Not a long-running watcher like the others - just performs the (admin-only) changes
            // once and exits. Launched elevated (see BehaviourView's button), so this process itself
            // never needs to be; the normal interactive Lingstrap stays a standard, unelevated app.
            _isWatcherMode = true;
            NetworkOptimizationService.RunAll();
            Shutdown();
            return;
        }

        if (LaunchArgs.Length > 0 && LaunchArgs[0].Equals("-networkrestore", StringComparison.OrdinalIgnoreCase))
        {
            _isWatcherMode = true;
            NetworkOptimizationService.RestoreAll();
            Shutdown();
            return;
        }

        if (LaunchArgs.Length > 0 && LaunchArgs[0].Equals("-activitywatcher", StringComparison.OrdinalIgnoreCase))
        {
            _isWatcherMode = true;
            // Unlike the other watcher modes, this one doesn't block or call Shutdown() itself -
            // OverlayBannerWindow needs a real, running Dispatcher to render, so this lets startup
            // continue into WPF's normal message loop instead (ShutdownMode="OnExplicitShutdown"
            // keeps it alive with no MainWindow). StartDetachedWatcher's own timer calls Shutdown()
            // once every Roblox client has closed.
            if (!ActivityCoordinator.StartDetachedWatcher())
                Shutdown();
            return;
        }

        if (LaunchArgs.Length > 0 && LaunchArgs[0].Equals("-fpswatcher", StringComparison.OrdinalIgnoreCase))
        {
            _isWatcherMode = true;
            // Same shape as -activitywatcher above - FpsOverlayWindow also needs a real Dispatcher to
            // render and keep updating, so this doesn't block or shut down here either.
            if (!FpsOverlayCoordinator.StartDetachedWatcher())
                Shutdown();
            return;
        }

        if (LaunchArgs.Length > 0 && LaunchArgs[0].Equals("-reopenwatcher", StringComparison.OrdinalIgnoreCase))
        {
            _isWatcherMode = true;
            ReopenOnCloseService.RunWatcherAndBlock();
            Shutdown();
            return;
        }

        SettingsService.Load();

        // Fixed theme (dark by default, light if the user opted in on the Appearance page) with a
        // custom accent built for the app icon, not the OS accent - applied once at startup rather
        // than following the system theme.
        //
        // Using the explicit 4-color overload rather than Apply(color, theme): WPF-UI's automatic
        // dark-theme ramp lightens the base color further (brightness+17/saturation-45) to derive
        // AccentFillColorDefault (what buttons/toggles actually use) - starting from an already
        // light violet like #A778FF, that washes out to near-white. Setting secondaryAccent (the
        // one dark theme uses as its default fill) to the base color itself keeps buttons vivid,
        // and tertiaryAccent reuses the icon's own darker gradient stop for depth.
        var theme = SettingsService.Current.LightTheme ? ApplicationTheme.Light : ApplicationTheme.Dark;
        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica);

        if (SettingsService.Current.LightTheme)
            ApplyLightPalette();

        var accent = SettingsService.Current.AccentTheme == Models.ColorThemeCatalog.CustomThemeName
            ? Models.ColorThemeCatalog.FromCustomColor(Models.ColorThemeCatalog.ParseCustomColor(SettingsService.Current.CustomAccentColor))
            : Models.ColorThemeCatalog.Find(SettingsService.Current.AccentTheme);
        ApplicationAccentColorManager.Apply(accent.Base, accent.Light, accent.Base, accent.Dark);

        if (IsRobloxLaunch)
        {
            HandleRobloxLaunch(LaunchArgs[0]);
            return;
        }

        try { ProtocolHandlerService.Register(); }
        catch (Exception ex) { Log.Warn($"Could not register protocol handler: {ex.Message}"); }

        RemoveStaleExeFromUpdate();
        StartMainWindowAsync();
    }

    /// <summary>
    /// Deletes the previous Lingstrap.exe that an update renamed aside. The installer can't overwrite
    /// an exe that's still running - and it often is, since the detached watchers run from this same
    /// file and the FPS overlay's one is elevated beyond an unelevated installer's reach - so it
    /// renames the old file out of the way instead and leaves it for whichever start comes next. By
    /// now nothing is using it; if something somehow still is, the delete fails and the following
    /// start tries again.
    /// </summary>
    private static void RemoveStaleExeFromUpdate()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            var directory = System.IO.Path.GetDirectoryName(exePath);
            if (directory is null) return;

            foreach (var stale in System.IO.Directory.GetFiles(directory, "Lingstrap.exe*.old"))
            {
                try
                {
                    System.IO.File.Delete(stale);
                    Log.Info($"Removed the previous version left behind by an update: {System.IO.Path.GetFileName(stale)}");
                }
                catch (Exception ex)
                {
                    Log.Info($"Previous version {System.IO.Path.GetFileName(stale)} is still in use - leaving it for the next start. ({ex.Message})");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not check for a leftover exe from an update: {ex.Message}");
        }
    }

    /// <summary>The result of an AutoInstall update check made here, before any window exists -
    /// MainWindow's own startup check reuses it (see CheckForUpdateOnStartupAsync) instead of hitting
    /// GitHub a second time, and clears it immediately after so a later re-check (reopened after
    /// Roblox closes, or a manual "Check now") always does a fresh one instead of reusing a stale
    /// result from however long ago this launch started.</summary>
    internal static UpdateCheckerService.UpdateCheckResult? PendingUpdateResult { get; set; }

    /// <summary>For UpdateCheckMode.AutoInstall, checks for and silently installs an update BEFORE
    /// ever showing a window - otherwise the window flashes open just to immediately close again the
    /// moment the installer takes over a moment later. Any other outcome (up to date, check failed,
    /// download failed, or a different mode entirely) falls through to the normal window.</summary>
    private async void StartMainWindowAsync()
    {
        if (SettingsService.Current.UpdateMode == Models.UpdateCheckMode.AutoInstall)
        {
            var result = await UpdateCheckerService.CheckForUpdateAsync();
            if (result.Status == UpdateCheckerService.UpdateStatus.UpdateAvailable && result.SetupDownloadUrl != null
                && await UpdateCheckerService.DownloadAndLaunchSetupAsync(result.SetupDownloadUrl))
            {
                Shutdown();
                return;
            }
            PendingUpdateResult = result;
        }

        var window = new MainWindow();
        window.Show();
    }

    private async void HandleRobloxLaunch(string launchUri)
    {
        // Started BEFORE the launch, not after - this is what wires up the join-detection log
        // watcher (among other things). The normal in-app flow already has this running well before
        // the user ever clicks Launch (MainWindow starts it on its own Loaded event at startup), so
        // starting it here only once LaunchAsync finishes was too late for a browser launch straight
        // into a specific game: the join can happen (and get written to Roblox's log) while the
        // client is still loading, before LaunchAsync itself returns - starting the watcher after
        // that point means it never sees a join line that's already scrolled past by the time it
        // starts tailing, leaving Discord stuck on "In the launcher" forever.
        if (!SettingsService.Current.CloseLingstrapOnLaunch)
            Lingstrap.MainWindow.StartBackgroundServices();

        var dialog = LaunchProgressDialogFactory.Create();
        var ok = await LauncherService.LaunchAsync(launchUri, dialog);

        try { ProtocolHandlerService.Register(); }
        catch (Exception ex) { Log.Warn($"Could not register protocol handler: {ex.Message}"); }

        if (ok)
        {
            if (SettingsService.Current.CloseLingstrapOnLaunch)
            {
                await Task.Delay(250); // let the dialog's own fade-out finish before tearing everything down
                Shutdown();
                return;
            }

            // Already running in the background (started above) - the user launched from their own
            // browser, not from Lingstrap, so popping the main window open here would be an
            // unrequested interruption to something they didn't ask to see.
            // ShutdownMode="OnExplicitShutdown" (see App.xaml) keeps this process alive with no
            // window; ReopenOnCloseService can bring a window up later if that option is turned on.
            return;
        }

        // Failed - the dialog already showed the error and stays open on its own (or, if the loading
        // screen is off, there was nothing to show) - bring up the main window so the user isn't left
        // with nothing to act on.
        var window = new MainWindow();
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_isWatcherMode) SettingsService.Save();
        Log.Info("Lingstrap exiting.");
        base.OnExit(e);
    }

    /// <summary>
    /// App.xaml hardcodes a dark palette (ApplicationBackgroundColor, CardBackgroundFillColorDefault,
    /// ControlStrokeColorDefault, TextFillColorPrimary/Secondary) as sibling resources so they survive
    /// ApplicationThemeManager's own theme-dictionary swap - see the comment in App.xaml. That means
    /// they don't move on their own when switching to Light, so this overrides those same five keys
    /// with light-appropriate colors instead. Run once at startup (Light theme setting requires a
    /// restart to apply anyway, same as accent color), so no need to ever undo this at runtime.
    /// </summary>
    private static void ApplyLightPalette()
    {
        SetColorResource("ApplicationBackgroundColor", "ApplicationBackgroundBrush", 0xFF, 0xF4, 0xF4, 0xF7);
        SetColorResource("CardBackgroundFillColorDefault", "CardBackgroundFillColorDefaultBrush", 0xFF, 0xFF, 0xFF, 0xFF);
        SetColorResource("ControlStrokeColorDefault", "ControlStrokeColorDefaultBrush", 0x1F, 0x00, 0x00, 0x00);
        SetColorResource("TextFillColorPrimary", "TextFillColorPrimaryBrush", 0xFF, 0x1B, 0x1B, 0x1F);
        SetColorResource("TextFillColorSecondary", "TextFillColorSecondaryBrush", 0xFF, 0x5C, 0x5F, 0x6B);
    }

    private static void SetColorResource(string colorKey, string brushKey, byte a, byte r, byte g, byte b)
    {
        var color = System.Windows.Media.Color.FromArgb(a, r, g, b);
        Current.Resources[colorKey] = color;
        Current.Resources[brushKey] = new System.Windows.Media.SolidColorBrush(color);
    }
}
