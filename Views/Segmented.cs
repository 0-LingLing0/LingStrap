using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Lingstrap.Views;

/// <summary>
/// A row of chips acting as one control - the selected one carries the accent, the rest are plain.
/// Used wherever a handful of short, mutually exclusive options read better laid out than folded into
/// a dropdown (themes, UI scale, the Presets page's two lists).
/// </summary>
public static class Segmented
{
    /// <summary>Returns the row plus the callback that moves the selection, so the caller's click
    /// handler decides whether a click should actually take effect.</summary>
    public static (FrameworkElement Root, Action<int> SetSelected) Build(
        string[] labels, int selectedIndex, Action<int> onSelect)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var chips = new List<Border>();

        for (var i = 0; i < labels.Length; i++)
        {
            var text = new TextBlock
            {
                Text = labels[i],
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var chip = new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(18, 8, 18, 8),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
                Child = text,
            };

            var index = i;
            chip.MouseLeftButtonUp += (_, _) => onSelect(index);
            chips.Add(chip);
            row.Children.Add(chip);
        }

        void SetSelected(int index)
        {
            for (var i = 0; i < chips.Count; i++)
            {
                var selected = i == index;
                var label = (TextBlock)chips[i].Child;

                if (selected)
                {
                    chips[i].SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
                    chips[i].SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
                    label.SetResourceReference(TextBlock.ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
                }
                else
                {
                    chips[i].SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
                    chips[i].SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
                    label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
                }
            }
        }

        SetSelected(selectedIndex);
        return (row, SetSelected);
    }
}
