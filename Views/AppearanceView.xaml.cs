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
    public AppearanceView()
    {
        InitializeComponent();
        Populate();
    }

    private void Populate()
    {
        SwatchPanel.Children.Clear();
        var current = SettingsService.Current.AccentTheme;

        foreach (var theme in ColorThemeCatalog.All)
        {
            var isSelected = theme.Name == current;

            var swatch = new Border
            {
                Width = 60,
                Height = 60,
                Margin = new Thickness(0, 0, 14, 14),
                CornerRadius = new CornerRadius(30),
                Background = new SolidColorBrush(theme.Base),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(isSelected ? 3 : 0),
                Cursor = Cursors.Hand,
                ToolTip = theme.Name,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
                Effect = new DropShadowEffect
                {
                    Color = theme.Base,
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

            var name = theme.Name;
            swatch.MouseLeftButtonUp += (_, _) => Select(name);
            swatch.MouseEnter += (_, _) => AnimateSwatchScale(swatch, 1.08);
            swatch.MouseLeave += (_, _) => AnimateSwatchScale(swatch, 1.0);

            SwatchPanel.Children.Add(swatch);
        }
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
