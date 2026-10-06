using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Lingstrap.Services;

namespace Lingstrap.Views;

/// <summary>The "Close stuck accounts" and "Check multi-instance" buttons - on Home and on Farming.</summary>
public static class FarmingActions
{
    public static async Task CloseStuckAsync(FrameworkElement owner)
    {
        var stuck = await Task.Run(StuckAccountService.Find);
        if (stuck.Count == 0)
        {
            await DialogHelper.ShowErrorAsync(owner, "Every Roblox account is responding normally - nothing to close.", "No stuck accounts");
            return;
        }

        var frozen = stuck.Count(s => s.Problem == StuckAccountService.Problem.NotResponding);
        var ghosts = stuck.Count(s => s.Problem == StuckAccountService.Problem.NoWindow);
        var handlers = stuck.Count(s => s.Problem == StuckAccountService.Problem.CrashHandler);
        var parts = new List<string>();
        if (frozen > 0) parts.Add($"{frozen} not responding");
        if (ghosts > 0) parts.Add($"{ghosts} running without a window (left over, can block multi-instance)");
        if (handlers > 0) parts.Add($"{handlers} crash handler{(handlers == 1 ? "" : "s")}");

        var confirmed = await DialogHelper.ShowConfirmAsync(owner,
            "Found: " + string.Join(", ", parts) + ".\n\nClose them? Accounts that are working normally stay open.",
            "Close stuck accounts?", confirmText: "Close them");
        if (!confirmed) return;

        var closed = await Task.Run(() => StuckAccountService.Close(stuck));
        await DialogHelper.ShowErrorAsync(owner, $"Closed {closed} of {stuck.Count}.", "Done");
    }

    public static async Task CheckMultiInstanceAsync(FrameworkElement owner)
    {
        var report = await Task.Run(MultiInstanceDiagnostics.Check);

        var lines = new List<string>();
        if (!report.SettingOn)
            lines.Add("• \"Allow multi-instance launching\" is off - turn it on first.");
        foreach (var pid in report.Blocking)
            lines.Add($"• PID {pid} is BLOCKING: it holds Roblox's close signal (opened before multi-instance was ready), so every new account makes the others close.");
        foreach (var pid in report.Uninspectable)
            lines.Add($"• Account PID {pid} couldn't be checked - Windows wouldn't let Lingstrap look inside it. It may be the one blocking.");
        foreach (var s in report.Stuck)
            lines.Add(s.Problem == StuckAccountService.Problem.NoWindow
                ? $"• PID {s.Pid} is running without a window - a leftover from a closed account."
                : $"• PID {s.Pid} is not responding.");

        if (report.AllGood)
        {
            await DialogHelper.ShowErrorAsync(owner,
                $"Nothing is blocking multi-instance. {report.RunningClients} Roblox account{(report.RunningClients == 1 ? "" : "s")} running" +
                (report.Armed ? ", and it's ready for the next one." : "; it gets ready again with the next launch."),
                "Multi-instance is fine");
            return;
        }

        var fix = await DialogHelper.ShowConfirmAsync(owner,
            string.Join("\n", lines) + "\n\nFix it? Blocking accounts stay open - only Roblox's single-window signal inside them is released. Accounts without a window or not responding are closed.",
            "Multi-instance problems found", confirmText: "Fix it");
        if (!fix) return;

        var done = await Task.Run(() => MultiInstanceDiagnostics.Fix(report));
        await DialogHelper.ShowErrorAsync(owner, done.Count > 0 ? string.Join("\n", done) : "Nothing needed doing.", "Done");
    }
}
