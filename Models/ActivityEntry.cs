using System;
using System.Linq;

namespace Lingstrap.Models;

/// <summary>One detected game join, pulled from the Roblox log plus whatever lookups succeeded.</summary>
public class ActivityEntry
{
    public DateTime Timestamp { get; set; }

    public string PlaceId { get; set; } = "";

    public string JobId { get; set; } = "";
    public string ServerIp { get; set; } = "";
    public string? ServerPort { get; set; }

    /// <summary>Which Roblox client log file this join came from - lets the overlay banner target that specific client's window when more than one is running (multi-instance).</summary>
    public string? SourceLogFile { get; set; }

    public string? City { get; set; }
    public string? Region { get; set; }
    public string? Country { get; set; }

    /// <summary>Set once, when the join was detected. Session length is computed from this, live - Roblox exposes no server uptime.</summary>
    public DateTime SessionStart { get; set; }

    /// <summary>"City, Region, Country" from whatever geo fields resolved - null if none did.</summary>
    public string? BuildLocationText()
    {
        var joined = string.Join(", ", new[] { City, Region, Country }.Where(s => !string.IsNullOrEmpty(s)));
        return joined.Length > 0 ? joined : null;
    }
}
