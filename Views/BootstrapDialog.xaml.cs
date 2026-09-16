using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lingstrap.Models;
using Lingstrap.Services;

namespace Lingstrap.Views;

/// <summary>The bootstrapper's loading dialog - implements ILaunchProgressDialog so LauncherService never sees this type directly.</summary>
public partial class BootstrapDialog : Window, ILaunchProgressDialog
{
    private const double TrackWidth = 580 - 142 - 40; // matches the XAML margins for the progress track
    private Storyboard? _indeterminateStoryboard;
    private DispatcherTimer? _percentTimer;
    private double _displayedPercent;
    private bool _closing;

    public event Action? CancelRequested;

    public BootstrapDialog()
    {
        InitializeComponent();

        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = (SystemParameters.PrimaryScreenHeight - Height) / 2;

        Opacity = 0;
        LogoImage.Source = IconRecolorService.GetIcon(ColorThemeCatalog.CurrentAccentColor());
        ColorAccentElements();
        BuildStripes();
        BuildIconGlow();
        StartShimmer();
        Loaded += (_, _) => FadeIn();
    }

    /// <summary>Tints the pieces that aren't animated/generated elsewhere: the title's accent bar and
    /// the ambient shadow behind the whole card (a soft colored glow instead of a plain black drop
    /// shadow reads as far more premium, and ties the card to whatever color the icon/progress bar
    /// are already using).</summary>
    private void ColorAccentElements()
    {
        var accent = ColorThemeCatalog.CurrentAccentColor();
        TitleAccentBar.Background = new SolidColorBrush(accent);
        AmbientShadow.Color = Color.FromRgb(
            (byte)(accent.R / 3), (byte)(accent.G / 3), (byte)(accent.B / 3));
    }

