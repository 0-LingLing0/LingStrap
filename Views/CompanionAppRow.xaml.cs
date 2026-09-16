using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Lingstrap.Models;
using Lingstrap.Services;

namespace Lingstrap.Views;

/// <summary>One companion app row: compact header plus a collapsible edit panel.</summary>
public partial class CompanionAppRow : UserControl
{
    private readonly CompanionApp _entry;
    private bool _loading = true;

    public event Action? Changed;
    public event Action? DeleteRequested;
    public event Action? MoveUpRequested;
    public event Action? MoveDownRequested;

    public CompanionAppRow(CompanionApp entry)
    {
        InitializeComponent();
        _entry = entry;

        NameText.Text = string.IsNullOrWhiteSpace(entry.Name) ? Path.GetFileNameWithoutExtension(entry.ExePath) : entry.Name;
        PathText.Text = entry.ExePath;
        EnabledToggle.IsChecked = entry.Enabled;

        NameBox.Text = entry.Name;
        ArgumentsBox.Text = entry.Arguments;
        DelayBox.Value = entry.StartupDelaySeconds;
        AdminToggle.IsChecked = entry.RunAsAdmin;
        DontAutoCloseToggle.IsChecked = entry.DontAutoClose;

        LoadIcon(entry.ExePath);

        _loading = false;
    }

    public void SetMoveButtonsEnabled(bool canMoveUp, bool canMoveDown)
    {
        UpButton.IsEnabled = canMoveUp;
        DownButton.IsEnabled = canMoveDown;
    }

    private void LoadIcon(string exePath)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon is null) return;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            AppIcon.Source = source;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not extract icon for {exePath}: {ex.Message}");
        }
    }

    private void EnabledToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _entry.Enabled = EnabledToggle.IsChecked == true;
        Changed?.Invoke();
    }

    private void Field_Changed(object sender, RoutedEventArgs e) => CommitFields();
    private void Field_Changed(object sender, TextChangedEventArgs e) => CommitFields();

    private void CommitFields()
    {
        if (_loading) return;

        _entry.Name = NameBox.Text.Trim();
        _entry.Arguments = ArgumentsBox.Text;
        _entry.StartupDelaySeconds = (int)(DelayBox.Value ?? 0);
        _entry.RunAsAdmin = AdminToggle.IsChecked == true;
        _entry.DontAutoClose = DontAutoCloseToggle.IsChecked == true;

        NameText.Text = string.IsNullOrWhiteSpace(_entry.Name) ? Path.GetFileNameWithoutExtension(_entry.ExePath) : _entry.Name;
        Changed?.Invoke();
    }

    private void Edit_Click(object sender, RoutedEventArgs e) =>
        EditPanel.Visibility = EditPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void Done_Click(object sender, RoutedEventArgs e) => EditPanel.Visibility = Visibility.Collapsed;

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteRequested?.Invoke();
    private void Up_Click(object sender, RoutedEventArgs e) => MoveUpRequested?.Invoke();
    private void Down_Click(object sender, RoutedEventArgs e) => MoveDownRequested?.Invoke();
}
