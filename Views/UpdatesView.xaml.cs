using System.Windows;
using System.Windows.Controls;
using Lingstrap.Models;
using Lingstrap.Services;

namespace Lingstrap.Views;

public partial class UpdatesView : Page
{
    private bool _loading = true;

    public UpdatesView()
    {
        InitializeComponent();

        UpdateModeCombo.SelectedIndex = (int)SettingsService.Current.UpdateMode;
        _loading = false;
    }

    private void UpdateModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Current.UpdateMode = (UpdateCheckMode)UpdateModeCombo.SelectedIndex;
        SettingsService.Save();
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking...";

        var result = await UpdateCheckerService.CheckForUpdateAsync();

        UpdateStatusText.Text = result.Status switch
        {
            UpdateCheckerService.UpdateStatus.UpToDate => $"You're up to date (v{result.LatestVersion}).",
            UpdateCheckerService.UpdateStatus.UpdateAvailable => $"Version {result.LatestVersion} is available.",
            _ => result.Message ?? "Could not check for updates.",
        };

        CheckUpdateButton.IsEnabled = true;

        if (result.Status == UpdateCheckerService.UpdateStatus.UpdateAvailable)
            await UpdateDialogHelper.ShowAsync(this, result);
    }
}
