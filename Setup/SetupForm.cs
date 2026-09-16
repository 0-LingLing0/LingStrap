using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Lingstrap.Setup;

public class SetupForm : Form
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoMoveSizeZOrder = 0x0002 | 0x0001 | 0x0004;

    private static readonly Color BackgroundColor = Color.FromArgb(0x20, 0x20, 0x20);
    private static readonly Color AccentColor = Color.FromArgb(0xA7, 0x78, 0xFF);
    private static readonly Color TextColor = Color.FromArgb(0xE8, 0xE8, 0xE8);
    private static readonly Color SubTextColor = Color.FromArgb(0x9A, 0x9A, 0x9A);
    private static readonly Color TrackColor = Color.FromArgb(0x38, 0x38, 0x38);

    private readonly Label _statusLabel;
    private readonly ProgressBarPanel _progressBar;

    public SetupForm()
    {
        Text = "Lingstrap Setup";
        ClientSize = new Size(420, 160);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BackgroundColor;

        var titleLabel = new Label
        {
            Text = "Lingstrap",
            Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold),
            ForeColor = AccentColor,
            BackColor = Color.Transparent,
            AutoSize = false,
            Location = new Point(28, 24),
            Size = new Size(364, 30),
        };

        _statusLabel = new Label
        {
            Text = "Preparing...",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = TextColor,
            BackColor = Color.Transparent,
            AutoSize = false,
            Location = new Point(28, 66),
            Size = new Size(364, 44),
        };

        _progressBar = new ProgressBarPanel(AccentColor, TrackColor)
        {
            Location = new Point(28, 118),
            Size = new Size(364, 6),
        };

        var subLabel = new Label
        {
            Text = "This only takes a moment.",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = SubTextColor,
            BackColor = Color.Transparent,
            AutoSize = false,
            Location = new Point(28, 132),
            Size = new Size(364, 20),
        };

        Controls.Add(titleLabel);
        Controls.Add(_statusLabel);
        Controls.Add(_progressBar);
        Controls.Add(subLabel);

        Load += async (_, _) => await RunInstallAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            var useDarkMode = 1;
            DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref useDarkMode, sizeof(int));
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SwpFrameChanged | SwpNoMoveSizeZOrder);
        }
        catch
        {
            // Pre-1809 Windows without DWM dark-mode support - a light title bar isn't worth failing over.
        }
    }

    private async Task RunInstallAsync()
    {
        try
        {
            _statusLabel.Text = "Checking for the latest version...";
            _progressBar.SetIndeterminate(true);
            var release = await InstallerService.GetLatestReleaseAsync();

            _statusLabel.Text = $"Downloading Lingstrap {release.Version}...";
            _progressBar.SetIndeterminate(false);

            await InstallerService.DownloadAndInstallAsync(release, (downloaded, total) =>
            {
                if (total <= 0) return;
                var percent = (int)(downloaded * 100 / total);
                BeginInvoke(() => _progressBar.Value = Math.Clamp(percent, 0, 100));
            });

            _statusLabel.Text = "Done - launching Lingstrap...";
            _progressBar.Value = 100;

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
            ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            ShowError($"Something went wrong: {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        MessageBox.Show(message, "Lingstrap Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Close();
    }
}

/// <summary>A flat, owner-drawn progress bar - WinForms' native ProgressBar renders as a dated
/// blocky Aero-style bar that clashed badly with Lingstrap's own dark Fluent look.</summary>
internal sealed class ProgressBarPanel : Panel
{
    private readonly Color _fill;
    private readonly Color _track;
    private readonly System.Windows.Forms.Timer _timer;
    private int _value;
    private bool _indeterminate;
    private float _phase;

    public int Value
    {
        get => _value;
        set { _value = value; Invalidate(); }
    }

    public ProgressBarPanel(Color fill, Color track)
    {
        _fill = fill;
        _track = track;
        DoubleBuffered = true;
        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += (_, _) =>
        {
            _phase += 0.02f;
            if (_phase > 1f) _phase -= 1f;
            Invalidate();
        };
    }

    public void SetIndeterminate(bool on)
    {
        _indeterminate = on;
        if (on) _timer.Start(); else _timer.Stop();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var trackBrush = new SolidBrush(_track))
            FillRounded(g, trackBrush, ClientRectangle);

        using var fillBrush = new SolidBrush(_fill);
        if (_indeterminate)
        {
            var barWidth = Width * 0.3f;
            var x = (Width + barWidth) * _phase - barWidth;
            var rect = RectangleF.Intersect(new RectangleF(x, 0, barWidth, Height), new RectangleF(0, 0, Width, Height));
            FillRounded(g, fillBrush, Rectangle.Round(rect));
        }
        else if (_value > 0)
        {
            var fillWidth = Math.Max((int)(Width * (_value / 100f)), Height);
            FillRounded(g, fillBrush, new Rectangle(0, 0, Math.Min(fillWidth, Width), Height));
        }
    }

    private static void FillRounded(Graphics g, Brush brush, Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var radius = rect.Height;
        using var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, radius, radius, 90, 180);
        path.AddArc(rect.Right - radius, rect.Y, radius, radius, 270, 180);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
