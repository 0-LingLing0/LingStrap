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

        var window = new MainWindow();
        window.Show();
    }

    private async void HandleRobloxLaunch(string launchUri)
    {
        var dialog = LaunchProgressDialogFactory.Create();
        var ok = await LauncherService.LaunchAsync(launchUri, dialog);

        if (ok)
        {
            if (SettingsService.Current.CloseLingstrapOnLaunch)
            {
                await Task.Delay(250); // let the dialog's own fade-out finish before tearing everything down
                Shutdown();
                return;
            }
        }
        // On failure the dialog has already shown the error and stays open on its own (or, if the
        // loading screen is off, there was nothing to show) - either way, still bring up the main
        // window so the user isn't left with nothing to act on.

        try { ProtocolHandlerService.Register(); }
        catch (Exception ex) { Log.Warn($"Could not register protocol handler: {ex.Message}"); }

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
