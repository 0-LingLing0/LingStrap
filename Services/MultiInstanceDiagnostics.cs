using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Lingstrap.Services;

/// <summary>What's standing in the way of multi-instance in this session, and fixing it.</summary>
public static class MultiInstanceDiagnostics
{
    public record Report(
        bool SettingOn,
        bool Armed,
        int RunningClients,
        List<int> Blocking,
        List<int> Uninspectable,
        List<StuckAccountService.StuckProcess> Stuck)
    {
        public bool AllGood => SettingOn && Blocking.Count == 0 && Uninspectable.Count == 0 && Stuck.Count == 0;
    }

    public static Report Check()
    {
        var stuck = StuckAccountService.Find()
            .Where(s => s.Problem != StuckAccountService.Problem.CrashHandler)
            .ToList();

        // Only worth scanning when Roblox's real event exists - with Lingstrap's placeholder holding the
        // name, or nothing at all, nothing can be blocking.
        var (holders, uninspectable) = RealEventExists()
            ? SingletonEventService.Scan()
            : (new List<int>(), new List<int>());

        return new Report(
            SettingsService.Current.MultiInstance,
            MultiInstanceWatcher.IsArmed(),
            RobloxProcesses.Count(),
            holders,
            uninspectable,
            stuck);
    }

    private static bool RealEventExists()
    {
        if (!EventWaitHandle.TryOpenExisting(SingletonEventService.EventName, out var existing)) return false;
        existing.Dispose();
        return true;
    }

    /// <summary>
    /// Releases the single-window signal inside blocking processes (they keep running), closes stuck
    /// clients, and arms multi-instance for the next launch. Returns a line per thing done.
    /// </summary>
    public static List<string> Fix(Report report)
    {
        var done = new List<string>();

        if (report.Stuck.Count > 0)
        {
            var closed = StuckAccountService.Close(report.Stuck);
            done.Add($"Closed {closed} stuck Roblox client{(closed == 1 ? "" : "s")}.");
        }

        var blocking = report.Blocking.Where(pid => report.Stuck.All(s => s.Pid != pid)).ToList();
        if (blocking.Count > 0)
        {
            var released = SingletonEventService.Release(blocking);
            done.Add(released > 0
                ? $"Released the single-window signal in {blocking.Count} process{(blocking.Count == 1 ? "" : "es")} - they stay open."
                : "Couldn't release the single-window signal.");
        }

        if (report.SettingOn && !MultiInstanceWatcher.IsArmed())
            MultiInstanceWatcher.EnsureWatcherRunning();

        if (RealEventExists())
        {
            var (stillHolding, unreadable) = SingletonEventService.Scan();
            var culprits = stillHolding.Concat(unreadable).Distinct().ToList();
            done.Add(culprits.Count > 0
                ? $"Still blocked by PID {string.Join(", ", culprits)}, which Lingstrap can't release. Close that account (or every Roblox) and launch again."
                : "Roblox's single-window signal is still there. Close every Roblox and launch again.");
        }
        else if (report.SettingOn)
        {
            done.Add(MultiInstanceWatcher.IsArmed()
                ? "Multi-instance is ready for the next account."
                : "Nothing is blocking any more - multi-instance gets ready with the next launch.");
        }

        return done;
    }
}
