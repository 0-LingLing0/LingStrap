using System.Diagnostics;
using System.Linq;

namespace Lingstrap.Services;

/// <summary>
/// Roblox processes in THIS Windows session only. On a machine with several RDP logins, every
/// session runs its own Roblox clients, and Roblox's own one-window lock is per session too - but
/// Process.GetProcessesByName lists every session's. Counting those made one RDP's Lingstrap think
/// Roblox was running when only another login's was, and reach into clients that aren't its own:
/// multi-instance, the client limits, closing crash handlers, deferring settings, all of it.
/// </summary>
public static class RobloxProcesses
{
    public const string ClientName = "RobloxPlayerBeta";

    private static readonly int Session = Process.GetCurrentProcess().SessionId;

    /// <summary>Running Roblox clients in this session. The caller disposes them.</summary>
    public static Process[] Clients() => InSession(ClientName);

    /// <summary>Processes with this name in this session. The caller disposes them.</summary>
    public static Process[] InSession(string name)
    {
        var all = Process.GetProcessesByName(name);
        var mine = all.Where(p => SessionOf(p) == Session).ToArray();
        foreach (var other in all.Except(mine)) other.Dispose();
        return mine;
    }

    /// <summary>How many Roblox clients are running in this session.</summary>
    public static int Count()
    {
        var clients = Clients();
        foreach (var c in clients) c.Dispose();
        return clients.Length;
    }

    private static int SessionOf(Process p)
    {
        try { return p.SessionId; }
        catch { return -1; } // exited mid-way - not ours to count
    }
}
