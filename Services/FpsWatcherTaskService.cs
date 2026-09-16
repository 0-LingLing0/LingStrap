using System;
using System.Diagnostics;

namespace Lingstrap.Services;

/// <summary>
/// Lets the FPS overlay's watcher process run every time Roblox launches without a UAC prompt each
/// time. RobloxFpsTracker's real-time ETW session needs administrator rights, but a plain
/// "runas"-elevated launch (the first version of this feature) means Windows asking every single
/// time Roblox starts. Registering ONE Windows Scheduled Task with "run with highest privileges"
/// avoids that: creating the task itself needs one-time admin approval, but running an already-
/// registered elevated task afterwards via "schtasks /run" does not prompt again, as long as the
/// signed-in user is an administrator - Task Scheduler already elevates it silently, since consent
/// was given when the task was registered. This is the standard, intended way apps avoid repeat UAC
/// prompts for a helper that always needs to run elevated, not a security bypass.
/// </summary>
public static class FpsWatcherTaskService
{
    private const string TaskName = "LingstrapFpsWatcher";

    public static bool TaskExists()
    {
        try
        {
            // Deliberately NOT redirecting stdout/stderr - only the exit code matters here, and
            // redirecting a stream without ever reading it is a well-known way to hang: schtasks
            // blocks trying to write once the OS pipe buffer fills, WaitForExit then waits on a
            // process that's stuck waiting on us, and this ran on every single launch whenever the
            // FPS overlay was enabled - exactly the kind of intermittent, launch-slowing hang that
            // showed up as "waiting for Roblox takes a long time" with no error or log line to
            // explain why.
            var process = Process.Start(new ProcessStartInfo("schtasks.exe", $"/query /tn \"{TaskName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process!.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"FPS overlay: could not check for the scheduled task: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Registers the task pointed at this exe's current path with -fpswatcher, run level HIGHEST, and
    /// a one-time trigger dated in the past - it never actually fires on its own (that date has
    /// already passed), it just satisfies schtasks' requirement that a task have some schedule.
    /// "schtasks /run" later ignores the schedule entirely and just executes the task's action
    /// immediately. Meant to be called from a background thread - this blocks waiting for the
    /// elevated schtasks process (and the UAC prompt) to finish.
    /// </summary>
    public static bool TryCreateTask()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            Log.Warn("FPS overlay: could not resolve own exe path - scheduled task not created.");
            return false;
        }

        try
        {
            var arguments = $"/create /tn \"{TaskName}\" /tr \"\\\"{exePath}\\\" -fpswatcher\" " +
                             "/sc ONCE /st 00:00 /sd 01/01/1999 /rl HIGHEST /f";
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
            });
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The user clicked "No" on the UAC prompt - not an error, just declined.
            Log.Info("FPS overlay: administrator elevation was declined while setting up the scheduled task.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"FPS overlay: could not create the scheduled task: {ex.Message}");
            return false;
        }
    }

    /// <summary>Runs the already-registered task - no UAC prompt, since consent was already given
    /// when the task itself was created.</summary>
    public static void RunTask()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/run /tn \"{TaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"FPS overlay: could not run the scheduled task: {ex.Message}");
        }
    }
}
