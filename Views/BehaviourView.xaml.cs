using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lingstrap.Models;
using Lingstrap.Services;

namespace Lingstrap.Views;

public partial class BehaviourView : Page
{
    private bool _loading = true;
    private bool _syncingBannerDuration;

    public BehaviourView()
    {
        InitializeComponent();

        var s = SettingsService.Current;
        ChkMulti.IsChecked          = s.MultiInstance;
        ChkCrashHandler.IsChecked   = s.CloseCrashHandler;
        ChkDedicatedGpu.IsChecked   = s.ForceDedicatedGpu;
        ChkCloseOnLaunch.IsChecked  = s.CloseLingstrapOnLaunch;
        ChkReopenOnClose.IsChecked  = s.ReopenLingstrapOnRobloxClose;
        ChkLoadingScreen.IsChecked  = s.ShowLoadingScreen;
        ChkPinCores.IsChecked       = s.PinClientsToCores;
        ChkServerLocation.IsChecked = s.ShowServerLocation;
        ChkNotification.IsChecked   = s.ShowServerNotification;
        ChkDiscord.IsChecked        = s.DiscordRichPresence;
        ChkFpsOverlay.IsChecked     = s.ShowFpsOverlay;
        FpsPositionCombo.SelectedIndex = (int)s.FpsOverlayPosition;

        BannerDurationSlider.Value = s.OverlayBannerSeconds;
        BannerDurationBox.Value = s.OverlayBannerSeconds;

        PriorityCombo.SelectedIndex = s.ProcessPriority switch
        {
            "High"        => 2,
            "AboveNormal" => 1,
            _             => 0,
        };

        RefreshCpuPinning();
        CpuAffinityService.AssignmentsChanged += OnCpuAssignmentsChanged;
        Unloaded += (_, _) => CpuAffinityService.AssignmentsChanged -= OnCpuAssignmentsChanged;

        PopulateMonitorMap();
        RefreshNetworkButtons();

        _loading = false;
    }

    private void RefreshNetworkButtons() =>
        RestoreNetworkButton.IsEnabled = NetworkOptimizationService.HasBackup();

    private void OnCpuAssignmentsChanged() => Dispatcher.Invoke(RefreshCpuPinning);

    /// <summary>Which PID got pinned to which core group - only meaningful while pinning is on and clients are running.</summary>
    private void RefreshCpuPinning()
    {
        if (!SettingsService.Current.PinClientsToCores || CpuAffinityService.CurrentAssignments.Count == 0)
        {
            CpuPinningCard.Visibility = Visibility.Collapsed;
            return;
        }

        CpuPinningCard.Visibility = Visibility.Visible;
        CpuAssignmentsPanel.Children.Clear();

        foreach (var (pid, label) in CpuAffinityService.CurrentAssignments)
        {
            var assignment = new TextBlock
            {
                Text = $"PID {pid}: {label}",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            };
            assignment.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            CpuAssignmentsPanel.Children.Add(assignment);
        }
    }

    private async void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var s = SettingsService.Current;
        var turningOnFpsOverlay = ChkFpsOverlay.IsChecked == true && !s.ShowFpsOverlay;

        s.MultiInstance           = ChkMulti.IsChecked == true;
        s.CloseCrashHandler       = ChkCrashHandler.IsChecked == true;
        s.ForceDedicatedGpu       = ChkDedicatedGpu.IsChecked == true;
        s.CloseLingstrapOnLaunch  = ChkCloseOnLaunch.IsChecked == true;
        s.ReopenLingstrapOnRobloxClose = ChkReopenOnClose.IsChecked == true;
        s.ShowLoadingScreen       = ChkLoadingScreen.IsChecked == true;
        s.PinClientsToCores       = ChkPinCores.IsChecked == true;
        s.ShowServerLocation      = ChkServerLocation.IsChecked == true;
        s.ShowServerNotification  = ChkNotification.IsChecked == true;
        s.DiscordRichPresence     = ChkDiscord.IsChecked == true;
        s.ShowFpsOverlay          = ChkFpsOverlay.IsChecked == true;

        SettingsService.Save();

        if (s.ShowServerLocation) ActivityCoordinator.Start(); else ActivityCoordinator.Stop();
        if (s.DiscordRichPresence) DiscordPresenceService.Start(); else DiscordPresenceService.Stop();
        if (s.PinClientsToCores) CpuAffinityService.Start(); else CpuAffinityService.Stop();
        RefreshCpuPinning();

