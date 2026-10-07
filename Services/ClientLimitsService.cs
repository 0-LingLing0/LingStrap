using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Lingstrap.Services;

/// <summary>
/// Farming helpers that work on Roblox clients from the outside with ordinary window and scheduling
/// calls - the same things Task Manager or dragging a window can do: one CPU per client, small or
/// minimized windows, and closing clients that are stuck.
///
/// It used to also cap each client's RAM (forcing its working set out to the page file) and CPU (a
/// job object with a hard rate limit). Both changed how the running Roblox process behaves from
/// outside, and were removed after an account was banned for "bypasses of our systems".
///
/// Runs in its own detached process (-limitswatcher, the same pattern as the other watchers) so it
/// keeps working when CloseLingstrapOnLaunch closes Lingstrap, and so clients launched later - each
/// through its own short-lived Lingstrap - are picked up by the one watcher already running.
/// </summary>
public static class ClientLimitsService
{
    private const string WatcherGuardMutexName = "Lingstrap_ClientLimitsWatcherActive";
    private const string WatcherArg = "-limitswatcher";

    private static class Native
    {
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
    }

    public static bool AnyLimitOn =>
        SettingsService.Current.OneCorePerClient || SettingsService.Current.SmallWindows
        || SettingsService.Current.MinimizeAfterLoad || SettingsService.Current.AutoCloseStuck;

