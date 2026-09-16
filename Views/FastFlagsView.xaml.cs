using System.Globalization;
using System.Windows.Controls;
using Lingstrap.Models;
using Lingstrap.Services;
using Wpf.Ui.Controls;

namespace Lingstrap.Views;

/// <summary>
/// The friendly FastFlags page (Froststrap-style). Named controls only - the user never types a
/// flag name here. Reads and writes the same flag store as FastFlagEditorView.
/// </summary>
public partial class FastFlagsView : Page
{
    private bool _loading = true;
    private bool _syncingRenderQuality;

    private static readonly int[] AntiAliasingValues = { 0, 1, 2, 4 };

    public FastFlagsView()
    {
        InitializeComponent();

        LoadRenderQuality();
        LoadTextureQuality();
        LoadAntiAliasing();
        LoadIntRow(FastFlagCatalog.GrassMinDistance, GrassMinToggle, GrassMinBox, 0);
        LoadIntRow(FastFlagCatalog.GrassMaxDistance, GrassMaxToggle, GrassMaxBox, 0);
        LoadIntRow(FastFlagCatalog.GrassMovement, GrassMovementToggle, GrassMovementBox, 0);
        LoadBoolRow(FastFlagCatalog.FreezeVoxelLighting, FreezeVoxelToggle);
        LoadBoolRow(FastFlagCatalog.GreySky, GreySkyToggle);
        LoadBoolRow(FastFlagCatalog.DisableDpiScaling, DisableDpiToggle);
        LoadRenderer();
        LoadLowPoly();
        LoadBoolRow(FastFlagCatalog.AltEnterFullscreen, AltEnterToggle);

        _loading = false;
    }

    // ---- pure bool rows (Freeze voxel lighting, Grey sky, Disable DPI scaling, Alt+Enter fullscreen) ----

    private static void LoadBoolRow(string flagName, ToggleSwitch toggle) =>
        toggle.IsChecked = FastFlagStore.Find(flagName) != null;

    private static void SaveBoolRow(string flagName, ToggleSwitch toggle)
    {
        if (toggle.IsChecked == true) FastFlagStore.Set(flagName, "true");
        else FastFlagStore.Remove(flagName);
    }

