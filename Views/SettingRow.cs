using System.Windows;
using System.Windows.Controls;

namespace Lingstrap.Views;

/// <summary>
/// One labelled setting - a title, an optional description, and whatever control operates it on the
/// right (the control goes in as this element's content). Rows sit inside a single bordered group
/// (see the SettingsGroup style) divided from each other by hairlines, rather than each setting
/// being its own filled card: a page of those reads as a stack of separate blocks pasted onto the
/// background, and takes roughly twice the vertical space for the same content.
///
/// Deliberately a templated ContentControl rather than a UserControl. A UserControl defines its own
/// XAML namescope, so a page couldn't put a named control inside one - every x:Name in the content
/// failed to register ("already had a name registered when it was defined in another scope"), which
/// is exactly what these rows need to do for their toggles and combo boxes.
/// </summary>
public class SettingRow : ContentControl
{
    static SettingRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SettingRow), new FrameworkPropertyMetadata(typeof(SettingRow)));
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(""));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(""));

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>Off for the last row in a group, so the group's own border is the only line along the
    /// bottom rather than two of them a pixel apart.</summary>
    public static readonly DependencyProperty ShowDividerProperty = DependencyProperty.Register(
        nameof(ShowDivider), typeof(bool), typeof(SettingRow), new PropertyMetadata(true));

    public bool ShowDivider
    {
        get => (bool)GetValue(ShowDividerProperty);
        set => SetValue(ShowDividerProperty, value);
    }
}