    /// <summary>Call once a Roblox client has started. Starts the watcher if anything is on; a no-op
    /// if one is already running, since it covers every client by name.</summary>
    public static void OnRobloxStarted()
    {
        if (!AnyLimitOn) return;

        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = WatcherArg,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not start the client limits watcher: {ex.Message}");
        }
    }

    /// <summary>Runs on the watcher process. Blocks until every Roblox client has closed.</summary>
    public static void RunWatcherAndBlock()
    {
        using var guard = new Mutex(initiallyOwned: true, name: WatcherGuardMutexName, createdNew: out var isFirst);
        if (!isFirst) return;

        Log.Info("Client limits watcher started.");
        var pinned = new Dictionary<int, int>(); // PID -> logical CPU it's locked to

        try
        {
            var startDeadline = DateTime.UtcNow.AddSeconds(30);
            while (RobloxProcesses.Count() == 0 && DateTime.UtcNow < startDeadline)
                Thread.Sleep(500);

            var tick = 0;
            while (true)
            {
                // Re-read every few seconds, so changing a setting in Lingstrap reaches running clients.
                if (tick++ % 3 == 0) SettingsService.Load(quiet: true);

                var clients = RobloxProcesses.Clients();
                if (clients.Length == 0) break;

                try { Enforce(clients, pinned); }
                catch (Exception ex) { Log.Warn($"Client limits: {ex.Message}"); }
                finally { foreach (var c in clients) c.Dispose(); }

                Thread.Sleep(3000);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Client limits watcher failed", ex);
        }

        Log.Info("Client limits watcher exiting - all Roblox clients closed.");
    }

    private static void Enforce(Process[] clients, Dictionary<int, int> pinned)
    {
        var s = SettingsService.Current;
        var alive = clients.Select(c => c.Id).ToHashSet();
        foreach (var gone in pinned.Keys.Where(id => !alive.Contains(id)).ToList()) pinned.Remove(gone);
        foreach (var gone in WindowFirstSeen.Keys.Where(id => !alive.Contains(id)).ToList()) WindowFirstSeen.Remove(gone);
        foreach (var gone in WindowSeenAt.Keys.Where(id => !alive.Contains(id)).ToList()) WindowSeenAt.Remove(gone);
        ShrinkLogged.RemoveWhere(id => !alive.Contains(id));
        Minimized.RemoveWhere(id => !alive.Contains(id));

        // Stable order, so an existing client keeps its CPU when another starts or exits.
        foreach (var client in clients.OrderBy(SafeStartTime))
        {
            try
            {
                ApplyCore(client, s.OneCorePerClient, pinned);
                if (s.SmallWindows) ShrinkWindowOf(client);
                if (s.MinimizeAfterLoad) MinimizeOnceLoaded(client);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Exited mid-way, or not ours to touch - the next pass sorts it out.
            }
        }

        if (s.AutoCloseStuck && DateTime.UtcNow - _lastStuckCheck > StuckCheckInterval)
        {
            _lastStuckCheck = DateTime.UtcNow;
            CloseLongStuck(alive);
        }
    }

    // ---- One core per client -------------------------------------------------------------

    private static void ApplyCore(Process client, bool on, Dictionary<int, int> pinned)
    {
        if (!on)
        {
            if (pinned.Remove(client.Id))
            {
                client.ProcessorAffinity = AllCoresMask();
                Log.Info($"Client limits: PID {client.Id} back on all cores.");
            }
            return;
        }

        if (!pinned.TryGetValue(client.Id, out var cpu))
        {
            cpu = PickCpu(pinned.Values);
            pinned[client.Id] = cpu;
            Log.Info($"Client limits: PID {client.Id} locked to CPU {cpu}.");
        }

        var mask = (IntPtr)(1L << cpu);
        if (client.ProcessorAffinity != mask) client.ProcessorAffinity = mask;
    }

    /// <summary>The least-used logical CPU, preferring one on a physical core no client has yet -
    /// two clients on the two threads of one core compete far more than on separate cores - and
    /// CPU 0 last, since Windows does much of its own work there.</summary>
    private static int PickCpu(IEnumerable<int> taken)
    {
        var count = Math.Min(Environment.ProcessorCount, 64);
        var order = new List<int>();

        var cores = CpuTopologyService.GetPhysicalCores();
        if (cores is { Count: > 0 })
        {
            var threads = cores
                .Select(c => Enumerable.Range(0, 64).Where(b => (c.Mask & (1UL << b)) != 0).ToList())
                .OrderBy(t => t.Contains(0) ? 1 : 0)
                .ToList();
            for (var round = 0; threads.Any(t => t.Count > round); round++)
                order.AddRange(threads.Where(t => t.Count > round).Select(t => t[round]));
        }
        else
        {
            order.AddRange(Enumerable.Range(1, count - 1));
            order.Add(0);
        }

        order = order.Where(c => c < count).ToList();
        var use = taken.GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        return order.OrderBy(c => use.GetValueOrDefault(c)).ThenBy(c => order.IndexOf(c)).First();
    }

    // ---- Small windows -------------------------------------------------------------------

    /// <summary>Clients whose window has already been shrunk - each one is, exactly once.</summary>
    private static readonly Dictionary<int, DateTime> WindowFirstSeen = new();
    private static readonly HashSet<int> ShrinkLogged = new();

    /// <summary>
    /// Asks the window to be 1x1 and lets Roblox refuse: a window enforces its own minimum size, so
    /// whatever it ends up at IS the smallest Roblox allows. Keeps the window where it is, and only
    /// happens once.
    /// </summary>
    private static void ShrinkWindowOf(Process client)
    {
        client.Refresh();
        var hwnd = client.MainWindowHandle;
        if (hwnd == IntPtr.Zero || Native.IsIconic(hwnd)) return;

        // Once, as the window appears - after that its size and position are the user's.
        if (WindowFirstSeen.ContainsKey(client.Id)) return;
        WindowFirstSeen[client.Id] = DateTime.UtcNow;

        const int SwRestore = 9;
        const uint SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;
        if (Native.IsZoomed(hwnd)) Native.ShowWindow(hwnd, SwRestore); // a maximized window can't be resized

        Native.GetWindowRect(hwnd, out var before);
        Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 1, 1, SwpNoMove | SwpNoZOrder | SwpNoActivate);
        Native.GetWindowRect(hwnd, out var after);

        if (ShrinkLogged.Add(client.Id))
            Log.Info($"Client limits: PID {client.Id} window shrunk from {before.Right - before.Left}x{before.Bottom - before.Top} " +
                     $"to {after.Right - after.Left}x{after.Bottom - after.Top} (the smallest Roblox allows).");
    }

    // ---- Minimize after loading ----------------------------------------------------------

    /// <summary>Long enough for a client to load into its game before it's tucked away.</summary>
    private static readonly TimeSpan MinimizeAfter = TimeSpan.FromMinutes(1);
    private static readonly Dictionary<int, DateTime> WindowSeenAt = new();
    private static readonly HashSet<int> Minimized = new();

    /// <summary>
    /// Minimizes the client's window once, a minute after it first appeared. A minimized Roblox
    /// draws next to nothing. Only once: restoring a window by hand keeps it restored.
    /// </summary>
    private static void MinimizeOnceLoaded(Process client)
    {
        if (Minimized.Contains(client.Id)) return;

        client.Refresh();
        var hwnd = client.MainWindowHandle;
        if (hwnd == IntPtr.Zero) return;

        if (!WindowSeenAt.TryGetValue(client.Id, out var seen))
        {
            WindowSeenAt[client.Id] = DateTime.UtcNow;
            return;
        }
        if (DateTime.UtcNow - seen < MinimizeAfter) return;

        const int SwShowMinNoActive = 7; // minimize without stealing focus from whatever's in front
        Native.ShowWindow(hwnd, SwShowMinNoActive);
        Minimized.Add(client.Id);
        Log.Info($"Client limits: minimized PID {client.Id}'s window.");
    }

    // ---- Auto-close stuck clients --------------------------------------------------------

    private static readonly TimeSpan StuckCheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NoWindowGrace = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan NotRespondingGrace = TimeSpan.FromMinutes(3);
    private static DateTime _lastStuckCheck = DateTime.MinValue;
    private static readonly Dictionary<int, DateTime> StuckSince = new();

    /// <summary>
    /// Closes clients that have been without a window (a leftover from a closed or crashed account)
    /// or Not Responding for a good while - checked every 30 seconds, and only once a client has been
    /// stuck across checks, so a client that's merely busy for a moment is never touched.
    /// </summary>
    private static void CloseLongStuck(HashSet<int> alive)
    {
        foreach (var gone in StuckSince.Keys.Where(id => !alive.Contains(id)).ToList()) StuckSince.Remove(gone);

        var stuck = StuckAccountService.Find()
            .Where(x => x.Problem != StuckAccountService.Problem.CrashHandler)
            .ToList();
        foreach (var notStuck in StuckSince.Keys.Where(id => stuck.All(x => x.Pid != id)).ToList()) StuckSince.Remove(notStuck);

        var toClose = new List<StuckAccountService.StuckProcess>();
        foreach (var x in stuck)
        {
            if (!StuckSince.TryGetValue(x.Pid, out var since))
            {
                StuckSince[x.Pid] = DateTime.UtcNow;
                continue;
            }

            var grace = x.Problem == StuckAccountService.Problem.NoWindow ? NoWindowGrace : NotRespondingGrace;
            if (DateTime.UtcNow - since >= grace) toClose.Add(x);
        }

        if (toClose.Count == 0) return;
        Log.Info("Client limits: closing stuck clients - " + string.Join(", ", toClose.Select(x => $"PID {x.Pid} ({x.Problem})")));
        StuckAccountService.Close(toClose);
        foreach (var x in toClose) StuckSince.Remove(x.Pid);
    }

    private static IntPtr AllCoresMask() =>
        unchecked((IntPtr)(long)(Environment.ProcessorCount is > 0 and < 64
            ? (1UL << Environment.ProcessorCount) - 1
            : ulong.MaxValue));

    private static DateTime SafeStartTime(Process p)
    {
        try { return p.StartTime; }
        catch { return DateTime.MaxValue; }
    }
}
