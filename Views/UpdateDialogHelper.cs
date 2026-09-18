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
    /// <summary>Returns false if the dialog could not be shown at all, so the caller can avoid
    /// recording the version as "announced" when nothing was actually announced.</summary>
    public static async Task<bool> ShowAsync(FrameworkElement owner, UpdateCheckerService.UpdateCheckResult result)
    {
        var host = ContentDialogHost.GetForWindow(Window.GetWindow(owner));
        if (host == null)
        {
            Log.Warn("Update dialog: no ContentDialogHost on this window yet - nothing was shown.");
            return false;
        }

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

        return true;
    }

    /// <summary>Same release notes, shown after the fact instead of as an install prompt - for the
    /// first launch of a version that UpdateCheckMode.AutoInstall already installed silently with no
    /// prior notice.</summary>
    public static async Task<bool> ShowWhatsNewAsync(FrameworkElement owner, UpdateCheckerService.UpdateCheckResult result)
    {
        var host = ContentDialogHost.GetForWindow(Window.GetWindow(owner));
        if (host == null)
        {
            // Left pending deliberately: the caller only records the version as announced when this
            // returns true, so a silent AutoInstall still gets to explain itself on the next launch.
            Log.Warn("What's-new dialog: no ContentDialogHost on this window yet - nothing was shown.");
            return false;
        }

        var dialog = new ContentDialog(host)
        {
            Title = $"Now on version {result.LatestVersion} - what's new",
            Content = BuildReleaseNotesContent(result.ReleaseNotes),
            SecondaryButtonText = result.ReleaseUrl != null ? "GitHub" : string.Empty,
            CloseButtonText = "Got it",
            DefaultButton = ContentDialogButton.Close,
        };

        var choice = await dialog.ShowAsync();

        if (choice == ContentDialogResult.Secondary && result.ReleaseUrl != null)
            Process.Start(new ProcessStartInfo { FileName = result.ReleaseUrl, UseShellExecute = true });

        return true;
    }

    internal static FrameworkElement BuildReleaseNotesContent(string? notes)
    {
        // Explicit foreground: a TextBlock built in code defaults to black, which on this theme is
        // the difference between release notes and an empty dialog.
        var text = new System.Windows.Controls.TextBlock
        {
            Text = string.IsNullOrWhiteSpace(notes) ? "(No release notes provided.)" : notes,
            TextWrapping = TextWrapping.Wrap,
        };
        text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

        return new ScrollViewer
        {
            MaxHeight = 320,
            MaxWidth = 480,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = text,
        };
    }
}
