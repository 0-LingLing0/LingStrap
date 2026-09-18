using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lingstrap.Models;
using Lingstrap.Services;

namespace Lingstrap.Views;

/// <summary>A drag-drop-or-click square for picking one cursor slot's image.</summary>
public partial class CursorSlotControl : UserControl
{
    private readonly CursorSlot _slot;

    // Starts true (not false) because setting Minimum/Maximum in XAML coerces Slider.Value from its
    // default of 0 up into range during InitializeComponent() itself, firing ValueChanged before any
    // statement in this constructor has run - without this, that spurious event would try to process
    // and read back a cursor slot that may not even have an image yet, and crash.
    private bool _loadingSlider = true;

    // Set while a drag is in flight; committed by _scaleCommitTimer once it settles.
    private int? _pendingPercent;
    private DispatcherTimer? _scaleCommitTimer;

    public CursorSlotControl(CursorSlot slot, string title)
    {
        InitializeComponent();
        _slot = slot;
        TitleText.Text = title;

        if (CursorImageService.HasSlot(slot))
            ShowFilled();
        else
            ShowEmpty();

        _loadingSlider = false;
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files &&
            CursorImageService.IsSupportedImage(files[0]))
        {
            e.Effects = DragDropEffects.Copy;
            SetDragOver(true);
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e) => SetDragOver(false);

    private void DropZone_MouseEnter(object sender, MouseEventArgs e) => SetHover(true);
    private void DropZone_MouseLeave(object sender, MouseEventArgs e) => SetHover(false);

    /// <summary>Brightens whichever border is currently showing (filled or empty) on plain mouse hover - separate from the accent drag-over highlight.</summary>
    private void SetHover(bool over)
    {
        var opacity = over ? 1.0 : 0.85;
        if (PreviewImage.Visibility == Visibility.Visible) SolidBorder.Opacity = opacity;
        else DashedBorder.Opacity = over ? 1.0 : 0.7;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        SetDragOver(false);
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        var file = files.FirstOrDefault(CursorImageService.IsSupportedImage);
        if (file != null) ApplyFile(file);
    }

    private void DropZone_Click(object sender, MouseButtonEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a cursor image",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
        };

        if (dialog.ShowDialog() == true)
            ApplyFile(dialog.FileName);
    }

    private async void ApplyFile(string path)
    {
        try
        {
            CursorImageService.SetSlot(_slot, path);
            ShowFilled();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not process cursor image {path}", ex);
            await DialogHelper.ShowErrorAsync(this, $"Could not use that image:\n{ex.Message}");
        }
    }

    /// <summary>
    /// Dragging the slider raises one ValueChanged per tick - around 200 across a single drag. Doing
    /// the real work on each of those re-encoded three PNGs, rewrote Settings.json and wrote six log
    /// lines per tick, for ~600 throwaway images whose only lasting effect was the last one. This
    /// waits for the drag to settle and then does it once. The label still updates on every tick, so
    /// the slider stays live; it's only the writing that's deferred.
    /// </summary>
    private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingSlider) return;

        _pendingPercent = (int)Math.Round(e.NewValue);
        ScaleSizeText.Text = $"{_pendingPercent}%";

        _scaleCommitTimer ??= CreateCommitTimer();
        _scaleCommitTimer.Stop();
        _scaleCommitTimer.Start();
    }

    private DispatcherTimer CreateCommitTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_pendingPercent is not { } percent) return;
            _pendingPercent = null;

            try
            {
                var size = CursorImageService.SetScalePercent(_slot, percent);
                RefreshThumbnail();
                ScaleSizeText.Text = $"{size.Width} x {size.Height}";
            }
            catch (Exception ex)
            {
                Log.Error($"Could not resize cursor slot {_slot} to {percent}%", ex);
            }
        };
        return timer;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        CursorImageService.ClearSlot(_slot);

        var versionFolder = RobloxLocator.FindVersionFolder();
        if (versionFolder != null)
            ModsService.RestoreCursorSlotNow(_slot, versionFolder);

        ShowEmpty();
    }

    private void ShowFilled()
    {
        RefreshThumbnail();

        FileNameText.Text = CursorImageService.PrimaryDestinationName(_slot);
        FileNameText.Visibility = Visibility.Visible;

        ClearButton.IsEnabled = true;

        ScalePanel.Visibility = Visibility.Visible;
        _loadingSlider = true;
        // Some destinations (e.g. Shiftlock's MouseLockedCursor, whose content box already fills its
        // whole native canvas) have far less headroom above 100% than others - capping the slider's
        // own Maximum here means the whole range stays meaningful instead of half of it doing nothing.
        ScaleSlider.Maximum = CursorImageService.GetMaxSafePercent(_slot);
        ScaleSlider.Value = CursorImageService.GetScalePercent(_slot);
        _loadingSlider = false;

        var size = CursorImageService.GetPrimaryTargetSize(_slot);
        ScaleSizeText.Text = $"{size.Width} x {size.Height}";
    }

    private void RefreshThumbnail()
    {
        var bytes = File.ReadAllBytes(CursorImageService.PreviewPath(_slot));
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.EndInit();
        bmp.Freeze();

        PreviewImage.Source = bmp;

        // Scale every cursor's preview by the same pixels-per-canvas-pixel factor, calibrated so the
        // largest native canvas (the arrow-family cursors' 64x64) fills the box - instead of letting
        // each canvas independently stretch to fill the same square regardless of its real size.
        // Roblox itself doesn't upscale a smaller cursor texture to match a bigger one, so Shiftlock's
        // 32x32 MouseLockedCursor genuinely renders smaller in-game than the 64x64 arrow cursor -
        // stretching both to the same box made two differently-sized cursors look identical here.
        const double pixelsPerCanvasPixel = 100.0 / 64.0;
        PreviewImage.Width = bmp.PixelWidth * pixelsPerCanvasPixel;
        PreviewImage.Height = bmp.PixelHeight * pixelsPerCanvasPixel;

        PreviewImage.Visibility = Visibility.Visible;
        HintText.Visibility = Visibility.Collapsed;
        SolidBorder.Visibility = Visibility.Visible;
        DashedBorder.Visibility = Visibility.Collapsed;
    }

    private void ShowEmpty()
    {
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        HintText.Visibility = Visibility.Visible;
        SolidBorder.Visibility = Visibility.Collapsed;
        DashedBorder.Visibility = Visibility.Visible;

        FileNameText.Text = "";
        FileNameText.Visibility = Visibility.Collapsed;

        ClearButton.IsEnabled = false;
        ScalePanel.Visibility = Visibility.Collapsed;
    }

    private void SetDragOver(bool over)
    {
        var brushKey = over ? "SystemAccentBrush" : "ControlStrokeColorDefaultBrush";
        var brush = (System.Windows.Media.Brush)FindResource(brushKey);
        SolidBorder.BorderBrush = brush;
        DashedBorder.Stroke = brush;
    }
}
