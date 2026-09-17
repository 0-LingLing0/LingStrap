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
        var content = BuildColorPickerContent(initial, ColorThemeCatalog.CurrentAccentColor(), c => picked = c);

        var confirmed = await DialogHelper.ShowConfirmAsync(this, content, "Pick a custom color", confirmText: "Use this color");
        if (!confirmed) return;

        _pendingAccentName = ColorThemeCatalog.CustomThemeName;
        _pendingCustomColor = picked;

        Populate();
        UpdatePreview();
        UpdateApplyButtonState();
    }

    private const int WheelSize = 200;
    private const int BrightnessBarWidth = 26;

    /// <summary>
    /// A click/drag hue+saturation wheel with a brightness bar beside it, the color being replaced
    /// shown next to the one being picked, a hex field, R/G/B readouts and the catalog colors as
    /// starting points - laid out in two columns so the wheel doesn't squeeze everything else into a
    /// strip beneath it. WPF-UI 4.3.0's own ColorPicker control is an internal, unimplemented stub,
    /// so this is hand-built rather than reusing a library control.
    /// </summary>
    private static FrameworkElement BuildColorPickerContent(Color initial, Color current, Action<Color> onChanged)
    {
        var (initHue, initSat, initValue) = RgbToHsv(initial.R, initial.G, initial.B);
        double hue = initHue, sat = initSat, value = initValue;
        var updating = false;

        // ---- left column: wheel + brightness bar ----
        var wheelCanvas = new Canvas { Width = WheelSize, Height = WheelSize, Cursor = Cursors.Hand };
        wheelCanvas.Children.Add(new System.Windows.Controls.Image
        {
            Width = WheelSize, Height = WheelSize, Source = BuildColorWheelBitmap(WheelSize),
        });

        // Filled with the picked color and ringed in white over a dark shadow, so it stays visible on
        // a pale yellow just as well as on a deep blue - the old plain white ring vanished on light hues.
        var indicator = new Ellipse
        {
            Width = 18,
            Height = 18,
            Stroke = Brushes.White,
            StrokeThickness = 2,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 5, Opacity = 0.85 },
        };
        wheelCanvas.Children.Add(indicator);

        var barStops = new GradientStopCollection { new(initial, 0), new(Colors.Black, 1) };
        var barBrush = new LinearGradientBrush(barStops) { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };

        var barCanvas = new Canvas
        {
            Width = BrightnessBarWidth, Height = WheelSize, Cursor = Cursors.Hand, Background = Brushes.Transparent,
        };
        barCanvas.Children.Add(new Border
        {
            Width = BrightnessBarWidth,
            Height = WheelSize,
            CornerRadius = new CornerRadius(BrightnessBarWidth / 2.0),
            Background = barBrush,
        });
        var barHandle = new Border
        {
            Width = BrightnessBarWidth + 6,
            Height = 10,
            CornerRadius = new CornerRadius(4),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 5, Opacity = 0.85 },
        };
        Canvas.SetLeft(barHandle, -3);
        barCanvas.Children.Add(barHandle);

        var leftColumn = new StackPanel { Orientation = Orientation.Horizontal };
        leftColumn.Children.Add(wheelCanvas);
        leftColumn.Children.Add(new Border { Width = 12 }); // gap
        leftColumn.Children.Add(barCanvas);

        // ---- right column: what you're changing, and the numbers behind it ----
        var newSwatch = new Border
        {
            Height = 54,
            Background = new SolidColorBrush(initial),
            CornerRadius = new CornerRadius(0, 8, 8, 0),
        };
        var compare = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        compare.ColumnDefinitions.Add(new ColumnDefinition());
        compare.ColumnDefinitions.Add(new ColumnDefinition());
        var oldSwatch = new Border
        {
            Height = 54,
            Background = new SolidColorBrush(current),
            CornerRadius = new CornerRadius(8, 0, 0, 8),
        };
        Grid.SetColumn(oldSwatch, 0);
        Grid.SetColumn(newSwatch, 1);
        compare.Children.Add(oldSwatch);
        compare.Children.Add(newSwatch);

        var hexBox = new System.Windows.Controls.TextBox
        {
            Text = ColorThemeCatalog.ToHex(initial),
            MaxLength = 6,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontFamily = new FontFamily("Consolas"),
            VerticalContentAlignment = VerticalAlignment.Center,
            MinWidth = 90,
        };
        var hexRow = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 2, 10, 2),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 14),
        };
        hexRow.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        hexRow.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        var hexInner = new StackPanel { Orientation = Orientation.Horizontal };
        var hash = new System.Windows.Controls.TextBlock
        {
            Text = "#", FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6,
        };
        hexInner.Children.Add(hash);
        hexInner.Children.Add(hexBox);
        hexRow.Child = hexInner;

        var rgbGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        for (var i = 0; i < 3; i++) rgbGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var rgbValues = new System.Windows.Controls.TextBlock[3];
        for (var i = 0; i < 3; i++)
        {
            var cell = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0),
            };
            cell.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
            cell.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");

            var stack = new StackPanel();
            var caption = new System.Windows.Controls.TextBlock
            {
                Text = i switch { 0 => "R", 1 => "G", _ => "B" }, FontSize = 10, FontWeight = FontWeights.SemiBold, Opacity = 0.6,
            };
            rgbValues[i] = new System.Windows.Controls.TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 13 };
            stack.Children.Add(caption);
            stack.Children.Add(rgbValues[i]);
            cell.Child = stack;

            Grid.SetColumn(cell, i);
            rgbGrid.Children.Add(cell);
        }

        var presets = new WrapPanel();

        var rightColumn = new StackPanel { MinWidth = 196, Margin = new Thickness(20, 0, 0, 0) };
        rightColumn.Children.Add(SectionLabel("Current  /  New"));
        rightColumn.Children.Add(compare);
        rightColumn.Children.Add(SectionLabel("Hex"));
        rightColumn.Children.Add(hexRow);
        rightColumn.Children.Add(rgbGrid);
        rightColumn.Children.Add(SectionLabel("Theme colors"));
        rightColumn.Children.Add(presets);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(leftColumn, 0);
        Grid.SetColumn(rightColumn, 1);
        root.Children.Add(leftColumn);
        root.Children.Add(rightColumn);

        // ---- keeping every piece in sync ----
        void Redraw(bool skipHexBox)
        {
            var (r, g, b) = HsvToRgb(hue, sat, value);
            var color = Color.FromRgb(r, g, b);

            newSwatch.Background = new SolidColorBrush(color);
            indicator.Fill = new SolidColorBrush(color);
            rgbValues[0].Text = r.ToString();
            rgbValues[1].Text = g.ToString();
            rgbValues[2].Text = b.ToString();
            if (!skipHexBox) hexBox.Text = ColorThemeCatalog.ToHex(color);

            // The bar always runs from this hue/saturation at full brightness down to black, so it
            // previews the range it's actually moving through rather than an abstract 0-100.
            var (br, bg, bb) = HsvToRgb(hue, sat, 1.0);
            barStops[0] = new GradientStop(Color.FromRgb(br, bg, bb), 0);

            var angleRad = hue * Math.PI / 180.0;
            var radius = sat * (WheelSize / 2.0);
            Canvas.SetLeft(indicator, WheelSize / 2.0 + radius * Math.Cos(angleRad) - indicator.Width / 2);
            Canvas.SetTop(indicator, WheelSize / 2.0 + radius * Math.Sin(angleRad) - indicator.Height / 2);
            Canvas.SetTop(barHandle, (1 - value) * WheelSize - barHandle.Height / 2);

            onChanged(color);
        }

        void PickFromWheel(Point p)
        {
            var dx = p.X - WheelSize / 2.0;
            var dy = p.Y - WheelSize / 2.0;
            var radius = Math.Min(Math.Sqrt(dx * dx + dy * dy), WheelSize / 2.0);
            var angle = Math.Atan2(dy, dx);
            if (angle < 0) angle += 2 * Math.PI;
            hue = angle * 180.0 / Math.PI;
            sat = radius / (WheelSize / 2.0);
            Redraw(false);
        }

        wheelCanvas.MouseLeftButtonDown += (_, e) => { wheelCanvas.CaptureMouse(); PickFromWheel(e.GetPosition(wheelCanvas)); };
        wheelCanvas.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) PickFromWheel(e.GetPosition(wheelCanvas)); };
        wheelCanvas.MouseLeftButtonUp += (_, _) => wheelCanvas.ReleaseMouseCapture();

        void PickFromBar(Point p)
        {
            value = Math.Clamp(1 - p.Y / WheelSize, 0, 1);
            Redraw(false);
        }

        barCanvas.MouseLeftButtonDown += (_, e) => { barCanvas.CaptureMouse(); PickFromBar(e.GetPosition(barCanvas)); };
        barCanvas.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) PickFromBar(e.GetPosition(barCanvas)); };
        barCanvas.MouseLeftButtonUp += (_, _) => barCanvas.ReleaseMouseCapture();

        hexBox.TextChanged += (_, _) =>
        {
            if (updating) return;
            var text = hexBox.Text.Trim().TrimStart('#');
            if (text.Length != 6 || !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return;

            var color = Color.FromRgb((byte)((parsed >> 16) & 0xFF), (byte)((parsed >> 8) & 0xFF), (byte)(parsed & 0xFF));
            (hue, sat, value) = RgbToHsv(color.R, color.G, color.B);

            updating = true;
            Redraw(true); // leave the box alone while it's being typed into
            updating = false;
        };

        foreach (var theme in ColorThemeCatalog.All)
        {
            var swatch = new Border
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 0, 7, 7),
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(theme.Base),
                Cursor = Cursors.Hand,
                ToolTip = $"Start from {theme.Name}",
            };
            var baseColor = theme.Base;
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                (hue, sat, value) = RgbToHsv(baseColor.R, baseColor.G, baseColor.B);
                Redraw(false);
            };
            presets.Children.Add(swatch);
        }

        Redraw(false);
        return root;
    }

    private static System.Windows.Controls.TextBlock SectionLabel(string text)
    {
        var label = new System.Windows.Controls.TextBlock
        {
            Text = text.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.6,
            Margin = new Thickness(0, 0, 0, 6),
        };
        label.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        return label;
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
