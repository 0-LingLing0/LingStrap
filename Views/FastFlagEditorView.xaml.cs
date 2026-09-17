using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Lingstrap.Models;
using Lingstrap.Services;
using Microsoft.Win32;

namespace Lingstrap.Views;

/// <summary>
/// The raw FastFlag editor (Froststrap-style). One row per flag, fully editable, with rows for
/// unrecognised names shown muted. Reads and writes the same flag store as FastFlagsView.
/// </summary>
public partial class FastFlagEditorView : Page
{
    private readonly ObservableCollection<FastFlagEntry> _flags = new();
    private ICollectionView _view = null!;

    public FastFlagEditorView()
    {
        InitializeComponent();

        foreach (var entry in SettingsService.Current.CustomFlags)
            _flags.Add(entry);

        Grid.ItemsSource = _flags;
        _view = CollectionViewSource.GetDefaultView(_flags);
        _flags.CollectionChanged += (_, _) => RefreshEmptyState();

        RefreshClientAppSettingsButton();
        RefreshEmptyState();
    }

    private void RefreshEmptyState() =>
        EmptyState.Visibility = _flags.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Disables "Open ClientAppSettings.json" with an explanatory tooltip instead of letting the user
    /// click it and get a popup - the button can only ever do one thing, so if that thing isn't
    /// currently possible, it shouldn't be clickable at all.
    /// </summary>
    private void RefreshClientAppSettingsButton()
    {
        var versionFolder = RobloxLocator.FindVersionFolder();
        if (versionFolder == null)
        {
            OpenClientAppSettingsButton.IsEnabled = false;
            OpenClientAppSettingsButton.ToolTip = "Roblox isn't installed yet - launch it at least once first.";
            return;
        }

        var file = Path.Combine(versionFolder, "ClientSettings", "ClientAppSettings.json");
        if (!File.Exists(file))
        {
            OpenClientAppSettingsButton.IsEnabled = false;
            OpenClientAppSettingsButton.ToolTip = "ClientAppSettings.json doesn't exist yet - it's written the next time you launch through Lingstrap.";
            return;
        }

        OpenClientAppSettingsButton.IsEnabled = true;
        OpenClientAppSettingsButton.ToolTip = null;
    }

    private void Persist()
    {
        SettingsService.Current.CustomFlags = _flags.ToList();
        PresetService.MarkCustom();
        SettingsService.Save();
    }

