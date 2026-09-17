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

        _loading = false;
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
