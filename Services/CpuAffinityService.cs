using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace Lingstrap.Services;

/// <summary>
/// Spreads running RobloxPlayerBeta.exe instances evenly across physical cores (both SMT threads of
/// a core always move together, never split between two clients) and clears affinity entirely for a
/// single client. With more clients than cores, cores end up shared as evenly as the numbers allow
/// instead of refusing to pin. Re-evaluates on a short poll so it reacts promptly to clients starting
/// or exiting.
/// </summary>
public static class CpuAffinityService
{
    private static Timer? _timer;
    private static readonly object Lock = new();

    public static event Action? AssignmentsChanged;

    /// <summary>PID -> human-readable description of what it's pinned to. Read by the Activity page.</summary>
    public static Dictionary<int, string> CurrentAssignments { get; } = new();

    public static void Start()
    {
        if (_timer != null) return;
        _timer = new Timer(_ => Reevaluate(), null, 0, 2000);
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private static void Reevaluate()
    {
        if (!SettingsService.Current.PinClientsToCores) return;

        lock (Lock)
        {
            try
            {
                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName("RobloxPlayerBeta")
                        .OrderBy(p => SafeStartTime(p))
                        .ToArray();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not enumerate Roblox processes: {ex.Message}");
                    return;
                }

                CurrentAssignments.Clear();

                if (processes.Length <= 1)
                {
                    ClearAll(processes, "Only client running");
                    AssignmentsChanged?.Invoke();
                    return;
                }

                var cores = CpuTopologyService.GetPhysicalCores();
                if (cores is null || cores.Count == 0)
                {
                    Log.Warn("Could not query CPU topology - not pinning.");
                    ClearAll(processes, "Topology unavailable");
                    AssignmentsChanged?.Invoke();
                    return;
                }

                var ordered = OrderByCcdPreference(cores.OrderBy(c => LowestBit(c.Mask)).ToList());
                var n = processes.Length;
                var groups = DistributeCores(ordered, n);

                for (var i = 0; i < n; i++)
                {
                    var group = groups[i];
                    var mask = group.Aggregate(0UL, (acc, c) => acc | c.Mask);

                    try
                    {
                        processes[i].ProcessorAffinity = unchecked((IntPtr)(long)mask);
                        var label = $"Cores {string.Join(",", group.Select(c => LowestBit(c.Mask)).OrderBy(x => x))}";
                        CurrentAssignments[processes[i].Id] = label;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Could not set affinity for PID {processes[i].Id}: {ex.Message}");
                    }
                }

                AssignmentsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("CPU affinity re-evaluation failed", ex);
            }
        }
    }

    /// <summary>
    /// Splits physical cores across clients (or, with more clients than cores, splits clients across
    /// cores) as evenly as possible: e.g. 6 physical cores and 2 clients gives each client 3 physical
    /// cores (6 logical cores apiece); 3 clients gives each 2 physical cores (4 logical apiece); 13
    /// clients on 6 physical cores gives one core 3 clients to share and the rest 2 each - always
    /// something, never a refusal to pin. Round-robin (core index % client count, or vice versa)
    /// rather than a contiguous split so an uneven remainder spreads out instead of landing entirely
    /// on one client, and so a hybrid P/E chip's fast and slow cores get mixed fairly between clients
    /// instead of one client getting all of one kind.
    /// </summary>
    private static List<List<CpuTopologyService.PhysicalCore>> DistributeCores(
        List<CpuTopologyService.PhysicalCore> cores, int clientCount)
    {
        var groups = new List<List<CpuTopologyService.PhysicalCore>>();

        if (clientCount <= cores.Count)
        {
            for (var i = 0; i < clientCount; i++)
                groups.Add(cores.Where((_, idx) => idx % clientCount == i).ToList());
        }
        else
        {
            for (var i = 0; i < clientCount; i++)
                groups.Add(new List<CpuTopologyService.PhysicalCore> { cores[i % cores.Count] });
        }

        return groups;
    }

    /// <summary>
    /// Puts V-Cache CCD cores first when exactly two L3 domains exist with meaningfully different
    /// sizes (the real signature of an AMD X3D chip) - so the first client (by launch order) lands
    /// there. Falls back to the input order untouched if that can't be determined reliably.
    /// </summary>
    private static List<CpuTopologyService.PhysicalCore> OrderByCcdPreference(List<CpuTopologyService.PhysicalCore> available)
    {
        var domains = CpuTopologyService.GetL3CacheDomains();
        if (domains is null || domains.Count != 2) return available;
        if (domains[0].SizeBytes == domains[1].SizeBytes) return available; // no size disparity - not X3D, or can't tell

        var bigger = domains[0].SizeBytes > domains[1].SizeBytes ? domains[0] : domains[1];

        var vcache = available.Where(c => (c.Mask & bigger.Mask) != 0).ToList();
        var other = available.Where(c => (c.Mask & bigger.Mask) == 0).ToList();
        return vcache.Concat(other).ToList();
    }

    private static void ClearAll(IEnumerable<Process> processes, string reason)
    {
        // Not -1 (all 64 bits): SetProcessAffinityMask requires the mask to be a subset of the
        // system's actual affinity mask, and a real machine essentially never has exactly 64 logical
        // processors - on this one's 12, requesting bits 12-63 too gets the whole call rejected with
        // "The parameter is incorrect", which silently failed to clear affinity at all every time this
        // ran (i.e. constantly, on every 2-second re-evaluation while a single client is running).
        var mask = Environment.ProcessorCount is > 0 and < 64
            ? (1UL << Environment.ProcessorCount) - 1
            : ulong.MaxValue;

        foreach (var p in processes)
        {
            try { p.ProcessorAffinity = unchecked((IntPtr)(long)mask); }
            catch (Exception ex) { Log.Warn($"Could not clear affinity for PID {p.Id}: {ex.Message}"); }

            CurrentAssignments[p.Id] = "All cores";
        }

        if (!string.IsNullOrEmpty(reason))
            Log.Info($"CPU affinity: {reason} - all clients set to all cores.");
    }

    private static DateTime SafeStartTime(Process p)
    {
        try { return p.StartTime; }
        catch { return DateTime.MaxValue; }
    }

    private static int LowestBit(ulong mask) => BitOperations.TrailingZeroCount(mask);
}
