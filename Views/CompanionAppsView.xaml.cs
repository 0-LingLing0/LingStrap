using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Lingstrap.Models;
using Lingstrap.Services;

namespace Lingstrap.Views;

public partial class CompanionAppsView : Page
{
    private bool _loading = true;

    public CompanionAppsView()
    {
        InitializeComponent();

        ManageToggle.IsChecked = SettingsService.Current.ManageCompanionApps;
        Rebuild();

        _loading = false;
    }

    private void Rebuild()
    {
        RowsPanel.Children.Clear();
        var apps = SettingsService.Current.CompanionApps;

        EmptyState.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DropHint.Visibility = apps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        for (var i = 0; i < apps.Count; i++)
        {
            var index = i;
            var row = new CompanionAppRow(apps[i]);
            row.SetMoveButtonsEnabled(index > 0, index < apps.Count - 1);

            row.Changed += Save;
            row.DeleteRequested += () =>
            {
                apps.RemoveAt(index);
                Save();
                Rebuild();
            };
            row.MoveUpRequested += () => MoveApp(index, index - 1);
            row.MoveDownRequested += () => MoveApp(index, index + 1);

            RowsPanel.Children.Add(row);
        }
    }

    private void MoveApp(int from, int to)
    {
        var apps = SettingsService.Current.CompanionApps;
        if (to < 0 || to >= apps.Count) return;

        (apps[from], apps[to]) = (apps[to], apps[from]);
        Save();
        Rebuild();
    }

    private void Save() => SettingsService.Save();

    private void ManageToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Current.ManageCompanionApps = ManageToggle.IsChecked == true;
        Save();
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a companion app",
            Filter = "Programs and shortcuts (*.exe;*.lnk)|*.exe;*.lnk",
        };

        if (dialog.ShowDialog() == true)
            AddApp(dialog.FileName);
    }

    private void Page_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Page_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".exe" or ".lnk")
                AddApp(file);
        }
    }

    /// <summary>Resolves a .lnk to its real target exe, keeping the shortcut's own name for display.</summary>
    private async void AddApp(string path)
    {
        var displayName = Path.GetFileNameWithoutExtension(path);
        var exePath = path;

        if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var target = ShortcutResolver.ResolveTarget(path);
            if (target is null || !File.Exists(target))
            {
                await DialogHelper.ShowErrorAsync(this, $"Could not resolve the shortcut '{Path.GetFileName(path)}' to a real program.");
                return;
            }
            exePath = target;
        }
        else if (!File.Exists(exePath))
        {
            return;
        }

        var entry = new CompanionApp { Name = displayName, ExePath = exePath, Enabled = true };
        SettingsService.Current.CompanionApps.Add(entry);
        Save();
        Rebuild();
    }
}
