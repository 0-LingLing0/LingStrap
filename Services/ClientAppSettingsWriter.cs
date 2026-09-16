using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Lingstrap.Services;

/// <summary>
/// Writes &lt;version&gt;/ClientSettings/ClientAppSettings.json - the file Roblox reads its
/// FastFlag overrides from. Values are typed (bool/number/string) because Roblox ignores a
/// flag whose JSON type doesn't match what it expects.
/// </summary>
public static class ClientAppSettingsWriter
{
    public static void Write(string versionFolder, IReadOnlyDictionary<string, string> flags)
    {
        var dir = Path.Combine(versionFolder, "ClientSettings");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "ClientAppSettings.json");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in flags)
            {
                if (IsStringFlag(name))
                    writer.WriteString(name, value);
                else if (bool.TryParse(value, out var b))
                    writer.WriteBoolean(name, b);
                else if (long.TryParse(value, out var i))
                    writer.WriteNumber(name, i);
                else
                    writer.WriteString(name, value);
            }
            writer.WriteEndObject();
        }

        File.WriteAllBytes(file, stream.ToArray());
        Log.Info($"Wrote {flags.Count} FastFlags to {file}");
    }

    /// <summary>
    /// Roblox's own naming convention marks a flag's real type in its name - a String/Log flag (e.g.
    /// FStringSomething) should always be written as a JSON string, even if its value happens to look
    /// like a number or "true"/"false" (a hand-added custom flag could easily have such a value).
    /// Guessing purely from the value's shape, as every other flag kind here still does, would
    /// otherwise write it as the wrong JSON type - which Roblox then just silently ignores.
    /// </summary>
    private static bool IsStringFlag(string name) =>
        name.StartsWith("DFString", StringComparison.Ordinal) ||
        name.StartsWith("SFString", StringComparison.Ordinal) ||
        name.StartsWith("FString", StringComparison.Ordinal) ||
        name.StartsWith("DFLog", StringComparison.Ordinal) ||
        name.StartsWith("FLog", StringComparison.Ordinal);
}
