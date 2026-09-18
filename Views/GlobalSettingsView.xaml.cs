using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Lingstrap.Models;
using Lingstrap.Services;

namespace Lingstrap.Views;

public partial class GlobalSettingsView : Page
{
    private static readonly Regex PrefixPattern = new(@"^[A-Z]+(?=[A-Z][a-z]|$)|^[A-Z][a-z0-9]*", RegexOptions.Compiled);

    private readonly List<(string Name, GbsFieldRow Row, Border? GroupHeader)> _searchableRows = new();

    private bool _loadingLock = true;

    public GlobalSettingsView()
    {
        InitializeComponent();

        // On if either says so: the setting is what Lingstrap re-applies each launch, but the file can
        // also have been locked by hand or by another tool, and a toggle showing "off" over a file
        // that's actually read-only would be lying about why edits in Roblox aren't sticking.
        ChkLock.IsChecked = SettingsService.Current.LockGlobalSettings || GlobalBasicSettingsService.IsLocked();
        _loadingLock = false;

        Load();
    }

    private async void Lock_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingLock) return;

        var locked = ChkLock.IsChecked == true;
        SettingsService.Current.LockGlobalSettings = locked;
        SettingsService.Save();

        if (!GlobalBasicSettingsService.ApplyLock(locked))
        {
            await DialogHelper.ShowErrorAsync(this,
                $"Could not {(locked ? "lock" : "unlock")} the settings file - see the log for details.");
        }
    }

    private void Load()
    {
        RunningBanner.Visibility = GlobalBasicSettingsService.IsRobloxRunning() ? Visibility.Visible : Visibility.Collapsed;

        if (!GlobalBasicSettingsService.TryLoad(out var fields, out var error))
        {
            ErrorCard.Visibility = Visibility.Visible;
            ErrorText.Text = error;
            ContentPanel.Visibility = Visibility.Collapsed;
            Log.Warn($"Could not load GlobalBasicSettings_13.xml: {error}");
            return;
        }

        ErrorCard.Visibility = Visibility.Collapsed;
        ContentPanel.Visibility = Visibility.Visible;

        RestoreOriginalButton.IsEnabled = GlobalBasicSettingsService.BackupExists();
        RestoreOriginalButton.ToolTip = RestoreOriginalButton.IsEnabled
            ? null
            : "No backup exists yet - nothing has been changed from the original.";

        GlobalBasicSettingsService.ApplyPendingOverlay(fields);

        BuildShortlist(fields);
        BuildAnnoyances(fields);

        var shown = new HashSet<string>(GbsFieldCatalog.Annoyances.Select(a => a.Name));
        shown.UnionWith(GbsFieldCatalog.Shortlist);
        BuildGroups(fields, shown);
    }

    private void BuildShortlist(List<GbsField> fields)
    {
        ShortlistPanel.Children.Clear();
        var byName = fields.ToDictionary(f => f.Name);
        var annoyanceNames = new HashSet<string>(GbsFieldCatalog.Annoyances.Select(a => a.Name));

        foreach (var name in GbsFieldCatalog.Shortlist)
        {
            if (annoyanceNames.Contains(name)) continue; // already shown in Annoyances - no duplicates
            if (!byName.TryGetValue(name, out var field)) continue; // not present in this file - skip silently

            GbsFieldCatalog.FriendlyNames.TryGetValue(name, out var friendly);
            ShortlistPanel.Children.Add(new GbsFieldRow(field, friendly));
        }
    }

    private void BuildAnnoyances(List<GbsField> fields)
    {
        AnnoyancesPanel.Children.Clear();
        var byName = fields.ToDictionary(f => f.Name);

        foreach (var (name, label, desc) in GbsFieldCatalog.Annoyances)
        {
            if (!byName.TryGetValue(name, out var field)) continue; // not present in this file - skip silently
            AnnoyancesPanel.Children.Add(new GbsFieldRow(field, new GbsFieldInfo(label, desc)));
        }

        var turnOffButton = new Wpf.Ui.Controls.Button
        {
            Content = "Turn off all of these",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
        };
        turnOffButton.Click += TurnOffAnnoyances_Click;
        AnnoyancesPanel.Children.Add(turnOffButton);
    }

    private void TurnOffAnnoyances_Click(object sender, RoutedEventArgs e)
    {
        if (!GlobalBasicSettingsService.TryLoad(out var fields, out _)) return;
        var byName = fields.ToDictionary(f => f.Name);

        foreach (var (name, _, _) in GbsFieldCatalog.Annoyances)
        {
            if (!byName.TryGetValue(name, out var field) || field.Type != GbsFieldType.Bool) continue;

            GlobalBasicSettingsService.WriteScalar(name, field.Tag, "false");

            if (PresetCatalog.ManagedGbsFields.Contains(name))
                PresetService.MarkCustom();
        }

        Load();
    }

    private void BuildGroups(List<GbsField> fields, HashSet<string> exclude)
    {
        GroupsPanel.Children.Clear();
        _searchableRows.Clear();

        var ordered = fields.Where(f => !exclude.Contains(f.Name)).OrderBy(f => f.Name).ToList();
        var prefixCounts = ordered
            .GroupBy(f => Prefix(f.Name))
            .ToDictionary(g => g.Key, g => g.Count());

        var groups = ordered
            .GroupBy(f => prefixCounts[Prefix(f.Name)] >= 2 ? Prefix(f.Name) : "Other")
            .OrderBy(g => g.Key == "Other" ? 1 : 0)
            .ThenBy(g => g.Key);

        foreach (var group in groups)
        {
            var header = new Border { Margin = new Thickness(0, 12, 0, 8) };
            var headerText = new TextBlock
            {
                Text = group.Key,
                FontWeight = FontWeights.SemiBold,
                FontSize = 15,
                Opacity = 0.85,
            };
            headerText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            header.Child = headerText;
            GroupsPanel.Children.Add(header);

            foreach (var field in group)
            {
                GbsFieldCatalog.FriendlyNames.TryGetValue(field.Name, out var friendly);
                var row = new GbsFieldRow(field, friendly);
                GroupsPanel.Children.Add(row);
                _searchableRows.Add((field.Name, row, header));
            }
        }
    }

    private static string Prefix(string name)
    {
        var m = PrefixPattern.Match(name);
        return m.Success ? m.Value : name;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        if (query.Length > 0) AdvancedExpander.IsExpanded = true;

        var visibleHeaders = new HashSet<Border>();

        foreach (var (name, row, header) in _searchableRows)
        {
            var match = query.Length == 0 || name.Contains(query, System.StringComparison.OrdinalIgnoreCase);
            row.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            if (match && header != null) visibleHeaders.Add(header);
        }

        foreach (var header in _searchableRows.Select(r => r.GroupHeader).Distinct())
        {
            if (header != null)
                header.Visibility = visibleHeaders.Contains(header) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void RestoreOriginal_Click(object sender, RoutedEventArgs e)
    {
        // Button is disabled with an explanatory tooltip when there's no backup - see Load().
        if (!GlobalBasicSettingsService.BackupExists()) return;

        var confirmed = await DialogHelper.ShowConfirmAsync(this,
            "This puts back the GlobalBasicSettings_13.xml from before Lingstrap's first change, discarding everything since.",
            "Restore original?");
        if (!confirmed) return;

        GlobalBasicSettingsService.RestoreOriginal();
        Load();
    }
}
