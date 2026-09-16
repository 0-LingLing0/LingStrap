using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Lingstrap.Views;

/// <summary>
/// A borderless, click-through banner shown over the Roblox window on join. Never shown if Roblox's
/// window can't be found - it never falls back to the screen centre.
/// </summary>
public partial class OverlayBannerWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint GW_HWNDPREV = 3;

    private static class Native
    {
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    }

    private readonly IntPtr _robloxHwnd;
    private System.Windows.Threading.DispatcherTimer? _followTimer;

    public OverlayBannerWindow(IntPtr robloxHwnd)
    {
        InitializeComponent();
        _robloxHwnd = robloxHwnd;
        Closed += (_, _) => _followTimer?.Stop();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var exStyle = Native.GetWindowLong(hwnd, GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);

            // Deliberately not Topmost: that would float this above every window on the desktop,
            // not just Roblox. Placing it directly above Roblox's own window in the normal z-order
            // instead means switching to any other app naturally covers both of them together,
            // exactly like a real overlay tied to that window would behave.
            //
            // SetWindowPos's hWndInsertAfter places this window immediately BEHIND the handle given
            // to it - passing Roblox's own hwnd there (the previous version's bug) put the banner
            // right behind Roblox, i.e. still hidden under it. To land in front of Roblox instead,
            // insert after whatever window currently sits just above Roblox in the z-order. If
            // nothing is above it, GetWindow returns IntPtr.Zero, which doubles as the HWND_TOP
            // constant - so this naturally puts the banner at the very top of the z-order in that case.
            if (_robloxHwnd != IntPtr.Zero)
            {
                var insertAfter = Native.GetWindow(_robloxHwnd, GW_HWNDPREV);
                Native.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        };
    }

    /// <summary>Builds and shows the overlay over the Roblox client that actually joined, or does nothing if there's no location or the window can't be found.</summary>
    public static void TryShow(string? locationText, string? sourceLogFile = null)
    {
        if (string.IsNullOrEmpty(locationText))
        {
            Services.Log.Info("Overlay banner skipped: no location resolved for this join.");
            return;
        }

        var located = Services.RobloxWindowLocator.FindClientRectForLogFile(sourceLogFile);
        if (located is not { } found)
        {
            Services.Log.Info("Overlay banner skipped: Roblox's window could not be found.");
            return;
        }

        var (rect, robloxHwnd) = found;
        var overlay = new OverlayBannerWindow(robloxHwnd);
        overlay.BuildContent(locationText);
        overlay.Reposition(rect);

        Services.Log.Info($"Overlay banner showing \"{locationText}\" at " +
                          $"({overlay.Left.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}," +
                          $"{overlay.Top.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}) {overlay.Width}x{overlay.Height}.");
        overlay.Show();
        overlay.Animate();
        overlay.StartFollowing();
        Services.Log.Info("Overlay banner Show() returned.");
    }

    /// <summary>Pins the banner centred just below Roblox's top edge, given its current client rect.</summary>
    private void Reposition(Rect robloxRect)
    {
        Left = robloxRect.Left + (robloxRect.Width - Width) / 2;
        Top = robloxRect.Top + 24;
    }

    /// <summary>
    /// Keeps the banner glued to the Roblox window while both are visible - without this, dragging
    /// or moving Roblox around during the few seconds the banner is up leaves it behind at the
    /// spot it was first shown at.
    /// </summary>
    private void StartFollowing()
    {
        _followTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _followTimer.Tick += (_, _) =>
        {
            if (Services.RobloxWindowLocator.GetClientRectQuiet(_robloxHwnd) is { } rect)
                Reposition(rect);
        };
        _followTimer.Start();
    }

    private void BuildContent(string locationText)
    {
        ContentPanel.Children.Clear();

        // A horizontal StackPanel measures its children with unconstrained width, so the text never
        // actually had a width to wrap or ellipsize against - it just overflowed past the window's
        // fixed 300px and got silently clipped. A Grid column properly bounds the text's width so
        // long locations wrap onto a second line instead.
        var row = new System.Windows.Controls.Grid();
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new SymbolIcon
        {
            Symbol = SymbolRegular.Location24,
            FontSize = 14,
            Foreground = System.Windows.Media.Brushes.White,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 6, 0),
        };
        System.Windows.Controls.Grid.SetColumn(icon, 0);

        var text = new TextBlock
        {
            Text = locationText,
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = System.Windows.TextWrapping.Wrap,
        };
        System.Windows.Controls.Grid.SetColumn(text, 1);

        row.Children.Add(icon);
        row.Children.Add(text);
        ContentPanel.Children.Add(row);
    }

    private void Animate()
    {
        const int fadeInMs = 150;
        const int fadeOutMs = 300;
        var totalMs = Math.Clamp(Services.SettingsService.Current.OverlayBannerSeconds, 1, 10) * 1000;

        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(fadeInMs))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(totalMs - fadeOutMs))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(totalMs))));
        animation.Completed += (_, _) => Close();

        BeginAnimation(OpacityProperty, animation);
    }
}