    private void Grid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is FastFlagEntry entry) ApplyRowStyle(e.Row, entry);
    }

    private static void ApplyRowStyle(DataGridRow row, FastFlagEntry entry)
    {
        var known = string.IsNullOrWhiteSpace(entry.Name) || FastFlagCatalog.IsKnown(entry.Name);
        row.Opacity = known ? 1.0 : 0.55;

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            row.ToolTip = null;
            return;
        }

        var definition = FastFlagCatalog.Find(entry.Name);
        row.ToolTip = definition != null
            ? definition.Description
            : "Not one of Lingstrap's 18 known-working flags. Since September 2025 Roblox only honors " +
              "flags on its own allowlist and silently ignores everything else - this one might still be " +
              "on that list, but there's no way for Lingstrap to check, so treat it as unverified.";
    }

    private void Grid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        // Cancelling an edit (Escape) still raises this - without the check, backing out of a cell
        // would save and drop the active preset to Custom despite nothing having changed.
        if (e.EditAction != DataGridEditAction.Commit) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            Persist();
            if (e.Row.Item is FastFlagEntry entry &&
                Grid.ItemContainerGenerator.ContainerFromItem(entry) is DataGridRow row)
            {
                ApplyRowStyle(row, entry);
            }
        }));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        _view.Filter = query.Length == 0
            ? null
            : o => o is FastFlagEntry f && FuzzyMatch.IsMatch(f.Name, query);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        _flags.Add(new FastFlagEntry { Name = "", Value = "true", Enabled = true });
        Persist();
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Grid.SelectedItems.Cast<FastFlagEntry>().ToList())
            _flags.Remove(item);
        Persist();
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog() != true) return;

        await ImportFromFile(dialog.FileName);
    }

    private async Task ImportFromFile(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            await DialogHelper.ShowErrorAsync(this, $"Could not read that file:\n{ex.Message}");
            return;
        }

        var imported = FastFlagFile.TryParse(json, out var error);
        if (imported == null)
        {
            await DialogHelper.ShowErrorAsync(this, $"Could not import that file:\n{error}");
            return;
        }

        var added = new List<FastFlagEntry>();
        var changed = new List<(FastFlagEntry Existing, FastFlagEntry Incoming)>();
        foreach (var entry in imported)
        {
            var existing = _flags.FirstOrDefault(f => f.Name == entry.Name);
            if (existing == null) added.Add(entry);
            else if (existing.Value != entry.Value || existing.Enabled != entry.Enabled) changed.Add((existing, entry));
        }

        if (added.Count == 0 && changed.Count == 0)
        {
            await DialogHelper.ShowErrorAsync(this,
                "Every flag in that file already matches what you have - nothing to import.", "Nothing changed");
            return;
        }

        var confirmed = await DialogHelper.ShowConfirmAsync(this, BuildImportDiff(added, changed),
            $"Import {added.Count + changed.Count} flag change(s)?", confirmText: "Import");
        if (!confirmed) return;

        foreach (var entry in imported)
        {
            var existing = _flags.FirstOrDefault(f => f.Name == entry.Name);
            if (existing != null)
            {
                existing.Value = entry.Value;
                existing.Enabled = entry.Enabled;
            }
            else
            {
                _flags.Add(entry);
            }
        }

        Persist();
    }

    /// <summary>Shows exactly what an import will do before it does it - which flags are brand new
    /// versus which ones overwrite a value you already set, since merging silently was easy to
    /// misjudge (an import can quietly clobber a flag you tuned yourself).</summary>
    private static FrameworkElement BuildImportDiff(List<FastFlagEntry> added, List<(FastFlagEntry Existing, FastFlagEntry Incoming)> changed)
    {
        var panel = new StackPanel();

        foreach (var entry in added)
            panel.Children.Add(DiffRow("+", Color.FromRgb(0x4C, 0xC9, 0x5A), $"{entry.Name} = {entry.Value}"));

        foreach (var (existing, incoming) in changed)
            panel.Children.Add(DiffRow("~", Color.FromRgb(0xE8, 0xB1, 0x3D), $"{existing.Name}: {existing.Value} → {incoming.Value}"));

        return new ScrollViewer
        {
            MaxHeight = 320,
            MaxWidth = 420,
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private static UIElement DiffRow(string prefix, Color color, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(new TextBlock { Text = prefix, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(color), Width = 20 });
        row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, FontFamily = new FontFamily("Consolas") });
        return row;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "JSON files (*.json)|*.json", FileName = "LingstrapFlags.json" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, FastFlagFile.Export(_flags));
        }
        catch (Exception ex)
        {
            await DialogHelper.ShowErrorAsync(this, $"Could not export to that file:\n{ex.Message}");
        }
    }

    private void OpenClientAppSettings_Click(object sender, RoutedEventArgs e)
    {
        var versionFolder = RobloxLocator.FindVersionFolder();
        if (versionFolder == null) return; // button is disabled in this state - see RefreshClientAppSettingsButton

        var file = Path.Combine(versionFolder, "ClientSettings", "ClientAppSettings.json");
        if (!File.Exists(file)) return;

        Process.Start(new ProcessStartInfo { FileName = file, UseShellExecute = true });
    }

    private void BackToFastFlags_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window)
            window.RootNavigation.Navigate(typeof(FastFlagsView));
    }

    private async void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        if (_flags.Count == 0) return;

        if (!await DialogHelper.ShowConfirmAsync(this, "This removes every custom FastFlag. This can't be undone.", "Reset all FastFlags?"))
            return;

        _flags.Clear();
        Persist();
    }
}
