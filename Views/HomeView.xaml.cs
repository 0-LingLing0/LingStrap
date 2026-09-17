using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lingstrap.Models;
using Lingstrap.Services;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;

namespace Lingstrap.Views;

public partial class HomeView : Page
{
    public HomeView()
    {
        InitializeComponent();
        ColorHero();
        Refresh();

        // Home is cached and re-shown rather than reconstructed on every visit (see NavigationView's
        // own caching), so without this, switching presets on the Presets page would leave this
        // page showing whatever preset was active the first time it was ever opened.
        Loaded += (_, _) => Refresh();
    }

    /// <summary>Tints the hero card's corner glow and gradient start to the current accent - a faint
    /// echo of the loading screen's own icon halo, so the two don't feel like unrelated designs. The
    /// gradient's far end used to be a hardcoded dark navy, which looked fine on the default dark
    /// theme but left this card a jarring dark patch against a white page once light theme shipped -
    /// it now matches whichever theme is actually active instead.</summary>
    private void ColorHero()
    {
        var accent = ColorThemeCatalog.CurrentAccentColor();
        var isLight = SettingsService.Current.LightTheme;
        HeroGradientStart.Color = Color.FromArgb(isLight ? (byte)0x40 : (byte)0x33, accent.R, accent.G, accent.B);
        HeroGradientEnd.Color = isLight ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x23, 0x25, 0x2F);
        HeroGlow.Fill = new SolidColorBrush(Color.FromArgb(0x30, accent.R, accent.G, accent.B));
    }

    private void Refresh()
    {
        var s = SettingsService.Current;
        PresetIcon.Symbol = s.ActivePreset switch
        {
            Preset.BestQuality     => SymbolRegular.Star24,
            Preset.BestPerformance => SymbolRegular.Rocket24,
            _                      => SymbolRegular.Scales24,
        };
        PresetText.Text = PresetInfo.Name(s.ActivePreset);
        PresetDesc.Text = PresetInfo.Description(s.ActivePreset);

        StatusRowsPanel.Children.Clear();
        StatusRowsPanel.Children.Add(StatusRow("Active preset", PresetInfo.Name(s.ActivePreset)));
        StatusRowsPanel.Children.Add(StatusRow("Custom FastFlags", s.CustomFlags.Count.ToString()));
        StatusRowsPanel.Children.Add(StatusRow("Mods", s.ModsEnabled ? "On" : "Off"));
        StatusRowsPanel.Children.Add(StatusRow("Multi-instance", s.MultiInstance ? "On" : "Off"));
        StatusRowsPanel.Children.Add(StatusRow("Roblox version", s.InstalledRobloxVersion ?? "Not installed yet"));

        var folders = StatusRow("Lingstrap folder", null);
        folders.Description = Paths.Root;
        folders.ShowDivider = false;
        folders.Content = new Wpf.Ui.Controls.Button { Content = "Open" };
        ((Wpf.Ui.Controls.Button)folders.Content).Click += OpenFolder_Click;
        StatusRowsPanel.Children.Add(folders);
    }

    private static SettingRow StatusRow(string title, string? value)
    {
        var row = new SettingRow { Title = title };
        if (value != null)
        {
            var text = new System.Windows.Controls.TextBlock { Text = value, FontSize = 13 };
            text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            row.Content = text;
        }
        return row;
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        // Immediate visual feedback against a fast double-click - LauncherService.LaunchAsync's own
        // named-mutex guard is the real protection (it also covers a second Lingstrap.exe launched
        // via the roblox-player: protocol handler), but disabling the button here means a same-window
        // double-click doesn't even get as far as showing that "already in progress" error.
        LaunchButton.IsEnabled = false;

        var closeOnLaunch = SettingsService.Current.CloseLingstrapOnLaunch;
        var mainWindow = Window.GetWindow(this);

        // Hidden, not closed: the loading dialog's own CloseDialog (driven by the launcher
        // pipeline reaching "Roblox's window appeared") is what decides when to exit, never this
        // window closing as a side effect. ShutdownMode is OnExplicitShutdown (see App.xaml).
        if (closeOnLaunch) mainWindow?.Hide();

        var dialog = LaunchProgressDialogFactory.Create();
        var ok = await LauncherService.LaunchAsync(string.Empty, dialog);

        if (!closeOnLaunch)
        {
            LaunchButton.IsEnabled = true;
            return;
        }

        if (ok)
        {
            await Task.Delay(250); // let the dialog's own fade-out finish before tearing everything down
            Application.Current.Shutdown();
        }
        else
        {
            // Failed - the dialog already showed the error and stays open on its own; bring the
            // main window back so there's still something for the user to act on.
            LaunchButton.IsEnabled = true;
            mainWindow?.Show();
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Paths.EnsureCreated();
        Process.Start(new ProcessStartInfo { FileName = Paths.Root, UseShellExecute = true });
    }
}
