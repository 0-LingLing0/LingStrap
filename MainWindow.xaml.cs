using System;
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
