namespace Lingstrap.Models;

/// <summary>One row in the FastFlags editor.</summary>
public class FastFlagEntry
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
