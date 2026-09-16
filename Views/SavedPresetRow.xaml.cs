using System;
using System.Windows;
using System.Windows.Controls;
using Lingstrap.Models;

namespace Lingstrap.Views;

public partial class SavedPresetRow : UserControl
{
    private readonly SavedPreset _preset;

    public event Action? ApplyRequested;
    public event Action? RenameRequested;
    public event Action? DeleteRequested;

    public SavedPresetRow(SavedPreset preset, bool active)
    {
        InitializeComponent();
        _preset = preset;

        NameText.Text = preset.Name;
        SubText.Text = $"{preset.Flags.Count} flag{(preset.Flags.Count == 1 ? "" : "s")} · {preset.Priority} priority" +
                       (active ? " · Active" : "");

        if (active)
        {
            ApplyButton.IsEnabled = false;
            ApplyButton.Content = "Active";
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => ApplyRequested?.Invoke();
    private void Rename_Click(object sender, RoutedEventArgs e) => RenameRequested?.Invoke();
    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteRequested?.Invoke();
}