    private void FreezeVoxelToggle_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_loading) return;
        SaveBoolRow(FastFlagCatalog.FreezeVoxelLighting, FreezeVoxelToggle);
    }

    private void GreySkyToggle_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_loading) return;
        SaveBoolRow(FastFlagCatalog.GreySky, GreySkyToggle);
    }

    private void DisableDpiToggle_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_loading) return;
        SaveBoolRow(FastFlagCatalog.DisableDpiScaling, DisableDpiToggle);
    }

    private void AltEnterToggle_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_loading) return;
        SaveBoolRow(FastFlagCatalog.AltEnterFullscreen, AltEnterToggle);
    }

    // ---- enable-toggle + NumberBox rows (render quality, grass x3) ----

    private static void LoadIntRow(string flagName, ToggleSwitch toggle, Wpf.Ui.Controls.NumberBox box, double fallback)
    {
        var entry = FastFlagStore.Find(flagName);
        var on = entry != null;
        toggle.IsChecked = on;
        box.IsEnabled = on;
        box.Value = on && double.TryParse(entry!.Value, out var v) ? v : fallback;
    }

    private static void SaveIntToggle(string flagName, ToggleSwitch toggle, Wpf.Ui.Controls.NumberBox box)
    {
        box.IsEnabled = toggle.IsChecked == true;
        if (toggle.IsChecked == true) FastFlagStore.Set(flagName, IntStr(box.Value));
        else FastFlagStore.Remove(flagName);
    }

    // ---- render quality (slider + type-in box, bounded 1-21) ----

    private void LoadRenderQuality()
    {
        var entry = FastFlagStore.Find(FastFlagCatalog.RenderQuality);
        var on = entry != null;
        RenderQualityToggle.IsChecked = on;
        RenderQualitySlider.IsEnabled = on;
        RenderQualityBox.IsEnabled = on;

        var value = on && double.TryParse(entry!.Value, out var v) ? v : 21;
        value = System.Math.Clamp(value, 1, 21);
        RenderQualitySlider.Value = value;
        RenderQualityBox.Value = value;
    }

    private void RenderQualityToggle_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        var on = RenderQualityToggle.IsChecked == true;
        RenderQualitySlider.IsEnabled = on;
        RenderQualityBox.IsEnabled = on;
        if (_loading) return;

        if (on) FastFlagStore.Set(FastFlagCatalog.RenderQuality, IntStr(RenderQualitySlider.Value));
        else FastFlagStore.Remove(FastFlagCatalog.RenderQuality);
    }

    private void RenderQualitySlider_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        // Live-updates the paired box while dragging; the actual FastFlag write happens only once
        // the drag/keyboard interaction ends (see RenderQualitySlider_MouseUp/_LostFocus below).
        // RenderQualityBox can still be null here: setting Minimum/Maximum in XAML coerces the
        // slider's Value during InitializeComponent() itself, before later-declared elements like
        // RenderQualityBox have been constructed yet.
        if (_syncingRenderQuality || RenderQualityBox == null) return;
        _syncingRenderQuality = true;
        RenderQualityBox.Value = e.NewValue;
        _syncingRenderQuality = false;
    }

    private void RenderQualitySlider_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e) => CommitRenderQuality();
    private void RenderQualitySlider_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => CommitRenderQuality();

    private void CommitRenderQuality()
    {
        if (_loading || RenderQualityToggle.IsChecked != true) return;
        FastFlagStore.Set(FastFlagCatalog.RenderQuality, IntStr(RenderQualitySlider.Value));
    }

    private void RenderQualityBox_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (_syncingRenderQuality) return;
        var clamped = System.Math.Clamp(RenderQualityBox.Value ?? 1, 1, 21);

        _syncingRenderQuality = true;
        RenderQualityBox.Value = clamped;
        RenderQualitySlider.Value = clamped;
        _syncingRenderQuality = false;

        if (_loading || RenderQualityToggle.IsChecked != true) return;
        FastFlagStore.Set(FastFlagCatalog.RenderQuality, IntStr(clamped));
    }

    private void GrassMinToggle_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_loading) return;
        SaveIntToggle(FastFlagCatalog.GrassMinDistance, GrassMinToggle, GrassMinBox);
    }

    private void GrassMinBox_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (_loading || GrassMinToggle.IsChecked != true) return;
        FastFlagStore.Set(FastFlagCatalog.GrassMinDistance, IntStr(GrassMinBox.Value));
    }

    private void GrassMaxToggle_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_loading) return;
        SaveIntToggle(FastFlagCatalog.GrassMaxDistance, GrassMaxToggle, GrassMaxBox);
    }

    private void GrassMaxBox_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (_loading || GrassMaxToggle.IsChecked != true) return;
        FastFlagStore.Set(FastFlagCatalog.GrassMaxDistance, IntStr(GrassMaxBox.Value));
    }

    private void GrassMovementToggle_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_loading) return;
        SaveIntToggle(FastFlagCatalog.GrassMovement, GrassMovementToggle, GrassMovementBox);
    }

    private void GrassMovementBox_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (_loading || GrassMovementToggle.IsChecked != true) return;
        FastFlagStore.Set(FastFlagCatalog.GrassMovement, IntStr(GrassMovementBox.Value));
    }

    // ---- texture quality (combo + toggle, plus its own "enabled" flag) ----

    private void LoadTextureQuality()
    {
        var entry = FastFlagStore.Find(FastFlagCatalog.TextureQuality);
        var on = entry != null;
        TextureQualityToggle.IsChecked = on;
        TextureQualityCombo.IsEnabled = on;
        TextureQualityCombo.SelectedIndex = on && int.TryParse(entry!.Value, out var v) ? v : 0;
    }

    private void TextureQualityToggle_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        TextureQualityCombo.IsEnabled = TextureQualityToggle.IsChecked == true;
        if (_loading) return;

        if (TextureQualityToggle.IsChecked == true)
        {
            FastFlagStore.Set(FastFlagCatalog.TextureQuality, TextureQualityCombo.SelectedIndex.ToString(CultureInfo.InvariantCulture));
            FastFlagStore.Set(FastFlagCatalog.TextureQualityEnabled, "true");
        }
        else
        {
            FastFlagStore.Remove(FastFlagCatalog.TextureQuality);
            FastFlagStore.Remove(FastFlagCatalog.TextureQualityEnabled);
        }
    }

    private void TextureQualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TextureQualityToggle.IsChecked != true) return;
        FastFlagStore.Set(FastFlagCatalog.TextureQuality, TextureQualityCombo.SelectedIndex.ToString(CultureInfo.InvariantCulture));
    }

    // ---- anti-aliasing (combo mapped to 0/1/2/4, plus toggle) ----

    private void LoadAntiAliasing()
    {
        var entry = FastFlagStore.Find(FastFlagCatalog.AntiAliasing);
        var on = entry != null;
        AntiAliasingToggle.IsChecked = on;
        AntiAliasingCombo.IsEnabled = on;

        var index = 0;
        if (on && int.TryParse(entry!.Value, out var v))
        {
            var found = System.Array.IndexOf(AntiAliasingValues, v);
            if (found >= 0) index = found;
        }
        AntiAliasingCombo.SelectedIndex = index;
    }

    private void AntiAliasingToggle_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        AntiAliasingCombo.IsEnabled = AntiAliasingToggle.IsChecked == true;
        if (_loading) return;

        if (AntiAliasingToggle.IsChecked == true)
            FastFlagStore.Set(FastFlagCatalog.AntiAliasing, AntiAliasingValues[AntiAliasingCombo.SelectedIndex].ToString(CultureInfo.InvariantCulture));
        else
            FastFlagStore.Remove(FastFlagCatalog.AntiAliasing);
    }

    private void AntiAliasingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AntiAliasingToggle.IsChecked != true) return;
        FastFlagStore.Set(FastFlagCatalog.AntiAliasing, AntiAliasingValues[AntiAliasingCombo.SelectedIndex].ToString(CultureInfo.InvariantCulture));
    }

    // ---- renderer (unchanged: always exactly one active, not stored via FastFlagStore) ----

    private void LoadRenderer()
    {
        RendererCombo.SelectedIndex = SettingsService.Current.Renderer switch
        {
            "Vulkan" => 1,
            "OpenGL" => 2,
            _        => 0,
        };
    }

    private void RendererCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        SettingsService.Current.Renderer = RendererCombo.SelectedIndex switch
        {
            1 => "Vulkan",
            2 => "OpenGL",
            _ => "D3D11",
        };
        SettingsService.Save();
        Log.Info($"Renderer set to {SettingsService.Current.Renderer}");
    }

    // ---- low-poly meshes (one toggle governs all four CSG LOD flags) ----

    private void LoadLowPoly()
    {
        var entry = FastFlagStore.Find(FastFlagCatalog.LodBase);
        var on = entry != null;
        LowPolyToggle.IsChecked = on;

        LodBaseBox.IsEnabled = Lod12Box.IsEnabled = Lod23Box.IsEnabled = Lod34Box.IsEnabled = on;
        LodBaseBox.Value = ParseOr(FastFlagStore.Find(FastFlagCatalog.LodBase)?.Value, 0);
        Lod12Box.Value   = ParseOr(FastFlagStore.Find(FastFlagCatalog.Lod12)?.Value, 0);
        Lod23Box.Value   = ParseOr(FastFlagStore.Find(FastFlagCatalog.Lod23)?.Value, 0);
        Lod34Box.Value   = ParseOr(FastFlagStore.Find(FastFlagCatalog.Lod34)?.Value, 0);
    }

    private void LowPolyToggle_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        var on = LowPolyToggle.IsChecked == true;
        LodBaseBox.IsEnabled = Lod12Box.IsEnabled = Lod23Box.IsEnabled = Lod34Box.IsEnabled = on;
        if (_loading) return;

        if (on)
        {
            FastFlagStore.Set(FastFlagCatalog.LodBase, IntStr(LodBaseBox.Value));
            FastFlagStore.Set(FastFlagCatalog.Lod12, IntStr(Lod12Box.Value));
            FastFlagStore.Set(FastFlagCatalog.Lod23, IntStr(Lod23Box.Value));
            FastFlagStore.Set(FastFlagCatalog.Lod34, IntStr(Lod34Box.Value));
        }
        else
        {
            FastFlagStore.Remove(FastFlagCatalog.LodBase);
            FastFlagStore.Remove(FastFlagCatalog.Lod12);
            FastFlagStore.Remove(FastFlagCatalog.Lod23);
            FastFlagStore.Remove(FastFlagCatalog.Lod34);
        }
    }

    private void LodBox_LostFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (_loading || LowPolyToggle.IsChecked != true) return;
        FastFlagStore.Set(FastFlagCatalog.LodBase, IntStr(LodBaseBox.Value));
        FastFlagStore.Set(FastFlagCatalog.Lod12, IntStr(Lod12Box.Value));
        FastFlagStore.Set(FastFlagCatalog.Lod23, IntStr(Lod23Box.Value));
        FastFlagStore.Set(FastFlagCatalog.Lod34, IntStr(Lod34Box.Value));
    }

    private static string IntStr(double? v) => ((long)(v ?? 0)).ToString(CultureInfo.InvariantCulture);
    private static double ParseOr(string? s, double fallback) => double.TryParse(s, out var d) ? d : fallback;

    /// <summary>Enter confirms a NumberBox's typed value immediately, instead of only committing once focus moves elsewhere on its own.</summary>
    private void NumberBox_EnterCommits(object sender, System.Windows.Input.KeyEventArgs e)
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

    /// <summary>
    /// FastFlag Editor no longer has its own nav item - it's reached from here instead, so everything
    /// FastFlag-related lives under one tab. MainWindow's own generated field is internal by default
    /// (no x:FieldModifier set), so this works from any class in the same assembly without needing
    /// MainWindow to expose a dedicated navigation method.
    /// </summary>
    private void OpenFastFlagEditor_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (System.Windows.Window.GetWindow(this) is MainWindow window)
            window.RootNavigation.Navigate(typeof(FastFlagEditorView));
    }
}
