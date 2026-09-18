using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Xml.XPath;
using Lingstrap.Models;

namespace Lingstrap.Services;

/// <summary>
/// Generic read/write access to Roblox's own GlobalBasicSettings_13.xml. Every property under
/// //Item[@class='UserGameSettings']/Properties is parsed by its XML element type - nothing is
/// hardcoded. Writes never happen while a Roblox client is running (Roblox rewrites this file on
/// exit); edits made while one is running are queued and flushed at the next safe opportunity.
/// </summary>
public static class GlobalBasicSettingsService
{
    private const string PropertiesXPath = "//Item[@class='UserGameSettings']/Properties";
    private const string BackupSuffix = ".lingstrap-backup";

    public static string FilePath { get; set; } =
        Path.Combine(Paths.RobloxRoot, "GlobalBasicSettings_13.xml");

    private record PendingEdit(string Tag, string? Value, float? X, float? Y, bool Remove);
    private static readonly Dictionary<string, PendingEdit> Pending = new();

    public static bool IsRobloxRunning() => Process.GetProcessesByName("RobloxPlayerBeta").Length > 0;
    public static bool BackupExists() => File.Exists(FilePath + BackupSuffix);
    public static bool HasPendingEdits => Pending.Count > 0;

    /// <summary>Re-reads one field's current live value - e.g. right after ResetField, to refresh a row in place without a full reload. Null if the field or file no longer exists.</summary>
    public static GbsField? GetField(string name)
    {
        if (!File.Exists(FilePath)) return null;

        try
        {
            var doc = XDocument.Load(FilePath);
            var props = doc.XPathSelectElement(PropertiesXPath);
            var el = props?.Elements().FirstOrDefault(e => (string?)e.Attribute("name") == name);
            var field = el is null ? null : ParseField(el, name);
            if (field != null && Pending.TryGetValue(name, out var pending) && !pending.Remove)
            {
                if (pending.X.HasValue) { field.VectorX = pending.X; field.VectorY = pending.Y; }
                else field.RawValue = pending.Value;
            }
            return field;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not re-read field {name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Parses every property in the file. Returns false with a reason if the file is missing or malformed.</summary>
    public static bool TryLoad(out List<GbsField> fields, out string? error)
    {
        fields = new List<GbsField>();
        error = null;

        if (!File.Exists(FilePath))
        {
            error = $"File not found: {FilePath}";
            return false;
        }

        try
        {
            var doc = XDocument.Load(FilePath);
            var props = doc.XPathSelectElement(PropertiesXPath);
            if (props is null)
            {
                error = "The file doesn't contain a UserGameSettings/Properties section.";
                return false;
            }

            foreach (var el in props.Elements())
            {
                var name = (string?)el.Attribute("name");
                if (string.IsNullOrEmpty(name)) continue;
                fields.Add(ParseField(el, name));
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Overlays any edits queued while Roblox was running onto freshly-parsed fields, so the UI shows what will actually apply.</summary>
    public static void ApplyPendingOverlay(List<GbsField> fields)
    {
        foreach (var f in fields)
        {
            if (!Pending.TryGetValue(f.Name, out var p) || p.Remove) continue;
            if (p.X.HasValue) { f.VectorX = p.X; f.VectorY = p.Y; }
            else f.RawValue = p.Value;
        }
    }

    public static void WriteScalar(string name, string tag, string value)
    {
        if (IsRobloxRunning())
        {
            Log.Info($"GBS field {name} queued (Roblox is currently running) - will apply on the next launch once it's closed.");
            Pending[name] = new PendingEdit(tag, value, null, null, false);
            return;
        }
        Pending.Remove(name);
        WriteScalarNow(name, tag, value);
    }

    public static void WriteVector2(string name, string tag, float x, float y)
    {
        if (IsRobloxRunning())
        {
            Log.Info($"GBS field {name} queued (Roblox is currently running) - will apply on the next launch once it's closed.");
            Pending[name] = new PendingEdit(tag, null, x, y, false);
            return;
        }
        Pending.Remove(name);
        WriteVector2Now(name, tag, x, y);
    }

    public static void RemoveField(string name)
    {
        if (IsRobloxRunning())
        {
            Log.Info($"GBS field {name} removal queued (Roblox is currently running) - will apply on the next launch once it's closed.");
            Pending[name] = new PendingEdit("", null, null, null, true);
            return;
        }
        Pending.Remove(name);
        RemoveFieldsNow(new[] { name });
    }

    public static void RemoveFields(IEnumerable<string> names)
    {
        foreach (var name in names) RemoveField(name);
    }

    /// <summary>Applies any edits queued while Roblox was running. Safe to call any time; no-ops if still running.</summary>
    public static void FlushPending()
    {
        if (Pending.Count == 0) return;

        if (IsRobloxRunning())
        {
            Log.Info($"{Pending.Count} pending GBS edit(s) not applied yet - a RobloxPlayerBeta process is still running. " +
                     "If this keeps appearing on every launch even right after Roblox visibly closed, a stale/hung " +
                     "RobloxPlayerBeta.exe is likely still alive in the background - check Task Manager.");
            return;
        }

        foreach (var (name, edit) in Pending.ToList())
        {
            if (edit.Remove) RemoveFieldsNow(new[] { name });
            else if (edit.X.HasValue) WriteVector2Now(name, edit.Tag, edit.X.Value, edit.Y ?? 0);
            else WriteScalarNow(name, edit.Tag, edit.Value!);
        }
        Pending.Clear();
    }

    /// <summary>Resets one field to whatever value the backup has (or removes it if the backup doesn't have it either).</summary>
    public static void ResetField(string name)
    {
        var backupPath = FilePath + BackupSuffix;
        if (!File.Exists(backupPath))
        {
            Log.Warn("No backup exists yet - nothing to reset from.");
            return;
        }

        try
        {
            var doc = XDocument.Load(backupPath);
            var props = doc.XPathSelectElement(PropertiesXPath);
            var el = props?.Elements().FirstOrDefault(e => (string?)e.Attribute("name") == name);

            if (el is null)
            {
                RemoveField(name);
                return;
            }

            var field = ParseField(el, name);
            if (field.Type == GbsFieldType.Vector2)
                WriteVector2(name, field.Tag, field.VectorX ?? 0, field.VectorY ?? 0);
            else if (field.Type != GbsFieldType.Other)
                WriteScalar(name, field.Tag, field.RawValue ?? "");
        }
        catch (Exception ex)
        {
            Log.Error($"Could not reset field {name} from backup", ex);
        }
    }

    /// <summary>Restores the whole file from the backup, discarding every change since it was made.</summary>
    public static void RestoreOriginal()
    {
        var backupPath = FilePath + BackupSuffix;
        if (!File.Exists(backupPath))
        {
            Log.Warn("No backup exists yet - nothing to restore.");
            return;
        }

        try
        {
            // File.Copy stamps the backup's (unlocked) attributes onto the destination, and throws
            // outright onto a read-only one - WithWriteAccess covers both, re-locking afterwards if
            // the file was locked before the restore.
            WithWriteAccess(() => File.Copy(backupPath, FilePath, overwrite: true));
            Pending.Clear();
            Log.Info("Restored GlobalBasicSettings_13.xml from backup.");
        }
        catch (Exception ex)
        {
            Log.Error("Could not restore GlobalBasicSettings_13.xml", ex);
        }
    }

    private static GbsField ParseField(XElement el, string name)
    {
        var tag = el.Name.LocalName;
        return tag switch
        {
            "bool" or "int" or "float" or "token" or "string" => new GbsField
            {
                Name = name,
                Tag = tag,
                Type = tag switch
                {
                    "bool" => GbsFieldType.Bool,
                    "int" => GbsFieldType.Int,
                    "float" => GbsFieldType.Float,
                    "token" => GbsFieldType.Token,
                    _ => GbsFieldType.String,
                },
                RawValue = el.Value,
            },
            "Vector2" => new GbsField
            {
                Name = name,
                Tag = tag,
                Type = GbsFieldType.Vector2,
                VectorX = ParseFloatOrNull(el.Element("X")?.Value),
                VectorY = ParseFloatOrNull(el.Element("Y")?.Value),
            },
            _ => new GbsField
            {
                Name = name,
                Tag = tag,
                Type = GbsFieldType.Other,
                RawXml = el.ToString(),
            },
        };
    }

    private static float? ParseFloatOrNull(string? s) =>
        s != null && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static void WriteScalarNow(string name, string tag, string value)
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                Log.Warn($"Cannot write {name} - GlobalBasicSettings_13.xml does not exist.");
                return;
            }

            BackupOnce();
            var doc = XDocument.Load(FilePath);
            var props = doc.XPathSelectElement(PropertiesXPath);
            if (props is null) return;

            var existing = props.Elements().FirstOrDefault(e => (string?)e.Attribute("name") == name);
            if (existing != null) existing.Value = value;
            else props.Add(new XElement(tag, new XAttribute("name", name), value));

            SaveDocument(doc);
            Log.Info($"Wrote GBS field {name} = {value}");
        }
        catch (Exception ex)
        {
            Log.Error($"Could not write GBS field {name}", ex);
        }
    }

    private static void WriteVector2Now(string name, string tag, float x, float y)
    {
        try
        {
            if (!File.Exists(FilePath)) return;

            BackupOnce();
            var doc = XDocument.Load(FilePath);
            var props = doc.XPathSelectElement(PropertiesXPath);
            if (props is null) return;

            var xs = x.ToString(CultureInfo.InvariantCulture);
            var ys = y.ToString(CultureInfo.InvariantCulture);

            var existing = props.Elements().FirstOrDefault(e => (string?)e.Attribute("name") == name);
            if (existing != null)
            {
                existing.SetElementValue("X", xs);
                existing.SetElementValue("Y", ys);
            }
            else
            {
                props.Add(new XElement(tag, new XAttribute("name", name),
                    new XElement("X", xs), new XElement("Y", ys)));
            }

            SaveDocument(doc);
            Log.Info($"Wrote GBS field {name} = ({xs}, {ys})");
        }
        catch (Exception ex)
        {
            Log.Error($"Could not write GBS field {name}", ex);
        }
    }

    private static void RemoveFieldsNow(IEnumerable<string> names)
    {
        if (!File.Exists(FilePath)) return;

        try
        {
            BackupOnce();
            var doc = XDocument.Load(FilePath);
            var props = doc.XPathSelectElement(PropertiesXPath);
            if (props is null) return;

            var set = new HashSet<string>(names);
            props.Elements().Where(e => (string?)e.Attribute("name") is { } n && set.Contains(n)).Remove();

            SaveDocument(doc);
        }
        catch (Exception ex)
        {
            Log.Error("Could not remove GlobalBasicSettings properties", ex);
        }
    }

    private static void BackupOnce()
    {
        var backupPath = FilePath + BackupSuffix;
        if (!File.Exists(backupPath) && File.Exists(FilePath))
        {
            File.Copy(FilePath, backupPath);
            // File.Copy carries the source's attributes across, so backing up a LOCKED file used to
            // produce a read-only backup too - one that Restore could still read, but nothing could
            // ever replace or delete afterwards. The backup is Lingstrap's, not a setting to protect.
            SetReadOnly(backupPath, false);
        }
    }

    // --- Locking ------------------------------------------------------------------------------
    //
    // Roblox rewrites this whole file from its in-memory settings every time it exits. That's why an
    // FPS cap set here "doesn't stick": change it, launch, touch any setting in Roblox's own menu (or
    // just have Roblox's defaults differ), quit - and Roblox's copy overwrites Lingstrap's. Marking the
    // file read-only is the standard way round it: Roblox still reads it on launch, it just can't save
    // over it. The cost is deliberate and the page says so - nothing changed in Roblox's own menu
    // persists while it's locked.

    /// <summary>Whether the settings file is currently read-only on disk - the real state, not the
    /// setting, since a lock can also be applied or removed outside Lingstrap.</summary>
    public static bool IsLocked() =>
        File.Exists(FilePath) && (File.GetAttributes(FilePath) & FileAttributes.ReadOnly) != 0;

    /// <summary>Locks or unlocks the file. Returns false (and logs why) if it couldn't be done.</summary>
    public static bool ApplyLock(bool locked)
    {
        if (!File.Exists(FilePath))
        {
            // Nothing to lock yet - Roblox creates this file on its first run. The launch path calls
            // this again every time, so it takes effect as soon as the file exists.
            if (locked) Log.Info("Lock requested for GlobalBasicSettings_13.xml, but it doesn't exist yet - will apply once Roblox creates it.");
            return true; // deferred, not failed: the setting is saved and the next launch applies it
        }

        try
        {
            SetReadOnly(FilePath, locked);
            Log.Info(locked
                ? "Locked GlobalBasicSettings_13.xml (read-only) - Roblox can no longer overwrite these settings."
                : "Unlocked GlobalBasicSettings_13.xml - Roblox can save its settings again.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not {(locked ? "lock" : "unlock")} GlobalBasicSettings_13.xml", ex);
            return false;
        }
    }

    private static void SetReadOnly(string path, bool readOnly)
    {
        var attributes = File.GetAttributes(path);
        var updated = readOnly ? attributes | FileAttributes.ReadOnly : attributes & ~FileAttributes.ReadOnly;
        if (updated != attributes) File.SetAttributes(path, updated);
    }

    /// <summary>
    /// Every write Lingstrap makes to the settings file goes through here. A lock is meant to keep
    /// ROBLOX out, not Lingstrap - so a change made on this page still lands even while the file is
    /// read-only: the flag comes off for exactly the duration of the write and goes straight back on,
    /// in a finally so a failed write can never leave a locked file quietly unlocked.
    /// </summary>
    private static void WithWriteAccess(Action write)
    {
        var wasLocked = IsLocked();
        if (wasLocked) SetReadOnly(FilePath, false);
        try
        {
            write();
        }
        finally
        {
            if (wasLocked && File.Exists(FilePath)) SetReadOnly(FilePath, true);
        }
    }

    private static void SaveDocument(XDocument doc) => WithWriteAccess(() => doc.Save(FilePath));
}
