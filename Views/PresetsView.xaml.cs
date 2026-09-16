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
        SelectTab("BuiltIn");
        Refresh();
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button b || b.Tag is not string tag) return;
        SelectTab(tag);
    }

    private void SelectTab(string tag)
    {
        var builtIn = tag == "BuiltIn";
        BuiltInPanel.Visibility = builtIn ? Visibility.Visible : Visibility.Collapsed;
        MinePanel.Visibility = builtIn ? Visibility.Collapsed : Visibility.Visible;
        HighlightTab(TabBuiltIn, builtIn);
        HighlightTab(TabMine, !builtIn);

        if (!builtIn) RefreshSavedPresets();
    }

    private static void HighlightTab(Wpf.Ui.Controls.Button tab, bool active)
    {
        if (active)
        {
            var c = ColorThemeCatalog.CurrentAccentColor();
            tab.Background = new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B));
        }
        else tab.ClearValue(Control.BackgroundProperty);
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CardAction b || b.Tag is not string tag) return;
        if (!Enum.TryParse<Preset>(tag, out var preset)) return;

        PresetService.Apply(preset);
        Refresh();
    }

    private async void SaveCurrent_Click(object sender, RoutedEventArgs e)
    {
        var name = await DialogHelper.ShowInputAsync(this, "Save current settings as a preset",
            placeholder: "Preset name", confirmText: "Save");
        if (name is null) return;

        PresetService.SaveCurrentAsPreset(name);
        Refresh();
        RefreshSavedPresets();
    }

    private void RefreshSavedPresets()
    {
        SavedPresetsPanel.Children.Clear();
        var presets = SettingsService.Current.SavedPresets;
        var activeId = SettingsService.Current.ActiveSavedPresetId;

        MineEmptyText.Visibility = presets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var preset in presets)
        {
            var row = new SavedPresetRow(preset, preset.Id == activeId);

            row.ApplyRequested += () =>
            {
                PresetService.ApplySaved(preset);
                Refresh();
                RefreshSavedPresets();
            };

            row.RenameRequested += async () =>
            {
                var newName = await DialogHelper.ShowInputAsync(this, "Rename preset",
                    defaultValue: preset.Name, confirmText: "Rename");
                if (newName is null) return;

                PresetService.RenameSaved(preset.Id, newName);
                RefreshSavedPresets();
            };

            row.DeleteRequested += async () =>
            {
                var confirmed = await DialogHelper.ShowConfirmAsync(this,
                    $"Delete the saved preset \"{preset.Name}\"? This can't be undone.",
                    "Delete preset", confirmText: "Delete");
                if (!confirmed) return;

                PresetService.DeleteSaved(preset.Id);
                Refresh();
                RefreshSavedPresets();
            };

            SavedPresetsPanel.Children.Add(row);
        }
    }

    private void Refresh()
    {
        var p = SettingsService.Current.ActivePreset;
        var savedId = SettingsService.Current.ActiveSavedPresetId;

        if (savedId != null)
        {
            var saved = SettingsService.Current.SavedPresets.Find(x => x.Id == savedId);
            CurrentText.Text = $"Active: {saved?.Name ?? "(deleted preset)"}";
            CurrentDesc.Text = "One of your own saved presets - see the My Presets tab.";
        }
        else
        {
            CurrentText.Text = $"Active: {PresetInfo.Name(p)}";
            CurrentDesc.Text = PresetInfo.Description(p);
        }

        Highlight(TileQuality, savedId == null && p == Preset.BestQuality);
        Highlight(TileBalanced, savedId == null && p == Preset.Balanced);
        Highlight(TilePerformance, savedId == null && p == Preset.BestPerformance);
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
