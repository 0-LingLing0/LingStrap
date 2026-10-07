using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Lingstrap.Services;

namespace Lingstrap.Views;

/// <summary>The "Close stuck accounts" button on the Farming page.</summary>
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
}