        if (turningOnFpsOverlay)
        {
            if (FpsWatcherTaskService.TaskExists())
            {
                // Already there from an earlier session - just record that fact so
                // FpsOverlayCoordinator can trust the cached flag on every future launch instead of
                // re-querying schtasks itself each time.
                SettingsService.Current.FpsWatcherTaskConfirmed = true;
                SettingsService.Save();
            }
            else
            {
                await EnsureFpsWatcherTaskAsync();
            }
        }

        if (s.ForceDedicatedGpu)
        {
            var exe = RobloxLocator.FindPlayerExe();
            if (exe != null) GpuPreferenceService.Apply(exe);
        }
        else
        {
            GpuPreferenceService.RemoveAllManaged();
        }
    }

    private void FpsPositionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Current.FpsOverlayPosition = (FpsOverlayPosition)FpsPositionCombo.SelectedIndex;
        SettingsService.Save();
    }

    /// <summary>One-time setup for the FPS overlay's scheduled task (see FpsWatcherTaskService) - a
    /// single admin approval now instead of a fresh UAC prompt every time Roblox launches
    /// afterwards. Turns the toggle back off if the user declines or setup otherwise fails, so the
    /// setting doesn't end up on with nothing actually working behind it.</summary>
    private async Task EnsureFpsWatcherTaskAsync()
    {
        var confirmed = await DialogHelper.ShowConfirmAsync(this,
            "This needs a one-time setup step so the FPS overlay can run with administrator rights " +
            "every time Roblox launches, without asking again after this. Windows will ask you to " +
            "approve running as administrator just this once.",
            "Set up the FPS overlay?", confirmText: "Continue");

        if (confirmed)
        {
            var created = await Task.Run(FpsWatcherTaskService.TryCreateTask);
            if (created)
            {
                SettingsService.Current.FpsWatcherTaskConfirmed = true;
                SettingsService.Save();
                return;
            }
            await DialogHelper.ShowErrorAsync(this, "Could not set this up - the FPS overlay won't turn on until you try again.");
        }

        ChkFpsOverlay.IsChecked = false;
        SettingsService.Current.ShowFpsOverlay = false;
        SettingsService.Save();
    }

    // ---- overlay banner duration (slider + type-in box, bounded 1-10 seconds) ----

    private void BannerDurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // BannerDurationBox can still be null here: setting Minimum/Maximum in XAML coerces the
        // slider's Value during InitializeComponent() itself, before later-declared elements have
        // been constructed yet.
        if (_syncingBannerDuration || BannerDurationBox == null) return;
        _syncingBannerDuration = true;
        BannerDurationBox.Value = e.NewValue;
        _syncingBannerDuration = false;
    }

    private void BannerDurationSlider_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e) => CommitBannerDuration();
    private void BannerDurationSlider_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => CommitBannerDuration();

    private void CommitBannerDuration()
    {
        if (_loading) return;
        SettingsService.Current.OverlayBannerSeconds = (int)Math.Round(BannerDurationSlider.Value);
        SettingsService.Save();
    }

    private void BannerDurationBox_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (_syncingBannerDuration) return;
        var clamped = Math.Clamp(BannerDurationBox.Value ?? 1, 1, 10);

        _syncingBannerDuration = true;
        BannerDurationBox.Value = clamped;
        BannerDurationSlider.Value = clamped;
        _syncingBannerDuration = false;

        if (_loading) return;
        SettingsService.Current.OverlayBannerSeconds = (int)clamped;
        SettingsService.Save();
    }

    /// <summary>Enter confirms a NumberBox's typed value immediately, instead of only committing once focus moves elsewhere on its own.</summary>
    private void NumberBox_EnterCommits(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        if (sender is System.Windows.DependencyObject element)
        {
            var scope = System.Windows.Input.FocusManager.GetFocusScope(element);
            System.Windows.Input.FocusManager.SetFocusedElement(scope, null);
        }
        System.Windows.Input.Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void PriorityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        SettingsService.Current.ProcessPriority = PriorityCombo.SelectedIndex switch
        {
            2 => "High",
            1 => "AboveNormal",
            _ => "Normal",
        };
        PresetService.MarkCustom();
        SettingsService.Save();
    }

    // ---- preferred launch monitor (mini map, click a tile to select it) ----

    private void PopulateMonitorMap()
    {
        MonitorMapCanvas.Children.Clear();

        var monitors = MonitorService.GetMonitors();
        if (monitors.Count == 0) return;

        var minX = monitors.Min(m => m.Bounds.Left);
        var minY = monitors.Min(m => m.Bounds.Top);
        var maxX = monitors.Max(m => m.Bounds.Right);
        var maxY = monitors.Max(m => m.Bounds.Bottom);

        const double maxPreviewWidth = 380;
        const double maxPreviewHeight = 130;
        var scale = Math.Min(maxPreviewWidth / (maxX - minX), maxPreviewHeight / (maxY - minY));

        MonitorMapCanvas.Width = (maxX - minX) * scale;
        MonitorMapCanvas.Height = (maxY - minY) * scale;

        var selected = SettingsService.Current.PreferredMonitorDeviceName;
        var accent = ColorThemeCatalog.CurrentAccentColor();
        var selectedFill = new SolidColorBrush(Color.FromArgb(0x1F, accent.R, accent.G, accent.B));

        foreach (var monitor in monitors)
        {
            var isSelected = monitor.DeviceName == selected;

            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            // Each tile is a plain Border, so there is no templated parent for these to inherit a
            // foreground from, and WPF's default for an unstyled TextBlock is black - invisible on
            // this theme. Same trap the cursor slots fell into once the CardExpander around them
            // went away: any TextBlock built in code has to be told its colour explicitly.
            var sizeText = new TextBlock
            {
                Text = $"{monitor.Bounds.Width}x{monitor.Bounds.Height}",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };
            sizeText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            content.Children.Add(sizeText);

            if (monitor.IsPrimary)
            {
                var primaryText = new TextBlock
                {
                    Text = "Primary",
                    FontSize = 9,
                    Opacity = 0.7,
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                primaryText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                content.Children.Add(primaryText);
            }

            var tile = new Border
            {
                Width = Math.Max(monitor.Bounds.Width * scale - 4, 20),
                Height = Math.Max(monitor.Bounds.Height * scale - 4, 20),
                Background = isSelected ? selectedFill : (Brush)TryFindResource("ControlFillColorDefaultBrush"),
                BorderBrush = (Brush)TryFindResource(isSelected ? "SystemAccentBrush" : "ControlStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(isSelected ? 2 : 1),
                CornerRadius = new CornerRadius(4),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = content,
            };

            var deviceName = monitor.DeviceName;
            tile.MouseLeftButtonUp += (_, _) =>
            {
                SettingsService.Current.PreferredMonitorDeviceName = deviceName;
                SettingsService.Save();
                PopulateMonitorMap();
            };

            Canvas.SetLeft(tile, (monitor.Bounds.Left - minX) * scale + 2);
            Canvas.SetTop(tile, (monitor.Bounds.Top - minY) * scale + 2);
            MonitorMapCanvas.Children.Add(tile);
        }
    }

    private void UseDefaultMonitor_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Current.PreferredMonitorDeviceName = null;
        SettingsService.Save();
        PopulateMonitorMap();
    }

    // ---- network optimization (runs the actual changes in an elevated helper process) ----

    private async void OptimizeNetwork_Click(object sender, RoutedEventArgs e)
    {
        var confirmed = await DialogHelper.ShowConfirmAsync(this,
            "This disables network adapter power saving, removes Windows' default cap on network " +
            "packet processing, and turns off Energy-Efficient Ethernet / Interrupt Moderation where " +
            "your driver exposes them. These apply system-wide, not just to Roblox, and Windows will " +
            "ask you to approve running as administrator. Every value it touches is backed up first, " +
            "so Restore can undo this afterward.",
            "Optimize network for lower ping?", confirmText: "Continue");
        if (!confirmed) return;

        await RunElevatedNetworkHelper("-networkoptimize",
            "Done - check the log (About page) for exactly what was found and changed on this machine, since it depends on your specific network hardware.",
            "Network optimization finished", "Could not run network optimization");
    }

    private async void RestoreNetwork_Click(object sender, RoutedEventArgs e)
    {
        var confirmed = await DialogHelper.ShowConfirmAsync(this,
            "This puts every network setting Lingstrap changed back to exactly what it was before - " +
            "Windows will ask you to approve running as administrator.",
            "Restore network settings?", confirmText: "Restore");
        if (!confirmed) return;

        await RunElevatedNetworkHelper("-networkrestore",
            "Done - your network settings are back to what they were before.",
            "Network settings restored", "Could not restore network settings");
    }

    private async Task RunElevatedNetworkHelper(string arguments, string successMessage, string successTitle, string failurePrefix)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            await DialogHelper.ShowErrorAsync(this, "Could not resolve Lingstrap's own exe path.");
            return;
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
            });

            if (process != null)
                await Task.Run(() => process.WaitForExit());

            RefreshNetworkButtons();
            await DialogHelper.ShowErrorAsync(this, successMessage, successTitle);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The user clicked "No" on the UAC prompt - not an error, just declined.
            Log.Info($"{arguments}: administrator elevation was declined.");
        }
        catch (Exception ex)
        {
            Log.Error(failurePrefix, ex);
            await DialogHelper.ShowErrorAsync(this, $"{failurePrefix}:\n{ex.Message}");
        }
    }
}
