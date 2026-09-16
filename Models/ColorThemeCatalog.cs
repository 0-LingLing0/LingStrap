using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Lingstrap.Models;

/// <summary>
/// One accent color option: a base color plus the lighter/darker variants
/// ApplicationAccentColorManager needs for its light/dark ramps. See App.xaml.cs for why these are
/// picked by hand rather than generated - WPF-UI's automatic dark-theme ramp washes out light base
/// colors when it lightens them further to derive the button/toggle fill.
/// </summary>
public record ColorTheme(string Name, Color Base, Color Light, Color Dark);

public static class ColorThemeCatalog
{
    public static readonly ColorTheme[] All =
    {
        new("Violet", Color.FromRgb(0xA7, 0x78, 0xFF), Color.FromRgb(0xC7, 0xA9, 0xFF), Color.FromRgb(0x58, 0x30, 0xB4)),
        new("Blue",   Color.FromRgb(0x4C, 0x9A, 0xFF), Color.FromRgb(0x8B, 0xC0, 0xFF), Color.FromRgb(0x1E, 0x4F, 0x9C)),
        new("Cyan",   Color.FromRgb(0x3D, 0xD6, 0xE8), Color.FromRgb(0x8A, 0xEA, 0xF2), Color.FromRgb(0x1B, 0x7A, 0x87)),
        new("Teal",   Color.FromRgb(0x2E, 0xC7, 0xA7), Color.FromRgb(0x82, 0xE8, 0xD1), Color.FromRgb(0x14, 0x6E, 0x5B)),
        new("Green",  Color.FromRgb(0x4C, 0xC9, 0x5A), Color.FromRgb(0x93, 0xE3, 0x9C), Color.FromRgb(0x22, 0x70, 0x2A)),
        new("Lime",   Color.FromRgb(0xA8, 0xD8, 0x3D), Color.FromRgb(0xCB, 0xE9, 0x8A), Color.FromRgb(0x5D, 0x78, 0x1A)),
        new("Gold",   Color.FromRgb(0xE8, 0xB1, 0x3D), Color.FromRgb(0xF2, 0xD1, 0x8A), Color.FromRgb(0x8A, 0x62, 0x14)),
        new("Orange", Color.FromRgb(0xFF, 0x8A, 0x3D), Color.FromRgb(0xFF, 0xBB, 0x8A), Color.FromRgb(0x9C, 0x4C, 0x1E)),
        new("Red",    Color.FromRgb(0xF2, 0x4C, 0x4C), Color.FromRgb(0xF7, 0x9A, 0x9A), Color.FromRgb(0x8F, 0x1E, 0x1E)),
        new("Pink",   Color.FromRgb(0xF2, 0x4C, 0xB0), Color.FromRgb(0xF7, 0x9A, 0xD6), Color.FromRgb(0x8F, 0x1E, 0x69)),
        new("Indigo", Color.FromRgb(0x6B, 0x6C, 0xF2), Color.FromRgb(0xA9, 0xAA, 0xF7), Color.FromRgb(0x36, 0x37, 0x8F)),
        new("Slate",  Color.FromRgb(0x8A, 0x93, 0xA1), Color.FromRgb(0xBD, 0xC4, 0xCE), Color.FromRgb(0x45, 0x4B, 0x54)),
    };

    public static ColorTheme Find(string? name) =>
        All.FirstOrDefault(t => t.Name == name) ?? All[0];

    /// <summary>
    /// The base color of whichever theme is currently applied, read back from the live resource
    /// ApplicationAccentColorManager.Apply just set - so callers always match what's on screen right
    /// now instead of going stale after the user switches themes on the Appearance page.
    /// </summary>
    public static Color CurrentAccentColor() => (Color)Application.Current.Resources["SystemAccentColor"];
}
