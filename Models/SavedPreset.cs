using System.Collections.Generic;

namespace Lingstrap.Models;

/// <summary>
/// A user-saved full snapshot of FastFlags, graphics settings and process priority - the same shape
/// PresetCatalog's built-in specs use, just captured from the current live config instead of
/// hand-authored, and given a name the user picked. See PresetService.SaveCurrentAsPreset/ApplySaved.
/// </summary>
public class SavedPreset
{
    public string Id { get; set; } = System.Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public Dictionary<string, string> Flags { get; set; } = new();
    public Dictionary<string, GbsFieldValue> GbsFields { get; set; } = new();
    public string Priority { get; set; } = "Normal";
}
