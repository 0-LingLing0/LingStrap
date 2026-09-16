using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Lingstrap.Models;
using Lingstrap.Services;
using Wpf.Ui.Controls;

namespace Lingstrap.Views;

public partial class AppearanceView : Page
{
    private bool _loading = true;

    // Nothing here is saved or applied until Apply is clicked - accent color and light/dark theme
    // both require a real process restart to take effect (see the RestartAppAsync doc comment below),
    // so rather than restarting the moment you touch a swatch or the theme toggle, every change here
    // just updates this pending state and the preview surface, and Apply commits it all at once.
    private string _pendingAccentName = null!;
    private Color? _pendingCustomColor;
    private bool _pendingLightTheme;

    public AppearanceView()
    {
        InitializeComponent();

        _pendingAccentName = SettingsService.Current.AccentTheme;
        _pendingCustomColor = SettingsService.Current.CustomAccentColor != null
            ? ColorThemeCatalog.ParseCustomColor(SettingsService.Current.CustomAccentColor)
            : null;
        _pendingLightTheme = SettingsService.Current.LightTheme;

        Populate();
        LightThemeToggle.IsChecked = _pendingLightTheme;
        SelectClosestFontScale(SettingsService.Current.FontScalePercent);
        UpdatePreview();
        _loading = false;
    }

    private Color PendingAccentColor => _pendingAccentName == ColorThemeCatalog.CustomThemeName
        ? _pendingCustomColor ?? ColorThemeCatalog.Find(SettingsService.Current.AccentTheme).Base
        : ColorThemeCatalog.Find(_pendingAccentName).Base;

