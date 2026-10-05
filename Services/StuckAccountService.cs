using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace Lingstrap.Services;

/// <summary>
/// Finds and closes Roblox clients in this session that are stuck or in the way: windows Windows
/// reports as "Not Responding", and "ghost" clients with no window at all - left behind after an
/// account was closed or crashed, still holding on to things multi-instance needs. Healthy clients
/// are never touched, and neither is anything young enough to still be starting up.
/// </summary>
public static class StuckAccountService
{
    /// <summary>A client starting up has no window for its first ~15 seconds - well inside this.</summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(1);

    private static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr hWnd);
    }

    /// <summary>
    /// Visible top-level windows per process id, read from the window list itself. Not
    /// Process.MainWindowHandle: that needs to open the process, and for some Roblox clients that's
    /// refused - it then reports "no window" for a perfectly healthy account, which this button would
    /// have closed.
    /// </summary>
    private static Dictionary<int, List<IntPtr>> VisibleWindowsByProcess()
    {
        var windows = new Dictionary<int, List<IntPtr>>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (Native.IsWindowVisible(hwnd))
            {
                Native.GetWindowThreadProcessId(hwnd, out var pid);
                if (!windows.TryGetValue((int)pid, out var list)) windows[(int)pid] = list = new List<IntPtr>();
                list.Add(hwnd);
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    public enum Problem { NotResponding, NoWindow, CrashHandler }

    public record StuckProcess(int Pid, string Name, Problem Problem);

    public static List<StuckProcess> Find()
    {
        var found = new List<StuckProcess>();
        var windows = VisibleWindowsByProcess();

        foreach (var client in RobloxProcesses.Clients())
        {
            using (client)
            {
                try
                {
                    // Unreadable start time throws - and then it's left alone rather than guessed at.
                    if (DateTime.Now - client.StartTime < StartupGrace) continue;

                    if (!windows.TryGetValue(client.Id, out var own))
                        found.Add(new StuckProcess(client.Id, client.ProcessName, Problem.NoWindow));
                    else if (own.All(Native.IsHungAppWindow))
                        found.Add(new StuckProcess(client.Id, client.ProcessName, Problem.NotResponding));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Exited while looking, or can't be inspected - leave it alone.
                }
            }
        }

        foreach (var handler in RobloxProcesses.InSession("RobloxCrashHandler"))
        {
            using (handler)
                found.Add(new StuckProcess(handler.Id, handler.ProcessName, Problem.CrashHandler));
        }

        return found;
    }

    /// <summary>Closes what Find returned. Returns how many actually closed.</summary>
    public static int Close(IEnumerable<StuckProcess> stuck)
    {
        var closed = 0;
        foreach (var s in stuck)
        {
            try
            {
                using var p = Process.GetProcessById(s.Pid);
                p.Kill();
                p.WaitForExit(3000);
                closed++;
                Log.Info($"Closed stuck Roblox process PID {s.Pid} ({s.Problem}).");
            }
            catch (ArgumentException)
            {
                closed++; // already gone
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not close PID {s.Pid}: {ex.Message}");
            }
        }
        return closed;
    }
}
