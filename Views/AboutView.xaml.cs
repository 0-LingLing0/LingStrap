using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Lingstrap.Services;

namespace Lingstrap.Views;

public partial class AboutView : Page
{
    private string? _pendingReleaseUrl;
    private bool _loading = true;

    public AboutView()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
        InfoText.Text = $"Version {version}\nData folder: {Paths.Root}";

        ChkAutoUpdate.IsChecked = SettingsService.Current.AutoCheckForUpdates;
        _loading = false;
    }

    /// <summary>Builds the scrollable "what was fixed" content shared by the manual check-now button
    /// and the silent startup check, so both show release notes the same way.</summary>
    internal static FrameworkElement BuildReleaseNotesContent(string? notes)
    {
        return new ScrollViewer
        {
            MaxHeight = 320,
            MaxWidth = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(notes) ? "(No release notes provided.)" : notes,
                TextWrapping = TextWrapping.Wrap,
            },
        };
    }

    private void AutoUpdateToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Current.AutoCheckForUpdates = ChkAutoUpdate.IsChecked == true;
        SettingsService.Save();
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        OpenReleaseButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "Checking...";

        var result = await UpdateCheckerService.CheckForUpdateAsync();

        UpdateStatusText.Text = result.Status switch
        {
            UpdateCheckerService.UpdateStatus.UpToDate => $"You're up to date (v{result.LatestVersion}).",
            UpdateCheckerService.UpdateStatus.UpdateAvailable => $"Version {result.LatestVersion} is available - you're on an older one.",
            _ => result.Message ?? "Could not check for updates.",
        };

        if (result.Status == UpdateCheckerService.UpdateStatus.UpdateAvailable && result.ReleaseUrl != null)
        {
            _pendingReleaseUrl = result.ReleaseUrl;
            OpenReleaseButton.Visibility = Visibility.Visible;
            CheckUpdateButton.IsEnabled = true;

            var openRelease = await DialogHelper.ShowConfirmAsync(this, BuildReleaseNotesContent(result.ReleaseNotes),
                $"Version {result.LatestVersion} - what's new", confirmText: "Open release page", cancelText: "Close");
            if (openRelease)
                Process.Start(new ProcessStartInfo { FileName = _pendingReleaseUrl, UseShellExecute = true });
            return;
        }

        CheckUpdateButton.IsEnabled = true;
    }

    private void OpenRelease_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingReleaseUrl != null)
            Process.Start(new ProcessStartInfo { FileName = _pendingReleaseUrl, UseShellExecute = true });
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Paths.EnsureCreated();
        Process.Start(new ProcessStartInfo { FileName = Paths.Logs, UseShellExecute = true });
    }

    private async void ForceCleanReinstall_Click(object sender, RoutedEventArgs e)
    {
        var confirmed = await DialogHelper.ShowConfirmAsync(this,
            "This deletes your entire current Roblox install. The next launch will download and " +
            "install it completely fresh, which can take a few minutes depending on your connection. " +
            "This can't be undone.",
            "Force a clean reinstall?", confirmText: "Delete install");
        if (!confirmed) return;

        if (RobloxInstallerService.DeleteCurrentInstall())
            await DialogHelper.ShowErrorAsync(this, "Done - the next launch will install Roblox fresh.", "Clean reinstall queued");
        else
            await DialogHelper.ShowErrorAsync(this, "Could not delete the current install - see the log for details.");
    }
}
