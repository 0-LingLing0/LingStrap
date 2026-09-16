using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lingstrap.Models;
using Lingstrap.Services;
using Wpf.Ui.Controls;

namespace Lingstrap.Views;

public partial class PresetsView : Page
{
    public PresetsView()
    {
        InitializeComponent();
        Refresh();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CardAction b || b.Tag is not string tag) return;
        if (!Enum.TryParse<Preset>(tag, out var preset)) return;

        PresetService.Apply(preset);
        Refresh();
    }

    private void Refresh()
    {
        var p = SettingsService.Current.ActivePreset;
        CurrentText.Text = $"Active: {PresetInfo.Name(p)}";
        CurrentDesc.Text = PresetInfo.Description(p);

        Highlight(TileQuality, p == Preset.BestQuality);
        Highlight(TileBalanced, p == Preset.Balanced);
        Highlight(TilePerformance, p == Preset.BestPerformance);
    }

    private static void Highlight(CardAction tile, bool active)
    {
        tile.BorderBrush = (Brush)tile.FindResource(active ? "SystemAccentBrush" : "ControlStrokeColorDefaultBrush");
        tile.BorderThickness = new Thickness(active ? 2 : 1);

        if (active)
        {
            var c = ColorThemeCatalog.CurrentAccentColor();
            tile.Background = new SolidColorBrush(Color.FromArgb(0x1F, c.R, c.G, c.B));
        }
        else tile.ClearValue(Control.BackgroundProperty);
    }
}
