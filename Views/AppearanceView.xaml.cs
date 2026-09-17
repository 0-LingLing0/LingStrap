using System;
using System.Collections.Generic;
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

        BuildPicker();
        BuildThemeChooser();
        BuildScaleChooser();
        UpdatePreview();
        _loading = false;
    }

    private Color PendingAccentColor => _pendingAccentName == ColorThemeCatalog.CustomThemeName
        ? _pendingCustomColor ?? ColorThemeCatalog.Find(SettingsService.Current.AccentTheme).Base
        : ColorThemeCatalog.Find(_pendingAccentName).Base;

    // ---- theme and scale choosers ------------------------------------------------------------

    /// <summary>Dark and Light as two labelled options rather than one unlabelled switch - a toggle
    /// left you to infer that "off" meant dark, and gave the light option no name at all.</summary>
    private void BuildThemeChooser()
    {
        Action<int>? select = null;
        var (root, setSelected) = Segmented.Build(new[] { "Dark", "Light" }, _pendingLightTheme ? 1 : 0, index =>
        {
            _pendingLightTheme = index == 1;
            select?.Invoke(index);
            UpdatePreview();
            UpdateApplyButtonState();
        });
        select = setSelected;
        ThemeHost.Child = root;
    }

    private static readonly int[] ScalePercents = { 80, 90, 100, 110, 125, 150 };

    private void BuildScaleChooser()
    {
        // Whichever preset is closest to the stored value, in case an older build saved a percentage
        // (from the slider this used to be) that isn't one of them.
        var stored = SettingsService.Current.FontScalePercent;
        var closest = 0;
        for (var i = 1; i < ScalePercents.Length; i++)
        {
            if (Math.Abs(ScalePercents[i] - stored) < Math.Abs(ScalePercents[closest] - stored)) closest = i;
        }

        Action<int>? select = null;
        var labels = Array.ConvertAll(ScalePercents, p => p + "%");
        var (root, setSelected) = Segmented.Build(labels, closest, index =>
        {
            select?.Invoke(index);
            if (_loading) return;

            var percent = ScalePercents[index];
            (Window.GetWindow(this) as MainWindow)?.ApplyFontScale(percent);
            SettingsService.Current.FontScalePercent = percent;
            SettingsService.Save();
        });
        select = setSelected;
        ScaleHost.Child = root;
    }

    // ---- preview -----------------------------------------------------------------------------

    /// <summary>
    /// A small stand-in for the real window rather than real WPF-UI controls: those resolve their
    /// accent brush once at startup (see RestartAppAsync), so they wouldn't move until after the very
    /// restart this preview exists to preview before committing. Showing a nav rail, a card and a
    /// couple of controls together puts the accent on the surfaces it actually lands on, which one
    /// loose sample button never did.
    /// </summary>
    private void UpdatePreview()
    {
        var accent = PendingAccentColor;
        var accentBrush = new SolidColorBrush(accent);

        var background = _pendingLightTheme ? Color.FromRgb(0xF4, 0xF4, 0xF7) : Color.FromRgb(0x16, 0x17, 0x1F);
        var chrome = _pendingLightTheme ? Color.FromRgb(0xEA, 0xEA, 0xF0) : Color.FromRgb(0x1C, 0x1D, 0x27);
        var card = _pendingLightTheme ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x22, 0x23, 0x2E);
        var foreground = _pendingLightTheme ? Color.FromRgb(0x1B, 0x1B, 0x1F) : Colors.White;
        var muted = _pendingLightTheme ? Color.FromRgb(0x5F, 0x63, 0x76) : Color.FromRgb(0x8B, 0x90, 0xA6);

        PreviewSurface.Background = new SolidColorBrush(background);
        PreviewTitleBar.Background = new SolidColorBrush(chrome);
        PreviewInnerCard.Background = new SolidColorBrush(card);
        PreviewTrack.Background = new SolidColorBrush(chrome);

        PreviewLogo.Background = accentBrush;
        PreviewButton.Background = accentBrush;
        PreviewToggleTrack.Background = accentBrush;
        PreviewNavBar.Background = accentBrush;
        PreviewFill.Background = accentBrush;
        // The same faint accent wash the real nav rail uses behind its selected item.
        PreviewNavActive.Background = new SolidColorBrush(Color.FromArgb(0x24, accent.R, accent.G, accent.B));

        var text = new SolidColorBrush(foreground);
        var mutedText = new SolidColorBrush(muted);
        PreviewTitle.Foreground = mutedText;
        PreviewHeading.Foreground = text;
        PreviewNavActiveText.Foreground = text;
        PreviewNavIdle1.Foreground = mutedText;
        PreviewNavIdle2.Foreground = mutedText;
        PreviewCardText.Foreground = mutedText;
        PreviewButtonText.Foreground = new SolidColorBrush(IsLight(accent) ? Color.FromRgb(0x1B, 0x1B, 0x1F) : Colors.White);
    }

    /// <summary>Perceived brightness, so label text on an accent-filled button stays readable on a
    /// pale yellow as well as on a deep indigo.</summary>
    private static bool IsLight(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000 > 140;

    private void UpdateApplyButtonState()
    {
        var dirty = IsDirty();
        ApplyButton.IsEnabled = dirty;
        DirtyHint.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
    }

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

    private const int WheelSize = 180;
    private const int BrightnessBarWidth = 24;

    /// <summary>
    /// The whole accent picker, built straight into the page: a click/drag hue+saturation wheel with a
    /// brightness bar beside it, the color currently applied shown next to the one being picked, a hex
    /// field, R/G/B readouts, and the catalog's own theme colors. This used to be a row of big swatches
    /// on the page with everything else buried behind a "pick a custom color" dialog - the theme colors
    /// are just the quick picks within the same control now, so choosing one and then nudging it is a
    /// single, visible flow instead of two separate places. WPF-UI 4.3.0's own ColorPicker control is
    /// an internal, unimplemented stub, so this is hand-built rather than reusing a library control.
    /// </summary>
    private void BuildPicker()
    {
        var initial = PendingAccentColor;
        var current = ColorThemeCatalog.CurrentAccentColor();
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
        // A hairline down the middle, so the two halves still read as two even while they're the same
        // color - which is exactly the state the page opens in.
        var newSwatch = new Border
        {
            Height = 54,
            Background = new SolidColorBrush(initial),
            CornerRadius = new CornerRadius(0, 8, 8, 0),
            BorderThickness = new Thickness(1, 0, 0, 0),
        };
        newSwatch.SetResourceReference(Border.BorderBrushProperty, "ApplicationBackgroundBrush");
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

        // The "#" sits outside the box rather than inside it: WPF-UI's TextBox draws its own frame
        // from a ControlTemplate, so a borderless TextBox nested in a styled Border just renders a
        // stubby second box floating inside the first one.
        var hexBox = new System.Windows.Controls.TextBox
        {
            Text = ColorThemeCatalog.ToHex(initial),
            MaxLength = 6,
            FontFamily = new FontFamily("Consolas"),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var hexRow = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // Every TextBlock built in code here sets its own Foreground: an unstyled one defaults to
        // black, which is invisible against the dark theme. Only controls with a WPF-UI style of
        // their own (the hex TextBox below) pick up a themed foreground without being told.
        var hash = new System.Windows.Controls.TextBlock
        {
            Text = "#",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
        };
        hash.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Grid.SetColumn(hash, 0);
        Grid.SetColumn(hexBox, 1);
        hexRow.Children.Add(hash);
        hexRow.Children.Add(hexBox);

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
                Text = i switch { 0 => "R", 1 => "G", _ => "B" }, FontSize = 10, FontWeight = FontWeights.SemiBold,
            };
            caption.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

            rgbValues[i] = new System.Windows.Controls.TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 13 };
            rgbValues[i].SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

            stack.Children.Add(caption);
            stack.Children.Add(rgbValues[i]);
            cell.Child = stack;

            Grid.SetColumn(cell, i);
            rgbGrid.Children.Add(cell);
        }

        var rightColumn = new StackPanel { MinWidth = 160, Margin = new Thickness(18, 0, 0, 0) };
        rightColumn.Children.Add(SectionLabel("Current  /  New"));
        rightColumn.Children.Add(compare);
        rightColumn.Children.Add(SectionLabel("Hex"));
        rightColumn.Children.Add(hexRow);
        rightColumn.Children.Add(rgbGrid);

        var topRow = new Grid();
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(leftColumn, 0);
        Grid.SetColumn(rightColumn, 1);
        topRow.Children.Add(leftColumn);
        topRow.Children.Add(rightColumn);

        // Full width under both columns rather than stacked in the narrow right one, so twelve
        // swatches fit in a row or two instead of a four-wide block three rows tall.
        var presets = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };

        var root = new StackPanel();
        root.Children.Add(topRow);
        root.Children.Add(new Border { Height = 16 });
        root.Children.Add(SectionLabel("Theme colors"));
        root.Children.Add(presets);

        // ---- keeping every piece in sync ----
        var swatches = new List<(Border Swatch, string Name)>();

        void RefreshSelection()
        {
            foreach (var (swatch, name) in swatches)
            {
                var selected = name == _pendingAccentName;
                swatch.BorderThickness = new Thickness(selected ? 3 : 0);
                swatch.Child = selected
                    ? new SymbolIcon
                    {
                        Symbol = SymbolRegular.Checkmark24,
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    }
                    : null;
            }
        }

        /// presetName names a catalog theme when one was clicked, and is null when the color came from
        /// the wheel, the bar or the hex box - which is what makes the accent Custom. commit is false
        /// only for the very first paint, so building the page doesn't mark it as an unsaved change.
        void Redraw(bool skipHexBox, string? presetName, bool commit)
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

            if (!commit)
            {
                RefreshSelection();
                return;
            }

            if (presetName != null)
            {
                // A catalog theme carries hand-tuned light/dark variants that an arbitrary color has
                // to derive algorithmically - so picking one stays that named theme rather than
                // collapsing into "Custom" with the same base color.
                _pendingAccentName = presetName;
            }
            else
            {
                _pendingAccentName = ColorThemeCatalog.CustomThemeName;
                _pendingCustomColor = color;
            }

            RefreshSelection();
            UpdatePreview();
            UpdateApplyButtonState();
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
            Redraw(false, null, true);
        }

        wheelCanvas.MouseLeftButtonDown += (_, e) => { wheelCanvas.CaptureMouse(); PickFromWheel(e.GetPosition(wheelCanvas)); };
        wheelCanvas.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) PickFromWheel(e.GetPosition(wheelCanvas)); };
        wheelCanvas.MouseLeftButtonUp += (_, _) => wheelCanvas.ReleaseMouseCapture();

        void PickFromBar(Point p)
        {
            value = Math.Clamp(1 - p.Y / WheelSize, 0, 1);
            Redraw(false, null, true);
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
            Redraw(true, null, true); // leave the box alone while it's being typed into
            updating = false;
        };

        foreach (var theme in ColorThemeCatalog.All)
        {
            var swatch = new Border
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(theme.Base),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = theme.Name,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
                Effect = new DropShadowEffect { Color = theme.Base, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.5 },
            };

            var baseColor = theme.Base;
            var themeName = theme.Name;
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                (hue, sat, value) = RgbToHsv(baseColor.R, baseColor.G, baseColor.B);
                Redraw(false, themeName, true);
            };
            swatch.MouseEnter += (_, _) => AnimateSwatchScale(swatch, 1.1);
            swatch.MouseLeave += (_, _) => AnimateSwatchScale(swatch, 1.0);

            swatches.Add((swatch, theme.Name));
            presets.Children.Add(swatch);
        }

        Redraw(false, null, false);
        PickerHost.Child = root;
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
