using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Lingstrap.Setup;

public class SetupForm : Form
{
    private readonly Label _statusLabel;
    private readonly ProgressBar _progressBar;

    public SetupForm()
    {
        Text = "Lingstrap Setup";
        ClientSize = new Size(420, 120);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        _statusLabel = new Label
        {
            Text = "Preparing...",
            AutoSize = false,
            Location = new Point(20, 20),
            Size = new Size(380, 40),
        };

        _progressBar = new ProgressBar
        {
            Location = new Point(20, 65),
            Size = new Size(380, 24),
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
        };

        Controls.Add(_statusLabel);
        Controls.Add(_progressBar);

        Load += async (_, _) => await RunInstallAsync();
    }

    private async Task RunInstallAsync()
    {
        try
        {
            _statusLabel.Text = "Checking for the latest version...";
            var release = await InstallerService.GetLatestReleaseAsync();

            _statusLabel.Text = $"Downloading Lingstrap {release.Version}...";
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Maximum = 100;

            await InstallerService.DownloadAndInstallAsync(release, (downloaded, total) =>
            {
                if (total <= 0) return;
                var percent = (int)(downloaded * 100 / total);
                BeginInvoke(() => _progressBar.Value = Math.Clamp(percent, 0, 100));
            });

            _statusLabel.Text = "Done - launching Lingstrap...";
            Process.Start(new ProcessStartInfo
            {
                FileName = InstallerService.InstalledExePath,
                WorkingDirectory = InstallerService.InstallDir,
                UseShellExecute = true,
            });

            await Task.Delay(600);
            Close();
        }
        catch (InstallException ex)
        {
            MessageBox.Show(ex.Message, "Lingstrap Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Something went wrong: {ex.Message}", "Lingstrap Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }
}
