using System;
using System.Windows;
using Lingstrap.Services;
using Wpf.Ui.Controls;

namespace Lingstrap.Views;

/// <summary>
/// Lingstrap is discontinued. Every start - the app itself, the shortcut, or a Roblox launch from the
/// browser - shows only this: the launcher its developer recommends instead, as links. Nothing is
/// downloaded or installed from here, and nothing else of Lingstrap can be reached.
/// </summary>
public partial class DiscontinuedWindow : FluentWindow
{
    public const string WebsiteUrl = "https://phasmastrap.com/";
    public const string GitHubUrl = "https://github.com/lacedsc3ne/PhasmaStrap";

    public DiscontinuedWindow()
    {
        InitializeComponent();
        Icon = IconRecolorService.GetIcon(Models.ColorThemeCatalog.CurrentAccentColor());
    }

    private void Website_Click(object sender, RoutedEventArgs e) => Open(WebsiteUrl);

    private void GitHub_Click(object sender, RoutedEventArgs e) => Open(GitHubUrl);

    private static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Could not open {url}: {ex.Message}"); }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // ShutdownMode is OnExplicitShutdown (see App.xaml).
        Application.Current.Shutdown();
    }
}
