using System;
using System.Threading.Tasks;
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

        // Reopen at the same screen position the old window was at, rather than snapping back to the
        // XAML-default centered placement, when this process was launched by AppearanceView's Apply
        // restart from a window the user had moved.
        if (App.WindowPositionOverride is { } pos)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = pos.X;
            Top = pos.Y;
        }
        if (App.WindowSizeOverride is { } size)
        {
            Width = size.Width;
            Height = size.Height;
        }

        var icon = IconRecolorService.GetIcon(ColorThemeCatalog.CurrentAccentColor());
        Icon = icon;
        TitleBarIcon.Source = icon;

        ApplyFontScale(SettingsService.Current.FontScalePercent);

        // Deliberately not calling SystemThemeWatcher.Watch: it forces the window to follow
        // the OS theme, which would undo the fixed dark theme + custom accent set at startup.
        // FluentWindow's own WindowBackdropType="Mica" (set in XAML) still applies Mica on its own.
        // An Apply-triggered restart always originates from the Appearance page - land back there
        // instead of Home, so applying a color/theme doesn't also bounce the user to a different page.
        var startPage = App.WindowPositionOverride != null ? typeof(Views.AppearanceView) : typeof(Views.HomeView);
        Loaded += (_, _) => RootNavigation.Navigate(startPage);
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

    /// <summary>
    /// Starts a replacement process (reopening at this window's screen position and landing back on
    /// the Appearance page) and waits until it actually has a window up before returning, so the
    /// caller can close this window immediately after with no gap where neither window is on screen.
    /// A real restart is still required to apply a new accent color or light/dark theme - WPF-UI's
    /// stock control styles resolve accent-colored brushes once, when its ControlsDictionary first
    /// merges into Application.Resources at startup, not live - see AppearanceView's pending-preview
    /// setup for why the page itself no longer needs any covering animation to make that restart feel
    /// intentional: the user already saw the new look in the preview and pressed Apply themselves.
    /// </summary>
    public async Task PrepareRestartAsync()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            var left = Left.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var top = Top.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var width = ActualWidth.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var height = ActualHeight.ToString(System.Globalization.CultureInfo.InvariantCulture);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"-windowpos:{left},{top} -windowsize:{width},{height}",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not relaunch Lingstrap to apply settings: {ex.Message}");
            return;
        }

        await WaitForReplacementWindowAsync();
    }

    /// <summary>Polls for another Lingstrap process (the one PrepareRestartAsync just started) to
    /// actually have a window up, rather than guessing a fixed delay - so the old window never closes
    /// (revealing whatever's behind it) before the replacement genuinely has something to show.</summary>
    private static async Task WaitForReplacementWindowAsync()
    {
        var ownId = Environment.ProcessId;
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName("Lingstrap"))
            {
                if (proc.Id != ownId && proc.MainWindowHandle != IntPtr.Zero)
                    return;
            }
            await Task.Delay(80);
        }
    }

    /// <summary>Silently checks GitHub once per launch per UpdateCheckMode: Off skips entirely,
    /// Notify shows the "update available" prompt every launch until it's actually installed (no
    /// more one-time nag - LastSeenUpdateVersion no longer gates this), AutoInstall installs it with
    /// no prompt. Either way, once a launch finds itself already on a version it hasn't announced yet
    /// (LastSeenUpdateVersion behind the current version) - the only way to reach that is having just
    /// installed silently via AutoInstall, since Notify's own prompt already records the version it
    /// showed - it shows a "what's new" notice instead, so a silent install still tells you what changed.</summary>
    private async System.Threading.Tasks.Task CheckForUpdateOnStartupAsync()
    {
        var mode = SettingsService.Current.UpdateMode;
        if (mode == UpdateCheckMode.Off) return;

        var result = await UpdateCheckerService.CheckForUpdateAsync();

        if (result.Status == UpdateCheckerService.UpdateStatus.UpdateAvailable)
        {
            if (mode == UpdateCheckMode.AutoInstall)
            {
                if (result.SetupDownloadUrl != null && await UpdateCheckerService.DownloadAndLaunchSetupAsync(result.SetupDownloadUrl))
                    Application.Current.Shutdown();
                return;
            }

            await UpdateDialogHelper.ShowAsync(this, result);
            SettingsService.Current.LastSeenUpdateVersion = result.LatestVersion;
            SettingsService.Save();
            return;
        }

        if (result.Status == UpdateCheckerService.UpdateStatus.UpToDate &&
            result.LatestVersion != null && result.LatestVersion != SettingsService.Current.LastSeenUpdateVersion)
        {
            SettingsService.Current.LastSeenUpdateVersion = result.LatestVersion;
            SettingsService.Save();
            await UpdateDialogHelper.ShowWhatsNewAsync(this, result);
        }
    }

    /// <summary>Also called directly (with no MainWindow at all) when Roblox is launched from the
    /// browser and CloseLingstrapOnLaunch is off - see App.HandleRobloxLaunch.</summary>
    internal static void StartBackgroundServices()
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
