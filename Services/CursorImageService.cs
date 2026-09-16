using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lingstrap.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Lingstrap.Services;

/// <summary>
/// Converts a user-picked image into correctly-sized PNGs for every Roblox cursor file a slot maps
/// to. Each destination has its own real canvas size AND a content sub-rectangle within that
/// canvas that Roblox actually treats as the visible glyph - the custom image is fit into that
/// sub-rectangle, not stretched to fill the whole canvas, matching Roblox's own convention.
/// </summary>
public static class CursorImageService
{
    private const int FallbackSize = 32;
    public const int DefaultScalePercent = 100;
    public const int MinScalePercent = 25;
    public const int MaxScalePercent = 200;
    private static readonly string[] SupportedExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };

    /// <summary>
    /// Cursors whose real canvas is known to pad well beyond the visible glyph. Used to detect a
    /// corrupted "original" (backup, or reference file) - a full or near-full canvas of content
    /// for one of these means whatever wrote it wasn't Roblox's own asset.
    /// </summary>
    private static readonly HashSet<string> SparseCursorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ArrowCursor.png", "ArrowFarCursor.png", "IBeamCursor.png",
    };

    /// <summary>Every Roblox content-relative destination file this slot maps to. First entry is the "primary" one shown to the user.</summary>
    public static IReadOnlyList<string> Destinations(CursorSlot slot) => slot switch
    {
        CursorSlot.Mouse => new[]
        {
            "textures/Cursors/KeyboardMouse/ArrowCursor.png",
            "textures/Cursors/KeyboardMouse/ArrowFarCursor.png",
            "textures/Cursors/KeyboardMouse/IBeamCursor.png",
        },
        // Roblox reads the shift-lock cursor from content/textures/ directly - not content root,
        // and not nested under Cursors/KeyboardMouse/ like the arrow-family cursors are.
        CursorSlot.Shiftlock => new[] { "textures/MouseLockedCursor.png" },
        _ => Array.Empty<string>(),
    };

    /// <summary>The destination filename shown to the user and used for the preview - never the source file's own name.</summary>
    public static string PrimaryDestinationName(CursorSlot slot) => Path.GetFileName(Destinations(slot)[0]);

    /// <summary>ArrowFarCursor mirrors ArrowCursor's geometry instead of having its own read; every other destination sizes to itself.</summary>
    private static string SizeReference(string destinationRelativePath) =>
        destinationRelativePath.EndsWith("ArrowFarCursor.png", StringComparison.OrdinalIgnoreCase)
            ? "textures/Cursors/KeyboardMouse/ArrowCursor.png"
            : destinationRelativePath;

    private static bool IsSparse(string destinationRelativePath) =>
        SparseCursorNames.Contains(Path.GetFileName(SizeReference(destinationRelativePath)));

    private static string MasterPath(CursorSlot slot) => Path.Combine(Paths.Cursors, $"{slot}.master.png");

    /// <summary>The processed, correctly-sized PNG Lingstrap keeps for one destination file.</summary>
    public static string ProcessedPath(CursorSlot slot, string destinationRelativePath) =>
        Path.Combine(Paths.Cursors, $"{slot}.{Path.GetFileName(destinationRelativePath)}");

    public static string PreviewPath(CursorSlot slot) => ProcessedPath(slot, Destinations(slot)[0]);

    public static bool IsSupportedImage(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static bool HasSlot(CursorSlot slot) => File.Exists(MasterPath(slot));

    /// <summary>The user's chosen size percentage for this slot, clamped to what's actually usable, or the default (100%) if never set.</summary>
    public static int GetScalePercent(CursorSlot slot) =>
        SettingsService.Current.CursorScalePercent.TryGetValue(slot.ToString(), out var percent)
            ? Math.Clamp(percent, MinScalePercent, GetMaxSafePercent(slot))
            : DefaultScalePercent;

    /// <summary>
    /// The highest percentage this slot's current image can actually reach before any destination's
    /// content would overflow its real canvas (e.g. MouseLockedCursor's content box already fills its
    /// whole 32x32 canvas, so it has zero headroom above 100% - unlike the arrow-family cursors,
    /// whose 64x64 canvas leaves plenty of room). ProcessAll already clamps the write itself so it can
    /// never overflow regardless, but the slider should stop offering percentages that don't actually
    /// change anything - otherwise half its range does nothing. Takes the most restrictive destination
    /// within the slot, so nothing in it can ever clip. Falls back to the full MaxScalePercent range
    /// when nothing is loaded yet, so an empty slot's slider still has sane bounds.
    /// </summary>
    public static int GetMaxSafePercent(CursorSlot slot)
    {
        if (!HasSlot(slot)) return MaxScalePercent;

        using var master = Image.Load<Rgba32>(MasterPath(slot));
        using var trimmedSource = master.Clone(x => x.Crop(FindOpaqueBounds(master)));
        if (trimmedSource.Width <= 0 || trimmedSource.Height <= 0) return MaxScalePercent;

        var versionFolder = RobloxLocator.FindVersionFolder();
        var sourceLonger = Math.Max(trimmedSource.Width, trimmedSource.Height);

        double? mostRestrictive = null;
        foreach (var relative in Destinations(slot))
        {
            var geometry = GetGeometry(versionFolder, relative)
                           ?? new CursorGeometry(new Size(FallbackSize, FallbackSize), new Rectangle(0, 0, FallbackSize, FallbackSize));

            var boxLonger = Math.Max(geometry.ContentBounds.Width, geometry.ContentBounds.Height);
            if (boxLonger <= 0) continue;
            var baseScale = (double)boxLonger / sourceLonger;

            var maxScale = Math.Min(
                geometry.CanvasSize.Width / (double)trimmedSource.Width,
                geometry.CanvasSize.Height / (double)trimmedSource.Height);
            var maxPercentForThis = maxScale / baseScale * 100.0;

            if (mostRestrictive == null || maxPercentForThis < mostRestrictive)
                mostRestrictive = maxPercentForThis;
        }

        if (mostRestrictive is not { } value) return MaxScalePercent;
        return (int)Math.Clamp(Math.Floor(value), MinScalePercent, MaxScalePercent);
    }

    /// <summary>Persists the chosen percentage and reprocesses the slot's current image at that scale. Returns the primary destination's resulting pixel size.</summary>
    public static (int Width, int Height) SetScalePercent(CursorSlot slot, int percent)
    {
        percent = Math.Clamp(percent, MinScalePercent, GetMaxSafePercent(slot));
        SettingsService.Current.CursorScalePercent[slot.ToString()] = percent;
        SettingsService.Save();

        if (!HasSlot(slot)) return default;
        using var master = Image.Load<Rgba32>(MasterPath(slot));
        return ProcessAll(slot, master, percent);
    }

    /// <summary>The primary destination's current pixel size at this slot's stored percentage, without changing anything.</summary>
    public static (int Width, int Height) GetPrimaryTargetSize(CursorSlot slot)
    {
        if (!HasSlot(slot)) return default;
        using var master = Image.Load<Rgba32>(MasterPath(slot));
        return ProcessAll(slot, master, GetScalePercent(slot));
    }

    /// <summary>
    /// Loads the picked image and saves a resized copy for every destination this slot maps to, at
    /// this slot's current (or default) size percentage. No automatic size decision is made here -
    /// the percentage is entirely user-controlled via the slider; dropping an image just applies
    /// whatever percentage (or the 100% default) was already set for this slot.
    /// </summary>
    public static (int Width, int Height) SetSlot(CursorSlot slot, string sourceFilePath)
    {
        Directory.CreateDirectory(Paths.Cursors);

        using var master = Image.Load<Rgba32>(sourceFilePath);
        master.SaveAsPng(MasterPath(slot));

        var percent = GetScalePercent(slot);
        var size = ProcessAll(slot, master, percent);

        Log.Info($"Cursor slot {slot} set from {sourceFilePath} at {percent}%");
        return size;
    }

    /// <summary>
    /// Scales the source (trimmed to its own opaque content) by the given percentage of Roblox's own
    /// content-box size, then pastes it onto a transparent canvas matching that destination's real,
    /// full native size. Returns the primary (first) destination's resulting pixel size.
    /// </summary>
    private static (int Width, int Height) ProcessAll(CursorSlot slot, Image<Rgba32> master, int percent)
    {
        var versionFolder = RobloxLocator.FindVersionFolder();

        // Trim the source down to its own opaque content before scaling. Left untrimmed, any
        // transparent padding baked into the picked image (common in exported cursor art - e.g. an
        // 18x18 glyph sitting in the middle of a 38x38 canvas) would throw off the percentage scale,
        // since it's meant to describe the visible glyph's size, not the padded source file's.
        var sourceContentBounds = FindOpaqueBounds(master);
        using var trimmedSource = master.Clone(x => x.Crop(sourceContentBounds));

        (int Width, int Height) primarySize = default;
        var isPrimary = true;

        foreach (var relative in Destinations(slot))
        {
            var geometry = GetGeometry(versionFolder, relative)
                           ?? new CursorGeometry(new Size(FallbackSize, FallbackSize), new Rectangle(0, 0, FallbackSize, FallbackSize));

            // 100% = the source's longer edge matches the box's longer edge (Roblox's own native
            // bbox size for this destination) - the user's percentage scales relative to that.
            var sourceLonger = Math.Max(trimmedSource.Width, trimmedSource.Height);
            var boxLonger = Math.Max(geometry.ContentBounds.Width, geometry.ContentBounds.Height);
            var baseScale = sourceLonger > 0 ? (double)boxLonger / sourceLonger : 1.0;
            var scale = baseScale * (percent / 100.0);

            var targetWidth = Math.Max(1, (int)Math.Round(trimmedSource.Width * scale));
            var targetHeight = Math.Max(1, (int)Math.Round(trimmedSource.Height * scale));

            // Never let content exceed the actual canvas - some destinations (e.g. MouseLockedCursor,
            // whose content box already fills its whole native canvas) have zero headroom above 100%,
            // so a percentage that's fine for one destination can silently overflow another. Without
            // this, the overflowing part gets clipped off during DrawImage, the displayed "WxH" no
            // longer matches what's actually visible, and two cursors reporting the same size end up
            // looking different - one still fits its canvas with margin, the other is cropped edge-to-edge.
            var overflowScale = Math.Min(
                (double)geometry.CanvasSize.Width / targetWidth,
                (double)geometry.CanvasSize.Height / targetHeight);
            if (overflowScale < 1.0)
            {
                targetWidth = Math.Max(1, (int)Math.Round(targetWidth * overflowScale));
                targetHeight = Math.Max(1, (int)Math.Round(targetHeight * overflowScale));
            }

            using var resizedContent = trimmedSource.Clone(x => x.Resize(targetWidth, targetHeight));

            // Centered directly on the full canvas - simple and predictable: what you see in the
            // preview is exactly what gets written, with equal transparent margin on every side.
            var drawX = (geometry.CanvasSize.Width - targetWidth) / 2;
            var drawY = (geometry.CanvasSize.Height - targetHeight) / 2;

            using var output = new Image<Rgba32>(geometry.CanvasSize.Width, geometry.CanvasSize.Height);
            output.Mutate(x => x.DrawImage(resizedContent, new Point(drawX, drawY), 1f));
            output.SaveAsPng(ProcessedPath(slot, relative));

            Log.Info($"Cursor slot {slot} -> {relative}: {percent}% source={trimmedSource.Width}x{trimmedSource.Height} -> " +
                     $"resized={targetWidth}x{targetHeight} at ({drawX},{drawY}) on canvas {geometry.CanvasSize.Width}x{geometry.CanvasSize.Height} (box={Describe(geometry.ContentBounds)})");

            if (isPrimary)
            {
                primarySize = (targetWidth, targetHeight);
                isPrimary = false;
            }
        }

        return primarySize;
    }

    /// <summary>
    /// The real geometry for a destination. For the arrow-family cursors (nested under
    /// Cursors/KeyboardMouse/), Lingstrap never writes to the plain textures/&lt;name&gt;.png copy of
    /// the same file, so that copy is permanently safe from a stale Lingstrap backup and is
    /// preferred. Otherwise falls back to the backup made before Lingstrap's first-ever overwrite,
    /// or the live file if Lingstrap hasn't touched this destination yet. Null if nothing usable
    /// is found (logged so it's clear why the 32x32 fallback was used).
    /// </summary>
    private static CursorGeometry? GetGeometry(string? versionFolder, string destinationRelativePath)
    {
        if (versionFolder == null)
        {
            Log.Warn($"Cursor geometry for {destinationRelativePath}: Roblox isn't installed - using {FallbackSize}x{FallbackSize} fallback.");
            return null;
        }

        var sparse = IsSparse(destinationRelativePath);
        var sizeRefRelative = SizeReference(destinationRelativePath);

        if (sizeRefRelative.Contains("Cursors/KeyboardMouse", StringComparison.OrdinalIgnoreCase))
        {
            var flatRelative = "textures/" + Path.GetFileName(sizeRefRelative);
            var flatPath = Path.Combine(versionFolder, "content", flatRelative.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(flatPath))
            {
                var flatGeo = ReadGeometry(flatPath);
                if (flatGeo != null && !(sparse && LooksSuspicious(flatGeo)))
                {
                    Log.Info($"Cursor geometry for {destinationRelativePath}: read from untouched reference {flatPath} -> " +
                             $"canvas={flatGeo.CanvasSize.Width}x{flatGeo.CanvasSize.Height}, content={Describe(flatGeo.ContentBounds)}");
                    return flatGeo;
                }
                if (flatGeo != null)
                    Log.Warn($"Cursor reference {flatPath} looks corrupted too (content fills {Describe(flatGeo.ContentBounds)} of a " +
                             $"{flatGeo.CanvasSize.Width}x{flatGeo.CanvasSize.Height} canvas) - falling back.");
            }
        }

        var dest = Path.Combine(versionFolder, "content", sizeRefRelative.Replace('/', Path.DirectorySeparatorChar));
        var backup = dest + ModsService.BackupSuffix;
        var probePath = File.Exists(backup) ? backup : (File.Exists(dest) ? dest : null);
        if (probePath == null)
        {
            Log.Warn($"Cursor geometry for {destinationRelativePath}: no backup or live file found at {dest} - using {FallbackSize}x{FallbackSize} fallback.");
            return null;
        }

        var geo = ReadGeometry(probePath);
        if (geo == null) return null;

        if (sparse && LooksSuspicious(geo))
        {
            Log.Warn($"Cursor {(probePath == backup ? "backup" : "live file")} {probePath} looks corrupted (content fills {Describe(geo.ContentBounds)} of a " +
                     $"{geo.CanvasSize.Width}x{geo.CanvasSize.Height} canvas) - likely written by an old Lingstrap build before this fix existed, or Roblox itself " +
                     "needs repairing. Try \"Reinitialize from current Roblox install\", or verify/repair Roblox if that doesn't help. Using " +
                     $"{FallbackSize}x{FallbackSize} fallback for now.");
            return null;
        }

        Log.Info($"Cursor geometry for {destinationRelativePath}: read from {(probePath == backup ? "backup" : "live")} {probePath} -> " +
                 $"canvas={geo.CanvasSize.Width}x{geo.CanvasSize.Height}, content={Describe(geo.ContentBounds)}");
        return geo;
    }

    private static CursorGeometry? ReadGeometry(string path)
    {
        try
        {
            using var image = Image.Load<Rgba32>(path);
            return new CursorGeometry(new Size(image.Width, image.Height), FindOpaqueBounds(image));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read cursor geometry from {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>A cursor whose visible content covers most of its own canvas is not one of the sparse arrow-family originals.</summary>
    private static bool LooksSuspicious(CursorGeometry geometry)
    {
        double canvasArea = geometry.CanvasSize.Width * geometry.CanvasSize.Height;
        double contentArea = geometry.ContentBounds.Width * geometry.ContentBounds.Height;
        return canvasArea > 0 && contentArea / canvasArea > 0.5;
    }

    private static Rectangle FindOpaqueBounds(Image<Rgba32> image)
    {
        int minX = image.Width, minY = image.Height, maxX = -1, maxY = -1;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    if (row[x].A <= 10) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        });

        return maxX < 0
            ? new Rectangle(0, 0, image.Width, image.Height) // fully transparent - treat the whole canvas as content
            : new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static string Describe(Rectangle r) => $"({r.X},{r.Y}) {r.Width}x{r.Height}";

    /// <summary>Removes the picked image and processed files for this slot, and resets its size percentage back to the 100% default.</summary>
    public static void ClearSlot(CursorSlot slot)
    {
        var master = MasterPath(slot);
        if (File.Exists(master)) File.Delete(master);

        foreach (var relative in Destinations(slot))
        {
            var processed = ProcessedPath(slot, relative);
            if (File.Exists(processed)) File.Delete(processed);
        }

        SettingsService.Current.CursorScalePercent.Remove(slot.ToString());
        SettingsService.Save();
    }
}
