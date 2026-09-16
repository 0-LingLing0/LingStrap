using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Lingstrap.Models;
using Lingstrap.Services;
using Wpf.Ui.Controls;

namespace Lingstrap.Views;

/// <summary>One row in the generic GlobalBasicSettings editor - control depends on the field's XML type.</summary>
public partial class GbsFieldRow : UserControl
{
    private readonly GbsField _field;
    private bool _loading = true;

    public GbsFieldRow(GbsField field, GbsFieldInfo? friendly)
    {
        InitializeComponent();
        _field = field;

        LabelText.Text = friendly?.Label ?? field.Name;
        DescText.Text = friendly != null ? $"{friendly.Description}  ({field.Name})" : $"{field.Tag} property";

        ResetButton.IsEnabled = GlobalBasicSettingsService.BackupExists();

        ValueHost.Content = BuildControl();
        _loading = false;
    }

    private FrameworkElement BuildControl()
    {
        switch (_field.Type)
        {
            case GbsFieldType.Bool:
            {
                var toggle = new ToggleSwitch { IsChecked = ParseBool(_field.RawValue) };
                toggle.Checked += (_, _) => Commit(toggle.IsChecked == true ? "true" : "false");
                toggle.Unchecked += (_, _) => Commit(toggle.IsChecked == true ? "true" : "false");
                return toggle;
            }
            case GbsFieldType.Int:
            case GbsFieldType.Token:
            {
                if (GbsFieldCatalog.BoundedRanges.TryGetValue(_field.Name, out var intRange))
                    return BuildBoundedControl(intRange, ParseDouble(_field.RawValue));

                var box = new NumberBox
                {
                    Value = ParseDouble(_field.RawValue),
                    Width = 130,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                    ClearButtonEnabled = false,
                };
                box.LostKeyboardFocus += (_, _) => Commit(((long)(box.Value ?? 0)).ToString(CultureInfo.InvariantCulture));
                box.PreviewKeyDown += EnterCommits;
                return box;
            }
            case GbsFieldType.Float:
            {
                if (GbsFieldCatalog.BoundedRanges.TryGetValue(_field.Name, out var floatRange))
                    return BuildBoundedControl(floatRange, ParseDouble(_field.RawValue));

                var box = new NumberBox
                {
                    Value = ParseDouble(_field.RawValue),
                    Width = 130,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                    ClearButtonEnabled = false,
                    SmallChange = 0.1,
                };
                box.LostKeyboardFocus += (_, _) => Commit((box.Value ?? 0).ToString(CultureInfo.InvariantCulture));
                box.PreviewKeyDown += EnterCommits;
                return box;
            }
            case GbsFieldType.String:
            {
                var box = new Wpf.Ui.Controls.TextBox { Text = _field.RawValue ?? "", Width = 220 };
                box.LostKeyboardFocus += (_, _) => Commit(box.Text);
                box.PreviewKeyDown += EnterCommits;
                return box;
            }
            case GbsFieldType.Vector2:
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal };
                var x = new NumberBox { Value = _field.VectorX ?? 0, Width = 90, PlaceholderText = "X", SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden, ClearButtonEnabled = false };
                var y = new NumberBox { Value = _field.VectorY ?? 0, Width = 90, Margin = new Thickness(8, 0, 0, 0), PlaceholderText = "Y", SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden, ClearButtonEnabled = false };
                x.LostKeyboardFocus += (_, _) => CommitVector(x.Value ?? 0, y.Value ?? 0);
                y.LostKeyboardFocus += (_, _) => CommitVector(x.Value ?? 0, y.Value ?? 0);
                x.PreviewKeyDown += EnterCommits;
                y.PreviewKeyDown += EnterCommits;
                panel.Children.Add(x);
                panel.Children.Add(y);
                return panel;
            }
            default:
                return new System.Windows.Controls.TextBlock
                {
                    Text = _field.RawXml ?? "(unreadable)",
                    Opacity = 0.6,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 320,
                    ToolTip = _field.RawXml,
                };
        }
    }

    /// <summary>
    /// A slider paired with a small editable numeric field, for fields with a known, documented
    /// range (GbsFieldCatalog.BoundedRanges). Percentage fields (0.0-1.0) show/accept 0-100 in the
    /// box while the slider and the actual written value stay in raw 0.0-1.0 units.
    /// </summary>
    private FrameworkElement BuildBoundedControl(GbsFieldRange range, double initialValue)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var slider = new Slider
        {
            Minimum = range.Min,
            Maximum = range.Max,
            Width = 150,
            VerticalAlignment = VerticalAlignment.Center,
            IsSnapToTickEnabled = range.IntegerSnap,
            TickFrequency = range.IntegerSnap ? 1 : 0.01,
            SmallChange = range.IntegerSnap ? 1 : 0.01,
            Value = Math.Clamp(initialValue, range.Min, range.Max),
        };

        var box = new NumberBox
        {
            Width = range.IntegerSnap || range.IsPercentage ? 64 : 84,
            Margin = new Thickness(10, 0, 0, 0),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            ClearButtonEnabled = false,
            // Whole numbers for percentages and integer-snapped fields; real decimal precision
            // otherwise - a raw value like sensitivity is often set to something specific
            // (0.1, 0.360000014, ...) that a rounded display would silently overwrite on commit.
            MaxDecimalPlaces = range.IntegerSnap || range.IsPercentage ? 0 : 4,
            Value = range.IsPercentage ? Math.Round(slider.Value * 100) : slider.Value,
        };

        var syncing = false;

        // Live-updates the paired box as the slider moves, but only commits (writes the file) once
        // the interaction actually ends - dragging shouldn't rewrite GlobalBasicSettings_13.xml on
        // every intermediate tick, matching how every other field here commits on blur, not per-edit.
        slider.ValueChanged += (_, e) =>
        {
            if (syncing) return;
            syncing = true;
            box.Value = range.IsPercentage ? Math.Round(e.NewValue * 100) : e.NewValue;
            syncing = false;
        };
        slider.PreviewMouseUp += (_, _) => Commit(slider.Value.ToString(CultureInfo.InvariantCulture));
        slider.LostKeyboardFocus += (_, _) => Commit(slider.Value.ToString(CultureInfo.InvariantCulture));

        box.LostKeyboardFocus += (_, _) =>
        {
            if (syncing) return;
            var boxMax = range.IsPercentage ? 100 : range.Max;
            var boxMin = range.IsPercentage ? 0 : range.Min;
            var clampedBoxValue = Math.Clamp(box.Value ?? boxMin, boxMin, boxMax);
            var rawValue = range.IsPercentage ? clampedBoxValue / 100.0 : clampedBoxValue;

            syncing = true;
            box.Value = clampedBoxValue;
            slider.Value = rawValue;
            syncing = false;

            Commit(rawValue.ToString(CultureInfo.InvariantCulture));
        };
        box.PreviewKeyDown += EnterCommits;

        panel.Children.Add(slider);
        panel.Children.Add(box);
        if (range.IsPercentage)
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "%", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), Opacity = 0.7,
            });

        return panel;
    }

    private void Commit(string value)
    {
        if (_loading) return;

        if (_field.Name == "FramerateCap" && int.TryParse(value, out var fps) && fps is > 0 and < 10)
        {
            WarningText.Text = "Under 10 FPS will make the game close to unplayable. It'll still be written.";
            WarningText.Visibility = Visibility.Visible;
        }
        else
        {
            WarningText.Visibility = Visibility.Collapsed;
        }

        GlobalBasicSettingsService.WriteScalar(_field.Name, _field.Tag, value);
        MarkCustomIfManaged();
        ResetButton.IsEnabled = GlobalBasicSettingsService.BackupExists();
    }

    private void CommitVector(double x, double y)
    {
        if (_loading) return;
        GlobalBasicSettingsService.WriteVector2(_field.Name, _field.Tag, (float)x, (float)y);
        MarkCustomIfManaged();
        ResetButton.IsEnabled = GlobalBasicSettingsService.BackupExists();
    }

    private void MarkCustomIfManaged()
    {
        if (PresetCatalog.ManagedGbsFields.Contains(_field.Name))
            PresetService.MarkCustom();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        GlobalBasicSettingsService.ResetField(_field.Name);

        var refreshed = GlobalBasicSettingsService.GetField(_field.Name);
        if (refreshed != null)
        {
            _field.RawValue = refreshed.RawValue;
            _field.VectorX = refreshed.VectorX;
            _field.VectorY = refreshed.VectorY;
        }

        // Rebuilding while _loading is true stops the freshly-constructed control's own initial
        // value assignment (e.g. ToggleSwitch.IsChecked) from re-triggering Commit() once it's
        // attached to the visual tree below.
        _loading = true;
        ValueHost.Content = BuildControl();
        _loading = false;

        WarningText.Visibility = Visibility.Collapsed;
        ResetButton.IsEnabled = GlobalBasicSettingsService.BackupExists();
    }

    private static bool ParseBool(string? s) => bool.TryParse(s, out var v) && v;
    private static double ParseDouble(string? s) => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>
    /// Enter should behave exactly like clicking away - not just commit the value but actually drop
    /// focus, so the box stops showing as focused. Keyboard.ClearFocus() alone commits the value
    /// (LostKeyboardFocus still fires) but leaves WPF's LOGICAL focus on the element, so its focus
    /// visual (the accent underline) stayed lit; clearing the focus scope's element too removes it.
    /// </summary>
    private static void EnterCommits(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        if (sender is System.Windows.DependencyObject element)
        {
            var scope = System.Windows.Input.FocusManager.GetFocusScope(element);
            System.Windows.Input.FocusManager.SetFocusedElement(scope, null);
        }
        System.Windows.Input.Keyboard.ClearFocus();
        e.Handled = true;
    }
}
