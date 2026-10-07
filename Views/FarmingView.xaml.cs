using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Lingstrap.Models;
using Lingstrap.Services;

namespace Lingstrap.Views;

/// <summary>Every setting that matters for running many accounts at once, in one place. The same
/// settings the Behaviour and Presets pages use - changing one here changes it there too.</summary>
public partial class FarmingView : Page
{
    private bool _loading;

    public FarmingView()
    {
        InitializeComponent();
        LoadFromSettings();
        Loaded += (_, _) => { LoadFromSettings(); RefreshStatus(); };
    }

    private void LoadFromSettings()
    {
        _loading = true;
        var s = SettingsService.Current;

        ChkMulti.IsChecked = s.MultiInstance;
        ChkAutoCloseStuck.IsChecked = s.AutoCloseStuck;
        ChkCrashHandler.IsChecked = s.CloseCrashHandler;
        ChkSmallWindows.IsChecked = s.SmallWindows;
        ChkMinimize.IsChecked = s.MinimizeAfterLoad;
        ChkOneCore.IsChecked = s.OneCorePerClient;
        ChkGameDvr.IsChecked = s.DisableGameDvr;

        PriorityCombo.SelectedIndex = s.ProcessPriority switch
        {
            "High" => 2,
            "AboveNormal" => 1,
            "BelowNormal" => 3,
            "Low" => 4,
            _ => 0,
        };
        RefreshNetworkRow();

        var extrasOn = s.ShowFpsOverlay || s.DiscordRichPresence || s.ShowServerLocation;
        LightweightButton.IsEnabled = extrasOn;
        LightweightButton.Content = extrasOn ? "Turn extras off" : "Extras are off";

        _loading = false;
    }

    private void RefreshStatus()
    {
        var running = RobloxProcesses.Count();
        StatusRow.Title = running == 0
            ? "No accounts running"
            : $"{running} account{(running == 1 ? "" : "s")} running";
    }

    private static void Select(ComboBox combo, string tag) =>
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? combo.Items[0];

    private static int IntTag(ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var v) ? v : 0;

    private async void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = SettingsService.Current;

        // Settings that presets set: changing one by hand makes the active preset Custom, the same
        // as on every other page.
        var presetBefore = (s.ProcessPriority, s.OneCorePerClient, s.SmallWindows);

        s.MultiInstance = ChkMulti.IsChecked == true;
        s.AutoCloseStuck = ChkAutoCloseStuck.IsChecked == true;
        s.CloseCrashHandler = ChkCrashHandler.IsChecked == true;
        s.SmallWindows = ChkSmallWindows.IsChecked == true;
        s.MinimizeAfterLoad = ChkMinimize.IsChecked == true;
        s.OneCorePerClient = ChkOneCore.IsChecked == true;
        s.ProcessPriority = PriorityCombo.SelectedIndex switch
        {
            2 => "High",
            1 => "AboveNormal",
            3 => "BelowNormal",
            4 => "Low",
            _ => "Normal",
        };

        if (presetBefore != (s.ProcessPriority, s.OneCorePerClient, s.SmallWindows))
            PresetService.MarkCustom();
        SettingsService.Save();

        // A running watcher picks the change up by itself; with none running, start one so this
        // reaches the accounts that are open right now.
        if (ClientLimitsService.AnyLimitOn && RobloxProcesses.Count() > 0)
            ClientLimitsService.OnRobloxStarted();
    }

    private void GameDvr_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        GameDvrService.Apply(turnOff: ChkGameDvr.IsChecked == true);
    }

    private void RefreshNetworkRow()
    {
        var on = NetworkOptimizationService.HasBackup() || RobloxQosService.LegacyMarkingPresent;
        NetworkRow.Title = on ? "Network for farming - optimized" : "Network for farming";
        NetworkRestoreButton.IsEnabled = on;
    }

    private async void NetworkOptimize_Click(object sender, RoutedEventArgs e) => await RunNetwork(on: true);
    private async void NetworkRestore_Click(object sender, RoutedEventArgs e) => await RunNetwork(on: false);

    /// <summary>Both network changes in one elevated run - one administrator prompt.</summary>
    private async System.Threading.Tasks.Task RunNetwork(bool on)
    {
        var ran = await System.Threading.Tasks.Task.Run(() => RobloxQosService.RunElevated(on));
        RefreshNetworkRow();
        if (!ran) return; // the administrator prompt was declined - nothing changed

        await DialogHelper.ShowErrorAsync(this,
            on
                ? (NetworkOptimizationService.HasBackup()
                    ? "Done - the network is set up for farming. Exactly what was changed on this PC is in the log (About > Open logs)."
                    : "That didn't work - see the log (About > Open logs).")
                : "Done - every network setting is back to what it was before.",
            "Network for farming");
    }

    private async void ApplyAfk_Click(object sender, RoutedEventArgs e)
    {
        PresetService.Apply(Preset.Afk);
        LoadFromSettings();
        await DialogHelper.ShowErrorAsync(this,
            "AFK preset applied. It takes effect for accounts you launch from now on - limits also reach the ones already running within a few seconds.",
            "AFK preset applied");
    }

    private void Lightweight_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsService.Current;
        s.ShowFpsOverlay = false;
        s.DiscordRichPresence = false;
        s.ShowServerLocation = false;
        s.ShowServerNotification = false;
        SettingsService.Save();

        ActivityCoordinator.Stop();
        DiscordPresenceService.Stop();
        Log.Info("Lightweight mode: FPS overlay, Discord status and server-location popup turned off.");
        LoadFromSettings();
    }

    private async void CloseStuck_Click(object sender, RoutedEventArgs e)
    {
        await FarmingActions.CloseStuckAsync(this);
        RefreshStatus();
    }
}
