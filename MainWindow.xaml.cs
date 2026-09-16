using System;
using System.Diagnostics;
using System.Windows;
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

        // Deliberately not calling SystemThemeWatcher.Watch: it forces the window to follow
        // the OS theme, which would undo the fixed dark theme + custom accent set at startup.
        // FluentWindow's own WindowBackdropType="Mica" (set in XAML) still applies Mica on its own.
        Loaded += (_, _) => RootNavigation.Navigate(typeof(Views.HomeView));
        Loaded += (_, _) => StartBackgroundServices();
        Loaded += async (_, _) => await CheckForUpdateOnStartupAsync();
    }

    /// <summary>Silently checks GitHub once per launch and, only the first time a given version is
    /// seen, shows what changed. Never nags again for a version already announced.</summary>
    private async System.Threading.Tasks.Task CheckForUpdateOnStartupAsync()
    {
        if (!SettingsService.Current.AutoCheckForUpdates) return;

        var result = await UpdateCheckerService.CheckForUpdateAsync();
        if (result.Status != UpdateCheckerService.UpdateStatus.UpdateAvailable) return;
        if (result.LatestVersion == SettingsService.Current.LastSeenUpdateVersion) return;

        SettingsService.Current.LastSeenUpdateVersion = result.LatestVersion;
        SettingsService.Save();

        var openRelease = await DialogHelper.ShowConfirmAsync(this, AboutView.BuildReleaseNotesContent(result.ReleaseNotes),
            $"Version {result.LatestVersion} is available - what's new", confirmText: "Open release page", cancelText: "Later");
        if (openRelease && result.ReleaseUrl != null)
            Process.Start(new ProcessStartInfo { FileName = result.ReleaseUrl, UseShellExecute = true });
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
