using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Lingstrap.Services;
using Wpf.Ui.Controls;

namespace Lingstrap.Views;

/// <summary>
/// The "what's new" dialog shown when an update is found - shared between the silent startup check
/// (MainWindow, UpdateCheckMode.Notify) and the manual "Check now" button (UpdatesView) so both
/// behave identically. Offers Update now (downloads and launches LingstrapSetup.exe, then closes
/// this process so it can replace this exe), Open release page, or Later.
/// </summary>
public static class UpdateDialogHelper
{
    public static async Task ShowAsync(FrameworkElement owner, UpdateCheckerService.UpdateCheckResult result)
    {
        var host = ContentDialogHost.GetForWindow(Window.GetWindow(owner));
        if (host == null) return;

        var dialog = new ContentDialog(host)
        {
            Title = $"Version {result.LatestVersion} is available - what's new",
            Content = BuildReleaseNotesContent(result.ReleaseNotes),
            PrimaryButtonText = result.SetupDownloadUrl != null ? "Update now" : string.Empty,
            SecondaryButtonText = result.ReleaseUrl != null ? "GitHub" : string.Empty,
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Close,
        };

        var choice = await dialog.ShowAsync();

        if (choice == ContentDialogResult.Primary && result.SetupDownloadUrl != null)
        {
            var started = await UpdateCheckerService.DownloadAndLaunchSetupAsync(result.SetupDownloadUrl);
            if (started)
                Application.Current.Shutdown();
            else
                await DialogHelper.ShowErrorAsync(owner, "Could not download the update - check your connection and try again from the Updates page.");
        }
        else if (choice == ContentDialogResult.Secondary && result.ReleaseUrl != null)
        {
            Process.Start(new ProcessStartInfo { FileName = result.ReleaseUrl, UseShellExecute = true });
        }
    }

    internal static FrameworkElement BuildReleaseNotesContent(string? notes)
    {
        return new ScrollViewer
        {
            MaxHeight = 320,
            MaxWidth = 480,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = string.IsNullOrWhiteSpace(notes) ? "(No release notes provided.)" : notes,
                TextWrapping = TextWrapping.Wrap,
            },
        };
    }
}