    private void PendingSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _pendingLightTheme = LightThemeToggle.IsChecked == true;
        UpdatePreview();
        UpdateApplyButtonState();
    }

    /// <summary>Manually-colored stand-ins for real WPF-UI controls, rather than the real
    /// button/toggle - those resolve their accent brush once at startup (see RestartAppAsync), so they
    /// wouldn't move until after the very restart this preview exists to preview before committing.</summary>
    private void UpdatePreview()
    {
        var accent = PendingAccentColor;
        var background = _pendingLightTheme ? Color.FromRgb(0xF4, 0xF4, 0xF7) : Color.FromRgb(0x16, 0x17, 0x1F);
        var foreground = _pendingLightTheme ? Color.FromRgb(0x1B, 0x1B, 0x1F) : Colors.White;

        PreviewSurface.Background = new SolidColorBrush(background);
        PreviewText.Foreground = new SolidColorBrush(foreground);
        PreviewButton.Background = new SolidColorBrush(accent);
        PreviewToggleTrack.Background = new SolidColorBrush(accent);
    }

    private void UpdateApplyButtonState() => ApplyButton.IsEnabled = IsDirty();

    private bool IsDirty()
    {
        if (_pendingLightTheme != SettingsService.Current.LightTheme) return true;
        if (_pendingAccentName != SettingsService.Current.AccentTheme) return true;
        if (_pendingAccentName != ColorThemeCatalog.CustomThemeName) return false;

        var pendingHex = _pendingCustomColor is { } c ? ColorThemeCatalog.ToHex(c) : null;
        return pendingHex != SettingsService.Current.CustomAccentColor;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Current.AccentTheme = _pendingAccentName;
        if (_pendingAccentName == ColorThemeCatalog.CustomThemeName && _pendingCustomColor is { } color)
            SettingsService.Current.CustomAccentColor = ColorThemeCatalog.ToHex(color);
        SettingsService.Current.LightTheme = _pendingLightTheme;
        SettingsService.Save();

        ApplyButton.IsEnabled = false;
        await RestartAppAsync();
    }

    /// <summary>Picks whichever preset is numerically closest to a stored value, in case an older
    /// build stored a percentage (from the slider this used to be) that isn't one of the presets.</summary>
    private void SelectClosestFontScale(int percent)
    {
        ComboBoxItem? closest = null;
        var closestDiff = int.MaxValue;
        foreach (ComboBoxItem item in FontScaleCombo.Items)
        {
            var diff = Math.Abs(int.Parse((string)item.Tag) - percent);
            if (diff >= closestDiff) continue;
            closestDiff = diff;
            closest = item;
        }
        FontScaleCombo.SelectedItem = closest;
    }

    private void FontScaleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FontScaleCombo.SelectedItem is not ComboBoxItem item) return;
        var percent = int.Parse((string)item.Tag);
        (Window.GetWindow(this) as MainWindow)?.ApplyFontScale(percent);
        SettingsService.Current.FontScalePercent = percent;
        SettingsService.Save();
    }

    private void Populate()
    {
        SwatchPanel.Children.Clear();
        var current = _pendingAccentName;

        foreach (var theme in ColorThemeCatalog.All)
        {
            var isSelected = theme.Name == current;
            var name = theme.Name;
            SwatchPanel.Children.Add(BuildSwatch(theme.Base, theme.Name, isSelected, () => Select(name)));
        }

        var isCustomSelected = current == ColorThemeCatalog.CustomThemeName;
        // _pendingCustomColor always, not just while custom is the active theme - switching to a
        // built-in preset and back should still find your last custom color waiting in this slot
        // instead of resetting it to a blank "add" button.
        SwatchPanel.Children.Add(BuildCustomSwatch(_pendingCustomColor, isCustomSelected));
    }

    private static Border BuildSwatch(Color color, string tooltip, bool isSelected, Action onSelect)
    {
        var swatch = new Border
        {
            Width = 60,
            Height = 60,
            Margin = new Thickness(0, 0, 14, 14),
            CornerRadius = new CornerRadius(30),
            Background = new SolidColorBrush(color),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(isSelected ? 3 : 0),
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            Effect = new DropShadowEffect
            {
                Color = color,
                BlurRadius = 14,
                ShadowDepth = 0,
                Opacity = 0.55,
            },
        };

        if (isSelected)
        {
            swatch.Child = new SymbolIcon
            {
                Symbol = SymbolRegular.Checkmark24,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        swatch.MouseLeftButtonUp += (_, _) => onSelect();
        swatch.MouseEnter += (_, _) => AnimateSwatchScale(swatch, 1.08);
        swatch.MouseLeave += (_, _) => AnimateSwatchScale(swatch, 1.0);

        return swatch;
    }

    /// <summary>The last tile in the row: a dashed "add" circle normally, or the picked color itself
    /// with a permanent pencil icon once a custom color is the active theme - the pencil (rather than
    /// the checkmark every other swatch uses) is what signals "click to edit", so it never looks like
    /// just another plain preset swatch. Selection is still shown the normal way, via the white ring
    /// border BuildSwatch's siblings use - this just skips BuildSwatch to always show the pencil
    /// instead of switching to a checkmark when selected.</summary>
    private Border BuildCustomSwatch(Color? activeCustomColor, bool isSelected)
    {
        if (activeCustomColor is { } color)
        {
            var customSwatch = new Border
            {
                Width = 60,
                Height = 60,
                Margin = new Thickness(0, 0, 14, 14),
                CornerRadius = new CornerRadius(30),
                Background = new SolidColorBrush(color),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(isSelected ? 3 : 0),
                Cursor = Cursors.Hand,
                ToolTip = "Custom - click to edit",
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
                Effect = new DropShadowEffect { Color = color, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.55 },
                Child = new SymbolIcon
                {
                    Symbol = SymbolRegular.Edit24,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            customSwatch.MouseLeftButtonUp += (_, _) => OpenCustomColorPicker();
            customSwatch.MouseEnter += (_, _) => AnimateSwatchScale(customSwatch, 1.08);
            customSwatch.MouseLeave += (_, _) => AnimateSwatchScale(customSwatch, 1.0);

            return customSwatch;
        }

        // Transparent background means this swatch's only contrast comes from its own border/icon
        // color against the page behind it - a translucent white (fine on the normal dark page) was
        // nearly invisible on the light theme variant, since that page background is itself close to
        // white. Picks the dim color from whichever theme is actually running right now (the page's
        // real background doesn't follow _pendingLightTheme until the restart Apply triggers).
        var dim = SettingsService.Current.LightTheme
            ? Color.FromArgb(0x80, 0x1B, 0x1B, 0x1F)
            : Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);

        var swatch = new Border
        {
            Width = 60,
            Height = 60,
            Margin = new Thickness(0, 0, 14, 14),
            CornerRadius = new CornerRadius(30),
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(dim),
            BorderThickness = new Thickness(2),
            Cursor = Cursors.Hand,
            ToolTip = "Pick a custom color",
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            Child = new SymbolIcon
            {
                Symbol = SymbolRegular.Add24,
                Foreground = new SolidColorBrush(dim),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        swatch.MouseLeftButtonUp += (_, _) => OpenCustomColorPicker();
        swatch.MouseEnter += (_, _) => AnimateSwatchScale(swatch, 1.08);
        swatch.MouseLeave += (_, _) => AnimateSwatchScale(swatch, 1.0);

        return swatch;
    }

    private static void AnimateSwatchScale(Border swatch, double to)
    {
        if (swatch.RenderTransform is not ScaleTransform transform) return;
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void Select(string name)
    {
        if (name == _pendingAccentName) return;

        _pendingAccentName = name;
        Populate();
        UpdatePreview();
        UpdateApplyButtonState();
    }

    private async void OpenCustomColorPicker()
    {
        // Falls back to the last custom color ever picked, even if a catalog preset is currently
        // pending - otherwise switching to a preset and back to "+" would lose the color you'd
        // picked and force you to start over instead of just reopening this.
        var initial = _pendingCustomColor ?? PendingAccentColor;

        var picked = initial;
        var content = BuildColorPickerContent(initial, c => picked = c);

        var confirmed = await DialogHelper.ShowConfirmAsync(this, content, "Pick a custom color", confirmText: "Use this color");
        if (!confirmed) return;

        _pendingAccentName = ColorThemeCatalog.CustomThemeName;
        _pendingCustomColor = picked;

        Populate();
        UpdatePreview();
        UpdateApplyButtonState();
    }

    private const int WheelSize = 200;

    /// <summary>A click/drag hue+saturation wheel, one "darkness" (HSV value) slider, a hex box, and a
    /// live preview swatch, all kept in sync. WPF-UI 4.3.0's own ColorPicker control is an internal,
    /// unimplemented stub, so this is hand-built rather than reusing a library control.</summary>
    private static FrameworkElement BuildColorPickerContent(Color initial, Action<Color> onChanged)
    {
        var panel = new StackPanel { Width = WheelSize + 40 };

        var preview = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(28),
            Background = new SolidColorBrush(initial),
            Margin = new Thickness(0, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        panel.Children.Add(preview);

        var hexBox = new System.Windows.Controls.TextBox
        {
            Text = ColorThemeCatalog.ToHex(initial),
            MaxLength = 6,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(30, 0, 30, 16),
        };
        panel.Children.Add(hexBox);

        var wheelCanvas = new Canvas
        {
            Width = WheelSize,
            Height = WheelSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16),
            Cursor = Cursors.Hand,
        };
        wheelCanvas.Children.Add(new System.Windows.Controls.Image { Width = WheelSize, Height = WheelSize, Source = BuildColorWheelBitmap(WheelSize) });

        var indicator = new Ellipse
        {
            Width = 14,
            Height = 14,
            Stroke = Brushes.White,
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };
        wheelCanvas.Children.Add(indicator);
        panel.Children.Add(wheelCanvas);

        var darknessRow = new StackPanel { Orientation = Orientation.Horizontal };
        darknessRow.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Darkness", Width = 62, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
        });
        var darknessSlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Width = WheelSize - 62,
            VerticalAlignment = VerticalAlignment.Center,
            IsDirectionReversed = true, // slider reads left (bright) -> right (dark), matching the label
        };
        darknessRow.Children.Add(darknessSlider);
        panel.Children.Add(darknessRow);

        var (initHue, initSat, initValue) = RgbToHsv(initial.R, initial.G, initial.B);
        var hue = initHue;
        var sat = initSat;
        darknessSlider.Value = initValue * 100;

        void UpdateIndicatorPosition()
        {
            var angleRad = hue * Math.PI / 180.0;
            var radius = sat * (WheelSize / 2.0);
            var cx = WheelSize / 2.0 + radius * Math.Cos(angleRad);
            var cy = WheelSize / 2.0 + radius * Math.Sin(angleRad);
            Canvas.SetLeft(indicator, cx - indicator.Width / 2);
            Canvas.SetTop(indicator, cy - indicator.Height / 2);
        }
        UpdateIndicatorPosition();

        var updating = false;

        void ApplyFromWheelOrSlider()
        {
            if (updating) return;
            updating = true;
            var (r, g, b) = HsvToRgb(hue, sat, darknessSlider.Value / 100.0);
            var color = Color.FromRgb(r, g, b);
            preview.Background = new SolidColorBrush(color);
            hexBox.Text = ColorThemeCatalog.ToHex(color);
            updating = false;
            onChanged(color);
        }

        void PickFromPoint(Point p)
        {
            var dx = p.X - WheelSize / 2.0;
            var dy = p.Y - WheelSize / 2.0;
            var radius = Math.Min(Math.Sqrt(dx * dx + dy * dy), WheelSize / 2.0);
            var angle = Math.Atan2(dy, dx);
            if (angle < 0) angle += 2 * Math.PI;
            hue = angle * 180.0 / Math.PI;
            sat = radius / (WheelSize / 2.0);
            UpdateIndicatorPosition();
            ApplyFromWheelOrSlider();
        }

        wheelCanvas.MouseLeftButtonDown += (_, e) =>
        {
            wheelCanvas.CaptureMouse();
            PickFromPoint(e.GetPosition(wheelCanvas));
        };
        wheelCanvas.MouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed) PickFromPoint(e.GetPosition(wheelCanvas));
        };
        wheelCanvas.MouseLeftButtonUp += (_, _) => wheelCanvas.ReleaseMouseCapture();

        darknessSlider.ValueChanged += (_, _) => ApplyFromWheelOrSlider();

        hexBox.TextChanged += (_, _) =>
        {
            if (updating) return;
            var text = hexBox.Text.Trim().TrimStart('#');
            if (text.Length != 6 || !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return;

            var color = Color.FromRgb((byte)((parsed >> 16) & 0xFF), (byte)((parsed >> 8) & 0xFF), (byte)(parsed & 0xFF));
            var (h, s, v) = RgbToHsv(color.R, color.G, color.B);

            updating = true;
            hue = h;
            sat = s;
            darknessSlider.Value = v * 100;
            UpdateIndicatorPosition();
            preview.Background = new SolidColorBrush(color);
            updating = false;
            onChanged(color);
        };

        return panel;
    }

    /// <summary>Hue = angle around the center, saturation = distance from center, value fixed at 1 -
    /// the "darkness" slider scales value separately once a hue/saturation is picked, so the wheel
    /// itself always shows the full-brightness ring like a standard color-wheel picker.</summary>
    private static WriteableBitmap BuildColorWheelBitmap(int size)
    {
        var bitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[size * size * 4];
        var center = size / 2.0;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - center;
                var dy = y - center;
                var radius = Math.Sqrt(dx * dx + dy * dy);
                if (radius > center) continue; // leave fully transparent outside the circle

                var angle = Math.Atan2(dy, dx);
                if (angle < 0) angle += 2 * Math.PI;
                var hue = angle * 180.0 / Math.PI;
                var sat = Math.Min(radius / center, 1.0);
                var (r, g, b) = HsvToRgb(hue, sat, 1.0);

                var idx = (y * size + x) * 4;
                pixels[idx] = b;
                pixels[idx + 1] = g;
                pixels[idx + 2] = r;
                pixels[idx + 3] = 255;
            }
        }

        bitmap.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static (double Hue, double Sat, double Value) RgbToHsv(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        var v = max;
        var s = max == 0 ? 0 : delta / max;

        double h;
        if (delta == 0) h = 0;
        else if (max == r) h = 60 * (((g - b) / delta) % 6);
        else if (max == g) h = 60 * ((b - r) / delta + 2);
        else h = 60 * ((r - g) / delta + 4);
        if (h < 0) h += 360;

        return (h, s, v);
    }

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;

        var (r1, g1, b1) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return ((byte)Math.Round((r1 + m) * 255), (byte)Math.Round((g1 + m) * 255), (byte)Math.Round((b1 + m) * 255));
    }

    /// <summary>
    /// WPF-UI's default control styles (Button, Slider, ToggleSwitch, and every other stock control)
    /// resolve their accent-colored brushes once, when its ControlsDictionary is first merged into
    /// Application.Resources at startup - that resolution is baked into the shared Style objects
    /// every instance of a control uses, not a live binding that reacts to
    /// ApplicationAccentColorManager.Apply() being called again later. A previous attempt at this
    /// only swapped in a fresh MainWindow, which fixed pages that read the accent color themselves
    /// (these swatches, Presets/Behaviour's highlight fills) but left every native WPF-UI control
    /// exactly as it was, since a new window doesn't re-merge those dictionaries - only a fresh
    /// process does. Actually restarting is the only reliable fix.
    ///
    /// No covering animation plays over the swap - the preview above already showed what's coming,
    /// and Apply was a deliberate click, so the window closing and a new one appearing right after
    /// reads as "my restart," not a crash. MainWindow.PrepareRestartAsync still starts the replacement
    /// and waits for its window to exist before this returns, so there's no gap where neither window
    /// is on screen.
    /// </summary>
    private async System.Threading.Tasks.Task RestartAppAsync()
    {
        if (Window.GetWindow(this) is MainWindow window)
            await window.PrepareRestartAsync();

        Application.Current.Shutdown();
    }
}
