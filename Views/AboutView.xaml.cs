using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Lingstrap.Services;

namespace Lingstrap.Views;

public partial class AboutView : Page
{
    public AboutView()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
        InfoText.Text = $"Version {version}\nData folder: {Paths.Root}";
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
