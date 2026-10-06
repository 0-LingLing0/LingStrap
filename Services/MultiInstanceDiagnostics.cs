using System.Collections.Generic;
using System.Linq;

namespace Lingstrap.Services;

/// <summary>What's standing in the way of multi-instance in this session, and fixing it.</summary>
public static class MultiInstanceDiagnostics
{
    public record Report(
        bool SettingOn,
        bool Armed,
        int RunningClients,
        List<int> Blocking,
        List<StuckAccountService.StuckProcess> Stuck)
    {
        public bool AllGood => SettingOn && Blocking.Count == 0 && Stuck.Count == 0;
    }

    public static Report Check()
    {
        var stuck = StuckAccountService.Find()
            .Where(s => s.Problem != StuckAccountService.Problem.CrashHandler)
            .ToList();

        return new Report(
            SettingsService.Current.MultiInstance,
            MultiInstanceWatcher.IsArmed(),
            RobloxProcesses.Count(),
            SingletonEventService.FindHolders(),
            stuck);
    }

    /// <summary>
    /// Releases the single-window signal inside blocking clients (they keep running), closes stuck
    /// ones, and arms multi-instance for the next launch. Returns a line per thing done.
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
                ? $"Released the single-window signal in {blocking.Count} account{(blocking.Count == 1 ? "" : "s")} - they stay open."
                : "Couldn't release the single-window signal - close those accounts instead.");
        }

        if (report.SettingOn && !MultiInstanceWatcher.IsArmed())
        {
            MultiInstanceWatcher.EnsureWatcherRunning();
            done.Add(MultiInstanceWatcher.IsArmed()
                ? "Multi-instance is ready for the next account."
                : "Multi-instance couldn't be readied - see the log.");
        }

        return done;
    }
}
