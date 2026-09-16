using System.Threading.Tasks;
using System.Windows;
using Wpf.Ui.Controls;

namespace Lingstrap.Views;

/// <summary>
/// Themed replacements for System.Windows.MessageBox, using the app's own ui:ContentDialogHost
/// (registered once, in MainWindow.xaml) so error/confirmation popups match the dark/violet theme
/// instead of rendering as a stock Windows dialog.
/// </summary>
public static class DialogHelper
{
    /// <summary>An OK-only dialog for a genuine error - not for anything that could just update the UI silently instead.</summary>
    public static async Task ShowErrorAsync(FrameworkElement owner, string message, string title = "Lingstrap")
    {
        var host = ContentDialogHost.GetForWindow(Window.GetWindow(owner));
        if (host == null)
        {
            System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        var dialog = new ContentDialog(host)
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }

    /// <summary>
    /// A Yes/Cancel dialog for a real, consequential decision - e.g. an irreversible restore, delete,
    /// or applying an imported file. `content` is usually a string, but can be any FrameworkElement
    /// (a diff list, for instance) when a plain message isn't enough context to decide on.
    /// </summary>
    public static async Task<bool> ShowConfirmAsync(FrameworkElement owner, object content, string title, string confirmText = "Yes", string cancelText = "Cancel")
    {
        var host = ContentDialogHost.GetForWindow(Window.GetWindow(owner));
        if (host == null)
        {
            var text = content as string ?? content.ToString() ?? "";
            return System.Windows.MessageBox.Show(text, title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning)
                   == System.Windows.MessageBoxResult.Yes;
        }

        var dialog = new ContentDialog(host)
        {
            Title = title,
            Content = content,
            PrimaryButtonText = confirmText,
            CloseButtonText = cancelText,
            DefaultButton = ContentDialogButton.Close,
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    /// <summary>A single-line text prompt - e.g. naming a new saved preset. Returns the trimmed text,
    /// or null if cancelled or empty.</summary>
    public static async Task<string?> ShowInputAsync(FrameworkElement owner, string title, string placeholder = "",
        string defaultValue = "", string confirmText = "Save")
    {
        var host = ContentDialogHost.GetForWindow(Window.GetWindow(owner));
        if (host == null) return null;

        var textBox = new Wpf.Ui.Controls.TextBox { Text = defaultValue, PlaceholderText = placeholder };

        var dialog = new ContentDialog(host)
        {
            Title = title,
            Content = textBox,
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Loaded += (_, _) => { textBox.Focus(); textBox.SelectAll(); };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;

        var text = textBox.Text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
