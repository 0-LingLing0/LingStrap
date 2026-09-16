namespace Lingstrap.Models;

/// <summary>One program Lingstrap starts alongside Roblox and (unless DontAutoClose) closes with it.</summary>
public class CompanionApp
{
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Arguments { get; set; } = "";
    public int StartupDelaySeconds { get; set; } = 0;
    public bool RunAsAdmin { get; set; } = false;
    public bool DontAutoClose { get; set; } = false;
}
