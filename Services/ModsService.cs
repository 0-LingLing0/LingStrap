using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Lingstrap.Models;

namespace Lingstrap.Services;

/// <summary>
/// Copies Mods/ and the cursor slots over a Roblox version's content directory at launch. Every
/// overwritten file is backed up so it can be put back exactly as Roblox shipped it once mods are
/// disabled or a cursor slot is cleared.
/// </summary>
public static class ModsService
{
    public const string BackupSuffix = ".lingstrap-original";

    public static void Apply(string versionFolder)
    {
        // Restoring then re-copying every mod and cursor file is real, redundant file I/O when
        // absolutely nothing has changed since the last launch against this exact version folder -
        // the overwhelmingly common case (same mods, same cursors, no Roblox update). A fingerprint
        // of what SHOULD be applied, stored alongside the manifest, lets a launch skip all of that
        // work entirely when it's still accurate, without needing to actually touch any files to
        // find out - a real, measurable chunk of a launch's total time on anything but a tiny Mods
        // folder, and the biggest concrete difference from a bootstrapper that never redoes this
        // unless something's actually different.
        var fingerprint = ComputeFingerprint();
        if (fingerprint == LoadFingerprint(versionFolder))
        {
            Log.Info("Mods and cursors unchanged since the last launch - skipping reapply.");
            return;
        }

        RestoreAll(versionFolder); // undo whatever the previous launch applied before reapplying

        var applied = new List<string>();
        var s = SettingsService.Current;
        var contentRoot = Path.Combine(versionFolder, "content");

        if (s.ModsEnabled && Directory.Exists(Paths.Mods))
        {
            foreach (var file in Directory.GetFiles(Paths.Mods, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(Paths.Mods, file);
                CopyWithBackup(file, Path.Combine(contentRoot, rel), applied);
            }
        }

        ApplyCursorSlot(CursorSlot.Mouse, contentRoot, applied);
        ApplyCursorSlot(CursorSlot.Shiftlock, contentRoot, applied);

        // Generated rather than copied from a source file: the font itself is copied, but each font
        // family is Roblox's own JSON with its faces repointed, so it has to be built from what's
        // installed. Written through the same backup path as everything else so Restore puts the
        // original families back untouched.
        foreach (var file in FontService.BuildFiles(versionFolder))
        {
            var dest = Path.Combine(contentRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            WriteWithBackup(file.Contents, dest, applied);
        }

        SaveManifest(versionFolder, applied);
        SaveFingerprint(versionFolder, fingerprint);
        Log.Info($"Mods applied: {applied.Count} file(s) overwritten in {versionFolder}");
    }

    /// <summary>
    /// A deterministic text description of everything that would affect what Apply() writes: whether
    /// mods are enabled and every source file's path/size/modified-time, plus each cursor slot's
    /// current processed output the same way. Comparing this against what was saved after the last
    /// real apply is what lets Apply() know nothing has actually changed, without re-copying
    /// anything to check.
    /// </summary>
    private static string ComputeFingerprint()
    {
        var s = SettingsService.Current;
        var sb = new StringBuilder();
        sb.Append("ModsEnabled=").Append(s.ModsEnabled).Append(';');

        if (s.ModsEnabled && Directory.Exists(Paths.Mods))
        {
            var files = Directory.GetFiles(Paths.Mods, "*", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var rel = Path.GetRelativePath(Paths.Mods, file);
                var info = new FileInfo(file);
                sb.Append(rel).Append(':').Append(info.LastWriteTimeUtc.Ticks).Append(':').Append(info.Length).Append(';');
            }
        }

        foreach (var slot in Enum.GetValues<CursorSlot>())
        {
            if (!CursorImageService.HasSlot(slot)) { sb.Append(slot).Append("=none;"); continue; }

            foreach (var relative in CursorImageService.Destinations(slot))
            {
                var processed = CursorImageService.ProcessedPath(slot, relative);
                if (!File.Exists(processed)) { sb.Append(slot).Append(':').Append(relative).Append("=missing;"); continue; }

                var info = new FileInfo(processed);
                sb.Append(slot).Append(':').Append(relative).Append(':').Append(info.LastWriteTimeUtc.Ticks).Append(':').Append(info.Length).Append(';');
            }
        }

        sb.Append(FontService.Fingerprint());

        return sb.ToString();
    }

    private static string FingerprintPath(string versionFolder) => Path.Combine(versionFolder, "Lingstrap.ModFingerprint.txt");

    private static string? LoadFingerprint(string versionFolder)
    {
        var path = FingerprintPath(versionFolder);
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    private static void SaveFingerprint(string versionFolder, string fingerprint)
    {
        try { File.WriteAllText(FingerprintPath(versionFolder), fingerprint); }
        catch (Exception ex) { Log.Warn($"Could not save mod fingerprint: {ex.Message}"); }
    }

    private static void ApplyCursorSlot(CursorSlot slot, string contentRoot, List<string> applied)
    {
        if (!CursorImageService.HasSlot(slot)) return;

        foreach (var relative in CursorImageService.Destinations(slot))
        {
            var source = CursorImageService.ProcessedPath(slot, relative);
            if (!File.Exists(source)) continue;

            var dest = Path.Combine(contentRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            CopyWithBackup(source, dest, applied);
        }
    }

    private static IEnumerable<string> CursorDestinations(CursorSlot slot, string contentRoot) =>
        CursorImageService.Destinations(slot)
            .Select(relative => Path.Combine(contentRoot, relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Immediately restores just this slot's destination files, without touching anything else Lingstrap manages.</summary>
    public static void RestoreCursorSlotNow(CursorSlot slot, string versionFolder)
    {
        var contentRoot = Path.Combine(versionFolder, "content");
        foreach (var dest in CursorDestinations(slot, contentRoot))
        {
            try
            {
                var backup = dest + BackupSuffix;
                if (File.Exists(backup))
                {
                    File.Copy(backup, dest, overwrite: true);
                    File.Delete(backup);
                }
                else
                {
                    // Unlike a generic mod file, a cursor destination is always one of Roblox's own
                    // pre-existing native assets - it was never "created" by Lingstrap from nothing,
                    // so there is no safe case where deleting it is a correct restore. If the backup
                    // is missing (e.g. "Reinitialize sizing" cleared it to force a fresh geometry
                    // read), leave whatever is currently there rather than erasing a file Roblox
                    // needs - that previously deleted MouseLockedCursor.png outright.
                    Log.Warn($"No backup found for {dest} - leaving it as-is rather than deleting a file Roblox needs.");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not restore {dest}: {ex.Message}");
            }
        }
    }

    /// <summary>Puts back every file Lingstrap has overwritten in this version folder.</summary>
    public static void RestoreAll(string versionFolder)
    {
        // A cursor destination is always one of Roblox's own pre-existing native assets, never a
        // file Lingstrap created from nothing - unlike a generic mod file, "no backup" for one of
        // these is never a safe reason to delete it. It happens routinely too: clearing a cursor
        // slot (ModsService.RestoreCursorSlotNow) restores the file and deletes its backup right
        // then, but doesn't touch this manifest - so the very next launch would otherwise find that
        // now-backup-less entry still listed here and delete the file Roblox needs, reproducing the
        // exact same data-loss bug RestoreCursorSlotNow itself was already fixed for, just through
        // this separate path.
        var contentRoot = Path.Combine(versionFolder, "content");
        var cursorDestinations = new HashSet<string>(
            Enum.GetValues<CursorSlot>().SelectMany(slot => CursorImageService.Destinations(slot))
                .Select(relative => Path.Combine(contentRoot, relative.Replace('/', Path.DirectorySeparatorChar))),
            StringComparer.OrdinalIgnoreCase);

        foreach (var dest in LoadManifest(versionFolder))
        {
            try
            {
                var backup = dest + BackupSuffix;
                if (File.Exists(backup))
                {
                    File.Copy(backup, dest, overwrite: true);
                    File.Delete(backup);
                }
                else if (cursorDestinations.Contains(dest))
                {
                    Log.Warn($"No backup found for cursor destination {dest} - leaving it as-is rather than deleting a file Roblox needs.");
                }
                else if (File.Exists(dest))
                {
                    File.Delete(dest); // a generic mod file Lingstrap created; it didn't exist before
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not restore {dest}: {ex.Message}");
            }
        }

        var manifestPath = ManifestPath(versionFolder);
        if (File.Exists(manifestPath))
            File.Delete(manifestPath);
    }

    private static void CopyWithBackup(string source, string dest, List<string> applied)
    {
        try
        {
            var backup = dest + BackupSuffix;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            if (File.Exists(dest) && !File.Exists(backup))
                File.Copy(dest, backup);

            File.Copy(source, dest, overwrite: true);
            applied.Add(dest);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not apply mod file {dest}: {ex.Message}");
        }
    }

    /// <summary>CopyWithBackup for content Lingstrap generates rather than copies - same backup and
    /// manifest handling, so it restores identically.</summary>
    private static void WriteWithBackup(byte[] contents, string dest, List<string> applied)
    {
        try
        {
            var backup = dest + BackupSuffix;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            if (File.Exists(dest) && !File.Exists(backup))
                File.Copy(dest, backup);

            File.WriteAllBytes(dest, contents);
            applied.Add(dest);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write generated file {dest}: {ex.Message}");
        }
    }

    private static string ManifestPath(string versionFolder) =>
        Path.Combine(versionFolder, "Lingstrap.ModManifest.json");

    private static void SaveManifest(string versionFolder, List<string> files)
    {
        try
        {
            File.WriteAllText(ManifestPath(versionFolder), JsonSerializer.Serialize(files));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save mod manifest: {ex.Message}");
        }
    }

    private static List<string> LoadManifest(string versionFolder)
    {
        var path = ManifestPath(versionFolder);
        if (!File.Exists(path)) return new List<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}
