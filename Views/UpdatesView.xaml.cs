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

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
        VersionRow.Content = new System.Windows.Controls.TextBlock
        {
            Text = "v" + version,
            FontSize = 13,
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

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
        CheckRow.Description = "Checking...";

        var result = await UpdateCheckerService.CheckForUpdateAsync();

        CheckRow.Description = result.Status switch
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