    /// <summary>Soft accent-tinted radial glow behind the logo, breathing slowly for a bit of life
    /// while the dialog otherwise just sits there waiting on the network/disk.</summary>
    private void BuildIconGlow()
    {
        var accent = ColorThemeCatalog.CurrentAccentColor();
        IconGlow.Fill = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x90, accent.R, accent.G, accent.B), 0),
                new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 1),
            },
        };

        var breathe = new DoubleAnimation
        {
            From = 0.35,
            To = 0.7,
            Duration = TimeSpan.FromSeconds(2.2),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        IconGlow.BeginAnimation(OpacityProperty, breathe);
    }

    /// <summary>A glossy highlight sweeping across whatever is currently filled - purely decorative,
    /// clipped to ProgressFill's own bounds so it never draws past the real progress.</summary>
    private void StartShimmer()
    {
        var slide = new DoubleAnimation
        {
            From = -46,
            To = TrackWidth + 46,
            Duration = TimeSpan.FromSeconds(1.6),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        ShimmerTransform.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    private void BuildStripes()
    {
        var accent = ColorThemeCatalog.CurrentAccentColor();

        // Two layers at different widths/speeds/opacities/directions for a subtle parallax feel,
        // rather than one flat scrolling pattern - the back layer is wider, dimmer and slower so it
        // reads as depth behind the sharper, faster foreground layer instead of just a second copy.
        BuildStripeLayer(StripesHostBack, accent, stripeWidth: 34, spacing: 74, opacity: 0x12,
            duration: TimeSpan.FromSeconds(6), reverse: false);
        BuildStripeLayer(StripesHost, accent, stripeWidth: 18, spacing: 46, opacity: 0x22,
            duration: TimeSpan.FromSeconds(3.5), reverse: true);
    }

    private static void BuildStripeLayer(Border host, Color accent, double stripeWidth, double spacing,
        byte opacity, TimeSpan duration, bool reverse)
    {
        const double coverSize = 950; // large enough that rotating 45deg still fully covers 580x220

        var canvas = new Canvas { Width = coverSize, Height = coverSize };
        var brush = new SolidColorBrush(Color.FromArgb(opacity, accent.R, accent.G, accent.B));

        for (var x = -coverSize; x < coverSize * 2; x += spacing)
        {
            var rect = new Rectangle { Width = stripeWidth, Height = coverSize * 3, Fill = brush };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, -coverSize);
            canvas.Children.Add(rect);
        }

        // Translate BEFORE rotating: sliding the pattern horizontally in its own local coordinate
        // space, then rotating the whole thing 45 degrees, is what makes the motion actually run
        // along the stripes' own diagonal length (a barber-pole scroll) instead of just sliding the
        // already-rotated shapes sideways.
        var scroll = new TranslateTransform();
        var transform = new TransformGroup();
        transform.Children.Add(scroll);
        transform.Children.Add(new RotateTransform(45));
        canvas.RenderTransform = transform;
        canvas.HorizontalAlignment = HorizontalAlignment.Center;
        canvas.VerticalAlignment = VerticalAlignment.Center;

        host.Child = canvas;

        // The foreground layer moves opposite the progress bar's own left-to-right fill/slide - one
        // spacing unit of travel loops seamlessly since the stripe pattern repeats every `spacing` px.
        var animation = new DoubleAnimation
        {
            From = 0,
            To = reverse ? -spacing : spacing,
            Duration = duration,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        scroll.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void FadeIn()
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));

        // A slight overshoot-then-settle on the logo reads as a much livelier entrance than a plain
        // fade, without touching the rest of the dialog (which fades in as a whole, unscaled).
        var pop = new DoubleAnimation
        {
            From = 0.6,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 },
        };
        LogoScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        LogoScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    private void RootBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CancelRequested?.Invoke();
        CloseDialog();
    }

    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = File.Exists(Log.FilePath) ? File.ReadAllText(Log.FilePath) : "(no log yet)";
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not copy log to clipboard: {ex.Message}");
        }
    }

    // --- ILaunchProgressDialog: all of these may be called from a background thread ---

    public void SetStatus(string status) => RunOnUi(() =>
        AnimateStatusChange(status, Color.FromRgb(0xA8, 0xAF, 0xBE))); // grey - revert in case ShowError turned it red

    public void SetProgress(double percent) => RunOnUi(() =>
    {
        StopIndeterminate();
        var clamped = Math.Clamp(percent, 0, 100);
        ProgressFill.BeginAnimation(FrameworkElement.WidthProperty,
            new DoubleAnimation(TrackWidth * clamped / 100.0, TimeSpan.FromMilliseconds(200)));
        AnimatePercentTo(clamped);
    });

    public void SetIndeterminate() => RunOnUi(() =>
    {
        StartIndeterminate();
        _percentTimer?.Stop();
        _displayedPercent = 0;
        PercentText.Text = "";
    });

    public void ShowError(string message) => RunOnUi(() =>
    {
        StopIndeterminate();
        AnimateStatusChange(message, Color.FromRgb(0xFF, 0x60, 0x70)); // red
        _percentTimer?.Stop();
        _displayedPercent = 0;
        PercentText.Text = "";
        CopyLogButton.Visibility = Visibility.Visible;
    });

    /// <summary>Counts the percentage up/down to the new value over the same span the bar itself
    /// takes to animate, instead of the number jumping straight to its new value - small touch, but a
    /// smoothly ticking number reads as noticeably more polished than a bar and number moving out of
    /// sync with each other.</summary>
    private void AnimatePercentTo(double target)
    {
        _percentTimer?.Stop();
        var start = _displayedPercent;
        var startTime = DateTime.UtcNow;
        const double durationMs = 200;

        _percentTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _percentTimer.Tick += (_, _) =>
        {
            var t = Math.Min(1, (DateTime.UtcNow - startTime).TotalMilliseconds / durationMs);
            _displayedPercent = start + (target - start) * t;
            PercentText.Text = $"{_displayedPercent:0}%";
            if (t >= 1) _percentTimer!.Stop();
        };
        _percentTimer.Start();
    }

    /// <summary>Crossfades StatusText to new text/colour instead of an abrupt swap - small touch, but
    /// a launch that visibly flickers through half a dozen status lines otherwise looks jumpy.</summary>
    private void AnimateStatusChange(string text, Color color)
    {
        var fadeOut = new DoubleAnimation(StatusText.Opacity, 0, TimeSpan.FromMilliseconds(90));
        fadeOut.Completed += (_, _) =>
        {
            StatusText.Text = text;
            StatusText.Foreground = new SolidColorBrush(color);
            StatusText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        };
        StatusText.BeginAnimation(OpacityProperty, fadeOut);
    }

    public void CloseDialog() => RunOnUi(() =>
    {
        if (_closing) return;
        _closing = true;

        var fadeOut = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(200));
        fadeOut.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fadeOut);
    });

    private void RunOnUi(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.Invoke(action);
    }

    private void StartIndeterminate()
    {
        if (_indeterminateStoryboard != null) return;

        ProgressFill.BeginAnimation(FrameworkElement.WidthProperty, null);
        ProgressFill.Width = TrackWidth * 0.3;

        var slide = new DoubleAnimation
        {
            From = -ProgressFill.Width,
            To = TrackWidth,
            Duration = TimeSpan.FromMilliseconds(1100),
            RepeatBehavior = RepeatBehavior.Forever,
        };

        var transform = new TranslateTransform();
        ProgressFill.RenderTransform = transform;
        _indeterminateStoryboard = new Storyboard();
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, new PropertyPath(TranslateTransform.XProperty));
        _indeterminateStoryboard.Children.Add(slide);
        _indeterminateStoryboard.Begin();
    }

    private void StopIndeterminate()
    {
        if (_indeterminateStoryboard is null) return;
        _indeterminateStoryboard.Stop();
        _indeterminateStoryboard = null;
        ProgressFill.RenderTransform = null;
    }
}
