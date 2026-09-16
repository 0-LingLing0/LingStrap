namespace Lingstrap.Models;

public enum GbsFieldType { Bool, Int, Float, Token, String, Vector2, Other }

/// <summary>One property parsed out of Roblox's GlobalBasicSettings_13.xml.</summary>
public class GbsField
{
    public required string Name { get; init; }
    public required string Tag { get; init; }      // original XML element name: bool, int, float, token, string, Vector2, ...
    public required GbsFieldType Type { get; init; }

    /// <summary>Scalar value as text, for Bool/Int/Float/Token/String. Null for Vector2/Other.</summary>
    public string? RawValue { get; set; }

    public float? VectorX { get; set; }
    public float? VectorY { get; set; }

    /// <summary>Full inner XML, only populated for Other (unknown) types, shown read-only.</summary>
    public string? RawXml { get; set; }
}
