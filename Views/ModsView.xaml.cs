using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Lingstrap.Models;
using Lingstrap.Services;

namespace Lingstrap.Views;

public partial class ModsView : Page
{
    private bool _loading = true;

    public ModsView()
    {
        InitializeComponent();

        var s = SettingsService.Current;
        ChkModsEnabled.IsChecked = s.ModsEnabled;

        // Nothing to persist per slot: the picked image lives in Lingstrap's Cursors folder and the
        // label is always the fixed Roblox target filename, so there's no per-slot setting to save.
        var mouseSlot = new CursorSlotControl(CursorSlot.Mouse, "Mouse cursor");
        var shiftlockSlot = new CursorSlotControl(CursorSlot.Shiftlock, "Shiftlock cursor");

        mouseSlot.Margin = new Thickness(0, 0, 24, 0);
        CursorSlotsPanel.Children.Add(mouseSlot);
        CursorSlotsPanel.Children.Add(shiftlockSlot);

        ModsFolderRow.Description = Paths.Mods;
        RefreshFont();

        _loading = false;
    }

    private void RefreshFont()
    {
        var name = SettingsService.Current.CustomFontName;
        var hasFont = FontService.HasCustomFont();

        FontRow.Description = hasFont
            ? $"{name ?? "Custom font"} - replaces the font everywhere in Roblox's interface"
            : "Roblox default. Pick a .ttf or .otf to use your own everywhere in Roblox's interface.";
        ClearFontButton.IsEnabled = hasFont;
    }

    private async void ChooseFont_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a font",
            Filter = "Fonts (*.ttf;*.otf)|*.ttf;*.otf",
        };
        if (dialog.ShowDialog() != true) return;

        if (!FontService.IsSupported(dialog.FileName))
        {
            await DialogHelper.ShowErrorAsync(this, "That file isn't a .ttf or .otf font.");
            return;
        }

        try
        {
            FontService.SetFont(dialog.FileName);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not set the custom font from {dialog.FileName}", ex);
            await DialogHelper.ShowErrorAsync(this, $"Could not use that font:\n{ex.Message}");
            return;
        }

        RefreshFont();
    }

    private void ClearFont_Click(object sender, RoutedEventArgs e)
    {
        FontService.Clear();
        RefreshFont();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Current.ModsEnabled = ChkModsEnabled.IsChecked == true;
        SettingsService.Save();
    }

    private void OpenMods_Click(object sender, RoutedEventArgs e)
    {
        Paths.EnsureCreated();
        Process.Start(new ProcessStartInfo { FileName = Paths.Mods, UseShellExecute = true });
    }
}
