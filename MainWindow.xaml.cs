using System;
using System.Windows;
using System.Windows.Media;
using Lingstrap.Models;
using Lingstrap.Services;
using Lingstrap.Views;
using Wpf.Ui.Controls;

namespace Lingstrap;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();

        var icon = IconRecolorService.GetIcon(ColorThemeCatalog.CurrentAccentColor());
        Icon = icon;
        TitleBarIcon.Source = icon;

        ApplyFontScale(SettingsService.Current.FontScalePercent);

        // Deliberately not calling SystemThemeWatcher.Watch: it forces the window to follow
        // the OS theme, which would undo the fixed dark theme + custom accent set at startup.
        // FluentWindow's own WindowBackdropType="Mica" (set in XAML) still applies Mica on its own.
        Loaded += (_, _) => RootNavigation.Navigate(typeof(Views.HomeView));
        Loaded += (_, _) => StartBackgroundServices();
        Loaded += async (_, _) => await CheckForUpdateOnStartupAsync();
    }

    /// <summary>
    /// Scales the whole page-content area via a LayoutTransform, rather than trying to scale the
    /// handful of centralized text Styles (H1/Body/Sub/...) - plenty of views still set FontSize
    /// directly on individual elements (ad-hoc, not through those styles), so scaling only the named
    /// styles would miss most of the UI. Unlike accent color and theme, this isn't baked into any
    /// WPF-UI control Style at ControlsDictionary-merge time, so it can apply live, no restart needed.
    /// </summary>
    public void ApplyFontScale(int percent)
    {
        var scale = percent / 100.0;
        RootNavigation.LayoutTransform = scale == 1.0 ? Transform.Identity : new ScaleTransform(scale, scale);
    }

    /// <summary>Silently checks GitHub once per launch per UpdateCheckMode: Off skips entirely,
    /// Notify shows what changed (only the first time a given version is seen - never nags again for
    /// a version already announced), AutoInstall installs it with no prompt at all.</summary>
    private async System.Threading.Tasks.Task CheckForUpdateOnStartupAsync()
    {
        var mode = SettingsService.Current.UpdateMode;
        if (mode == UpdateCheckMode.Off) return;

        var result = await UpdateCheckerService.CheckForUpdateAsync();
        if (result.Status != UpdateCheckerService.UpdateStatus.UpdateAvailable) return;

        if (mode == UpdateCheckMode.AutoInstall)
        {
            if (result.SetupDownloadUrl != null && await UpdateCheckerService.DownloadAndLaunchSetupAsync(result.SetupDownloadUrl))
                Application.Current.Shutdown();
            return;
        }

        if (result.LatestVersion == SettingsService.Current.LastSeenUpdateVersion) return;
        SettingsService.Current.LastSeenUpdateVersion = result.LatestVersion;
        SettingsService.Save();

        await UpdateDialogHelper.ShowAsync(this, result);
    }

    private static void StartBackgroundServices()
    {
        if (SettingsService.Current.ShowServerLocation)
            ActivityCoordinator.Start();

        if (SettingsService.Current.DiscordRichPresence)
            DiscordPresenceService.Start();

        if (SettingsService.Current.PinClientsToCores)
            CpuAffinityService.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        ActivityCoordinator.Stop();
        DiscordPresenceService.Stop();
        CpuAffinityService.Stop();
        SettingsService.Save();

        // ShutdownMode is OnExplicitShutdown (see App.xaml) so this window closing doesn't exit on
        // its own - it needs to here, since this is the normal "user closed Lingstrap" path.
        Application.Current.Shutdown();
    }
}
