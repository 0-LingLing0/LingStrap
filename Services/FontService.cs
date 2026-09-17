using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lingstrap.Services;

/// <summary>
/// Replaces the font Roblox's whole interface is drawn in with one of the user's own.
///
/// Roblox doesn't have a "UI font" setting to point somewhere else - it resolves fonts through
/// content/fonts/families/*.json, one file per family (Builder Sans, Arimo, Gotham and so on), each
/// listing its faces with an assetId that's either a local rbxasset://fonts/&lt;file&gt; or an
/// rbxassetid:// Roblox downloads. Swapping the font therefore means dropping the chosen file into
/// content/fonts and repointing every face of every family at it, which is what every other
/// bootstrapper's custom-font feature does too. Families are covered rather than just the default
/// one because games pick their own fonts, and a half-replaced UI looks worse than an untouched one.
///
/// The files themselves are written through ModsService, so they're backed up and put back exactly
/// like any other replaced client file - see BuildFiles.
/// </summary>
public static class FontService
{
    private static readonly string[] SupportedExtensions = { ".ttf", ".otf" };

    /// <summary>The name Roblox sees. Kept fixed so a font swap overwrites the previous one rather
    /// than leaving orphans behind in the client's fonts folder.</summary>
    private const string InstalledName = "LingstrapCustomFont";

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>The stored font file, or null if the Roblox default is in use.</summary>
    public static string? CurrentFontFile()
    {
        if (!Directory.Exists(Paths.Fonts)) return null;
        return Directory.GetFiles(Paths.Fonts, InstalledName + ".*").FirstOrDefault();
    }

    public static bool HasCustomFont() => CurrentFontFile() != null;

    /// <summary>Copies the picked font into Lingstrap's own folder, so the original can be moved or
    /// deleted afterwards without the next launch silently falling back to Roblox's font.</summary>
    public static void SetFont(string sourcePath)
    {
        Directory.CreateDirectory(Paths.Fonts);
        Clear();

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        File.Copy(sourcePath, Path.Combine(Paths.Fonts, InstalledName + extension), overwrite: true);

        SettingsService.Current.CustomFontName = Path.GetFileNameWithoutExtension(sourcePath);
        SettingsService.Save();

        Log.Info($"Custom UI font set from {sourcePath}");
    }

    public static void Clear()
    {
        if (Directory.Exists(Paths.Fonts))
        {
            foreach (var file in Directory.GetFiles(Paths.Fonts, InstalledName + ".*"))
            {
                try { File.Delete(file); }
                catch (Exception ex) { Log.Warn($"Could not remove the stored custom font: {ex.Message}"); }
            }
        }

        SettingsService.Current.CustomFontName = null;
        SettingsService.Save();
    }

    /// <summary>One file Lingstrap needs to place under the client's content folder.</summary>
    public record GeneratedFile(string RelativePath, byte[] Contents);

    /// <summary>
    /// The font file plus a rewritten copy of every font family, ready for ModsService to write with
    /// its usual backup. Empty when no custom font is set, or when this Roblox install has no font
    /// families to rewrite (nothing to point at the new file, so dropping it in would do nothing).
    /// </summary>
    public static IEnumerable<GeneratedFile> BuildFiles(string versionFolder)
    {
        var fontFile = CurrentFontFile();
        if (fontFile == null) yield break;

        var familiesDir = Path.Combine(versionFolder, "content", "fonts", "families");
        if (!Directory.Exists(familiesDir))
        {
            Log.Warn($"Custom font: no font families found at {familiesDir} - leaving fonts alone.");
            yield break;
        }

        var installedFileName = InstalledName + Path.GetExtension(fontFile);
        var assetId = $"rbxasset://fonts/{installedFileName}";

        byte[] fontBytes;
        try
        {
            fontBytes = File.ReadAllBytes(fontFile);
        }
        catch (Exception ex)
        {
            Log.Warn($"Custom font: could not read {fontFile}: {ex.Message}");
            yield break;
        }

        yield return new GeneratedFile($"fonts/{installedFileName}", fontBytes);

        var rewritten = 0;
        foreach (var family in Directory.GetFiles(familiesDir, "*.json"))
        {
            var json = TryRewriteFamily(family, assetId);
            if (json == null) continue;

            rewritten++;
            yield return new GeneratedFile($"fonts/families/{Path.GetFileName(family)}", Encoding.UTF8.GetBytes(json));
        }

        Log.Info($"Custom font: {Path.GetFileName(fontFile)} applied to {rewritten} font families.");
    }

    /// <summary>Repoints every face in one family file at the custom font. Null when the file isn't a
    /// font family this understands, so an unexpected file is skipped rather than corrupted.</summary>
    private static string? TryRewriteFamily(string path, string assetId)
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path));
            if (root?["faces"] is not JsonArray faces || faces.Count == 0) return null;

            foreach (var face in faces)
            {
                if (face is JsonObject obj) obj["assetId"] = assetId;
            }

            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Custom font: skipped {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Identifies the current font for ModsService's change check, so picking a different
    /// one (or clearing it) actually triggers a reapply on the next launch.</summary>
    public static string Fingerprint()
    {
        var file = CurrentFontFile();
        if (file == null) return "Font=none;";

        try
        {
            var info = new FileInfo(file);
            return $"Font={info.Name}:{info.LastWriteTimeUtc.Ticks}:{info.Length};";
        }
        catch
        {
            return "Font=unreadable;";
        }
    }
}
