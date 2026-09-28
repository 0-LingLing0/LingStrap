using System;
using System.Collections.Generic;
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
    // Must match the XAML margins for the progress track. It was still the old layout's 580-142-40
    // after the card was rebuilt, so the fill ran 25% short - at 100% the bar only reached about
    // three quarters across.
    private const double TrackWidth = 580 - 24 - 24;
    private Storyboard? _indeterminateStoryboard;
    private DispatcherTimer? _percentTimer;
    private double _displayedPercent;
    private bool _closing;

    public event Action? CancelRequested;

    public BootstrapDialog()
    {
        InitializeComponent();

        // Was a hardcoded "v0.1.0" in XAML, never actually reflecting the exe's own version (which CI
        // bakes in from the release tag at build time - see AboutView for the same computation).
        VersionText.Text = "v" + UpdateCheckerService.CurrentVersionText;

        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = (SystemParameters.PrimaryScreenHeight - Height) / 2;

        Opacity = 0;
        LogoImage.Source = IconRecolorService.GetIcon(ColorThemeCatalog.CurrentAccentColor());
        ColorAccentElements();
        BuildAtmosphere();
        DescribeSetup();
        StartShimmer();
        Loaded += (_, _) => FadeIn();
    }

    /// <summary>Tints the ambient shadow behind the card - a soft coloured glow rather than a plain
    /// black drop shadow, tied to whatever colour the icon and progress bar already use.</summary>
    private void ColorAccentElements()
    {
        var accent = ColorThemeCatalog.CurrentAccentColor();
        AmbientShadow.Color = Color.FromRgb(
            (byte)(accent.R / 3), (byte)(accent.G / 3), (byte)(accent.B / 3));
    }

    /// <summary>
    /// Two accent auroras drifting behind the card at different speeds and directions, plus a halo
    /// breathing behind the logo. This is the depth the old diagonal stripes were reaching for: they
    /// moved, but as a flat pattern scrolling past, which reads as a texture rather than as space.
    /// Blurred blobs travelling on their own paths give parallax instead, and because nothing has a
    /// hard edge there is no repeating motif for the eye to lock onto - it stays atmosphere rather
    /// than becoming the thing you look at.
    /// </summary>
    private void BuildAtmosphere()
    {
        var accent = ColorThemeCatalog.CurrentAccentColor();

        AuroraA.Fill = AccentGradient(accent, 0x96);
        AuroraB.Fill = AccentGradient(accent, 0x6E);
        LogoHalo.Fill = AccentGradient(accent, 0xB4);

        // Different periods on each axis so the two never fall into a visible loop together.
        Drift(AuroraATransform, TranslateTransform.XProperty, 0, 46, 9.0);
        Drift(AuroraATransform, TranslateTransform.YProperty, 0, 30, 13.0);
        Drift(AuroraBTransform, TranslateTransform.XProperty, 0, -54, 11.0);
        Drift(AuroraBTransform, TranslateTransform.YProperty, 0, -26, 7.5);

        Breathe(AuroraA, 0.42, 0.72, 6.5);
        Breathe(AuroraB, 0.30, 0.58, 8.5);
        Breathe(LogoHalo, 0.35, 0.75, 2.6);
    }

    private static RadialGradientBrush AccentGradient(Color accent, byte peakAlpha) => new()
    {
        GradientStops =
        {
            new GradientStop(Color.FromArgb(peakAlpha, accent.R, accent.G, accent.B), 0),
            new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 1),
        },
    };

    private static void Drift(TranslateTransform target, DependencyProperty axis, double from, double to, double seconds)
    {
        target.BeginAnimation(axis, new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromSeconds(seconds),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private static void Breathe(UIElement target, double from, double to, double seconds)
    {
        target.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromSeconds(seconds),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    /// <summary>What this launch is about to apply, so the wait has a subject rather than being a
    /// bar with a spinner's worth of information.</summary>
    private void DescribeSetup()
    {
        try
        {
            var s = SettingsService.Current;
            var parts = new List<string> { Models.PresetInfo.Name(s.ActivePreset) };

            if (s.ManageFastFlags && s.CustomFlags.Count > 0)
                parts.Add($"{s.CustomFlags.Count} FastFlags");
            if (s.ModsEnabled)
                parts.Add("mods on");

            SetupText.Text = string.Join("  ·  ", parts);
        }
        catch (Exception ex)
        {
            // Cosmetic only - never let a summary line stop a launch from showing its dialog.
            Log.Warn($"Could not describe the launch setup: {ex.Message}");
            SetupText.Text = "";
        }
    }

    /// <summary>
    /// Lights the step dots from the progress percentage. The launch has five real stages (version
    /// check, FastFlags, mods, starting Roblox, waiting for its window) and LaunchAsync already
    /// reports progress at their boundaries, so this needs no extra plumbing. It matters most on the
    /// last stage, which is the longest by far - eight seconds of a bar that barely moves reads as
    /// frozen, where "four of five done" reads as working.
    /// </summary>
    private void UpdateStepDots(double percent)
    {
        var accent = new SolidColorBrush(ColorThemeCatalog.CurrentAccentColor());
        var idle = new SolidColorBrush(Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF));

        var reached = percent switch
        {
            >= 98 => 5,
            >= 85 => 4,
            >= 60 => 3,
            >= 40 => 2,
            _ => 1,
        };

        var dots = new[] { Dot1, Dot2, Dot3, Dot4, Dot5 };
        for (var i = 0; i < dots.Length; i++)
        {
            var isDone = i < reached;
            var wasDone = ReferenceEquals(dots[i].Fill, _litBrush);
            dots[i].Fill = isDone ? accent : idle;
            dots[i].BeginAnimation(OpacityProperty, null);
            dots[i].Opacity = 1;

            // The dot that just lit pops, so reaching a stage registers as an event rather than a
            // silent colour change nobody was watching for.
            if (isDone && i == reached - 1 && !wasDone)
                PopDot(dots[i]);
        }

        // The stage in progress keeps pulsing. It matters on the last one: waiting for Roblox's own
        // window is over eight seconds, and a bar that barely moves for that long reads as frozen.
        if (reached < dots.Length)
        {
            dots[reached].Fill = accent;
            dots[reached].Opacity = 0.35;
            Breathe(dots[reached], 0.22, 0.85, 1.1);
        }

        _litBrush = accent;
        StageText.Text = $"Step {reached} of 5";
    }

    private SolidColorBrush? _litBrush;

    private static void PopDot(UIElement dot)
    {
        var scale = new ScaleTransform(1, 1);
        dot.RenderTransformOrigin = new Point(0.5, 0.5);
        dot.RenderTransform = scale;

        var pop = new DoubleAnimation
        {
            From = 0.4,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 1.1 },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
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

    private void FadeIn()
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));

        // The card rises into place rather than just appearing - a plain fade on something this size
        // reads as a screenshot being switched on.
        var rise = new TranslateTransform();
        RootBorder.RenderTransform = rise;
        rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = 18,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(460),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

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
        UpdateStepDots(clamped);
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
