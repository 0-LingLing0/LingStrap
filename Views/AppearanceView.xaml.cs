using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Lingstrap.Models;
using Lingstrap.Services;
using Wpf.Ui.Controls;

namespace Lingstrap.Views;

public partial class AppearanceView : Page
{
    private bool _loading = true;

    public AppearanceView()
    {
        InitializeComponent();
        Populate();

        LightThemeToggle.IsChecked = SettingsService.Current.LightTheme;
        SelectClosestFontScale(SettingsService.Current.FontScalePercent);
        _loading = false;
    }

    private void LightThemeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Current.LightTheme = LightThemeToggle.IsChecked == true;
        SettingsService.Save();
        RestartApp();
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
        var current = SettingsService.Current.AccentTheme;

        foreach (var theme in ColorThemeCatalog.All)
        {
            var isSelected = theme.Name == current;
            var name = theme.Name;
            SwatchPanel.Children.Add(BuildSwatch(theme.Base, theme.Name, isSelected, () => Select(name)));
        }

        var isCustomSelected = current == ColorThemeCatalog.CustomThemeName;
        var customColor = isCustomSelected ? ColorThemeCatalog.ParseCustomColor(SettingsService.Current.CustomAccentColor) : (Color?)null;
        SwatchPanel.Children.Add(BuildCustomSwatch(customColor, isCustomSelected));
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
    /// (with the usual checkmark) once a custom color is the active theme.</summary>
    private Border BuildCustomSwatch(Color? activeCustomColor, bool isSelected)
    {
        if (activeCustomColor is { } color)
            return BuildSwatch(color, "Custom", isSelected, OpenCustomColorPicker);

        var swatch = new Border
        {
            Width = 60,
            Height = 60,
            Margin = new Thickness(0, 0, 14, 14),
            CornerRadius = new CornerRadius(30),
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(2),
            Cursor = Cursors.Hand,
            ToolTip = "Pick a custom color",
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            Child = new SymbolIcon
            {
                Symbol = SymbolRegular.Add24,
                Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
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
        if (name == SettingsService.Current.AccentTheme) return;

        SettingsService.Current.AccentTheme = name;
        SettingsService.Save();

        Populate(); // instant feedback on the swatches themselves before the restart below

        RestartApp();
    }

    private async void OpenCustomColorPicker()
    {
        var initial = SettingsService.Current.AccentTheme == ColorThemeCatalog.CustomThemeName
            ? ColorThemeCatalog.ParseCustomColor(SettingsService.Current.CustomAccentColor)
            : ColorThemeCatalog.Find(SettingsService.Current.AccentTheme).Base;

        var picked = initial;
        var content = BuildColorPickerContent(initial, c => picked = c);

        var confirmed = await DialogHelper.ShowConfirmAsync(this, content, "Pick a custom color", confirmText: "Apply");
        if (!confirmed) return;

        SettingsService.Current.AccentTheme = ColorThemeCatalog.CustomThemeName;
        SettingsService.Current.CustomAccentColor = ColorThemeCatalog.ToHex(picked);
        SettingsService.Save();

        Populate();
        RestartApp();
    }

    /// <summary>RGB sliders + a hex box, kept in sync with each other, plus a live preview swatch.
    /// WPF-UI 4.3.0's own ColorPicker control is an internal, unimplemented stub, so this is hand-built
    /// rather than reusing a library control.</summary>
    private static FrameworkElement BuildColorPickerContent(Color initial, Action<Color> onChanged)
    {
        var panel = new StackPanel { Width = 280 };

        var preview = new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(32),
            Background = new SolidColorBrush(initial),
            Margin = new Thickness(0, 0, 0, 16),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        panel.Children.Add(preview);

        var hexBox = new System.Windows.Controls.TextBox
        {
            Text = ColorThemeCatalog.ToHex(initial),
            MaxLength = 6,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(40, 0, 40, 16),
        };
        panel.Children.Add(hexBox);

        var updating = false;
        var rSlider = AddChannelRow(panel, "R", initial.R);
        var gSlider = AddChannelRow(panel, "G", initial.G);
        var bSlider = AddChannelRow(panel, "B", initial.B);

        void UpdateFromSliders()
        {
            if (updating) return;
            var color = Color.FromRgb((byte)rSlider.Value, (byte)gSlider.Value, (byte)bSlider.Value);
            updating = true;
            preview.Background = new SolidColorBrush(color);
            hexBox.Text = ColorThemeCatalog.ToHex(color);
            updating = false;
            onChanged(color);
        }

        void UpdateFromHex()
        {
            if (updating) return;
            var text = hexBox.Text.Trim().TrimStart('#');
            if (text.Length != 6 || !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                return;

            var color = Color.FromRgb((byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));
            updating = true;
            rSlider.Value = color.R;
            gSlider.Value = color.G;
            bSlider.Value = color.B;
            preview.Background = new SolidColorBrush(color);
            updating = false;
            onChanged(color);
        }

        rSlider.ValueChanged += (_, _) => UpdateFromSliders();
        gSlider.ValueChanged += (_, _) => UpdateFromSliders();
        bSlider.ValueChanged += (_, _) => UpdateFromSliders();
        hexBox.TextChanged += (_, _) => UpdateFromHex();

        return panel;
    }

    private static Slider AddChannelRow(StackPanel parent, string label, byte value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };

        row.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = label,
            Width = 16,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 255,
            Value = value,
            Width = 200,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
        };
        row.Children.Add(slider);

        var valueLabel = new System.Windows.Controls.TextBlock
        {
            Text = value.ToString(),
            Width = 28,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(valueLabel);
        slider.ValueChanged += (_, _) => valueLabel.Text = ((byte)slider.Value).ToString();

        parent.Children.Add(row);
        return slider;
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
    /// </summary>
    private void RestartApp()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
                Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not relaunch Lingstrap to apply the new theme: {ex.Message}");
        }

        Application.Current.Shutdown();
    }
}
