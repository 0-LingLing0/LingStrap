using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Lingstrap.Models;

namespace Lingstrap.Services;

/// <summary>
/// Reading and writing FastFlag files on disk. Kept apart from FastFlagStore, which owns the live
/// list in settings: this half is pure format handling with no state behind it, which is also what
/// makes it straightforward to exercise directly.
/// </summary>
public static class FastFlagFile
{    /// <summary>
    /// Parses a flag file in either shape people actually have.
    ///
    /// The universal one - what Roblox's own ClientAppSettings.json uses, what every other
    /// bootstrapper imports and exports, and what any flag list copied off a forum or shared by a
    /// friend looks like - is a flat object of name to value:
    ///     { "DFIntTaskSchedulerTargetFps": 9999, "FFlagDebugGraphicsPreferVulkan": true }
    /// Values there may be strings, bools or numbers; Lingstrap keeps them all as text and
    /// ClientAppSettingsWriter re-types them on the way out.
    ///
    /// The other is Lingstrap's own older export, an array of its internal entries, which carries the
    /// per-flag enabled state the flat form has no room for. Imports used to accept only that one, so
    /// importing anything from anywhere else failed outright ("The JSON value could not be converted
    /// to FastFlagEntry[]") - which is every realistic way of getting a flag list in the first place.
    ///
    /// Returns null with a reason when the file isn't usable at all.
    /// </summary>
    public static List<FastFlagEntry>? TryParse(string json, out string? error)
    {
        error = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            error = $"That file isn't valid JSON: {ex.Message}";
            return null;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                var entries = new List<FastFlagEntry>();
                foreach (var property in root.EnumerateObject())
                {
                    if (string.IsNullOrWhiteSpace(property.Name)) continue;
                    entries.Add(new FastFlagEntry
                    {
                        Name = property.Name,
                        Value = ValueToText(property.Value),
                        Enabled = true,
                    });
                }
                return entries;
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                try
                {
                    var entries = JsonSerializer.Deserialize<List<FastFlagEntry>>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (entries != null)
                        return entries.Where(e => !string.IsNullOrWhiteSpace(e.Name)).ToList();
                }
                catch (JsonException)
                {
                    // Falls through to the shared message below - an array of something that isn't
                    // a flag entry is no more usable than any other unexpected shape.
                }

                error = "That file is a JSON list, but not of FastFlags.";
                return null;
            }

            error = "A FastFlag file should be a list of \"name\": value pairs.";
            return null;
        }
    }

    /// <summary>
    /// The enabled flags as the universal flat object, indented - directly usable as
    /// ClientAppSettings.json and importable by any other bootstrapper. Disabled entries are left out
    /// rather than exported in some off state the format can't express: "disabled" means Lingstrap
    /// doesn't write that flag at all, so it isn't part of what this file describes.
    /// </summary>
    public static string Export(IEnumerable<FastFlagEntry> flags)
    {
        var map = new Dictionary<string, object?>();
        foreach (var flag in flags)
        {
            if (!flag.Enabled || string.IsNullOrWhiteSpace(flag.Name)) continue;

            // Written back as the type the value actually reads as, matching both Roblox's own file
            // and what other tools expect - a bare "true" or 9999 rather than quoted text.
            if (bool.TryParse(flag.Value, out var b)) map[flag.Name] = b;
            else if (long.TryParse(flag.Value, out var i)) map[flag.Name] = i;
            else map[flag.Name] = flag.Value;
        }

        return JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ValueToText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.Null => "",
        _ => value.GetRawText(),
    };
}
