using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Lingstrap.Services;

/// <summary>
/// Caps what each running Roblox client may use, for running many accounts at once (the AFK preset):
/// a single logical CPU per client, and a hard limit on how much RAM each one keeps resident.
///
/// Runs in its own detached process (-limitswatcher, the same pattern as the other watchers) so it
/// keeps working when CloseLingstrapOnLaunch closes Lingstrap, and so clients launched later - each
/// through its own short-lived Lingstrap - are picked up by the one watcher already running.
///
/// The memory limit doesn't free anything Roblox needs: Windows moves whatever is over the limit out
/// to the page file and brings it back on demand. A client never runs out of memory and doesn't
/// crash - it just slows down when it touches something that was moved out, which at 3 FPS, unattended,
/// barely matters. That's what makes 15 clients fit in a few GB of RAM.
/// </summary>
public static class ClientLimitsService
{
    private const string WatcherGuardMutexName = "Lingstrap_ClientLimitsWatcherActive";
    private const string WatcherArg = "-limitswatcher";

    /// <summary>Loading a game touches far more memory than idling in it. Capping from the first
    /// second made joining crawl, so a client gets this long before its limit applies.</summary>
    private static readonly TimeSpan MemoryLimitDelay = TimeSpan.FromSeconds(45);

    private const int QuotaLimitsHardwsMinDisable = 0x2;
    private const int QuotaLimitsHardwsMaxEnable = 0x4;
    private const int QuotaLimitsHardwsMaxDisable = 0x8;

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessWorkingSetSizeEx(IntPtr process, IntPtr minimumWorkingSetSize,
            IntPtr maximumWorkingSetSize, int flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool K32EmptyWorkingSet(IntPtr process);

        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessWorkingSetSizeEx(IntPtr process, out IntPtr minimumWorkingSetSize,
            out IntPtr maximumWorkingSetSize, out int flags);
    }

    public static bool AnyLimitOn =>
        SettingsService.Current.OneCorePerClient || SettingsService.Current.MemoryLimitMb > 0
        || SettingsService.Current.SmallWindows;

    /// <summary>Clients whose window has already been shrunk - each one is, exactly once.</summary>
    private static readonly Dictionary<int, DateTime> WindowFirstSeen = new();
    private static readonly HashSet<int> ShrinkLogged = new();

    /// <summary>Call once a Roblox client has started. Starts the watcher if a limit is on; a no-op
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
        var pinned = new Dictionary<int, int>();   // PID -> logical CPU it's locked to
        var limited = new Dictionary<int, int>();  // PID -> MB limit applied

        try
        {
            var startDeadline = DateTime.UtcNow.AddSeconds(30);
            while (RobloxProcesses.Count() == 0 && DateTime.UtcNow < startDeadline)
                Thread.Sleep(500);

            var tick = 0;
            while (true)
            {
                // Re-read every few seconds, so changing a limit in Lingstrap reaches running clients.
                if (tick++ % 3 == 0) SettingsService.Load();

                var clients = RobloxProcesses.Clients();
                if (clients.Length == 0) break;

                try { Enforce(clients, pinned, limited); }
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

    private static void Enforce(Process[] clients, Dictionary<int, int> pinned, Dictionary<int, int> limited)
    {
        var s = SettingsService.Current;
        var alive = clients.Select(c => c.Id).ToHashSet();
        foreach (var gone in pinned.Keys.Where(id => !alive.Contains(id)).ToList()) pinned.Remove(gone);
        foreach (var gone in limited.Keys.Where(id => !alive.Contains(id)).ToList()) limited.Remove(gone);
        Reapplied.RemoveWhere(id => !alive.Contains(id));
        ShrinkLogged.RemoveWhere(id => !alive.Contains(id));
        Boosted.RemoveWhere(id => !alive.Contains(id));
        foreach (var gone in LastNotResponding.Keys.Where(id => !alive.Contains(id)).ToList()) LastNotResponding.Remove(gone);
        foreach (var gone in WindowFirstSeen.Keys.Where(id => !alive.Contains(id)).ToList()) WindowFirstSeen.Remove(gone);
        Trimmed.RemoveWhere(id => !alive.Contains(id));

        if (s.MemoryLimitMb > 0 && DateTime.UtcNow - _lastMemoryReport > TimeSpan.FromMinutes(1))
        {
            _lastMemoryReport = DateTime.UtcNow;
            Log.Info("Client limits: RAM in use - " + string.Join(", ", clients.Select(c =>
            {
                try { c.Refresh(); return $"PID {c.Id} {c.WorkingSet64 / (1024 * 1024)} MB"; }
                catch { return $"PID {c.Id} ?"; }
            })) + $" (limit {s.MemoryLimitMb} MB).");
        }

        // Stable order, so an existing client keeps its CPU when another starts or exits.
        foreach (var client in clients.OrderBy(SafeStartTime))
        {
            try
            {
                ApplyCore(client, s.OneCorePerClient, pinned);
                ApplyMemory(client, s.MemoryLimitMb, s.MemoryBoostMb, limited);
                if (s.SmallWindows) ShrinkWindowOf(client);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Exited mid-way, or not ours to touch - the next pass sorts it out.
            }
        }
    }

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

    /// <summary>How long a client has to respond normally again before a raised cap drops back -
    /// without it, a client that stalls on and off would bounce between the two every few seconds.</summary>
    private static readonly TimeSpan BoostRecovery = TimeSpan.FromSeconds(15);

    private static readonly Dictionary<int, DateTime> LastNotResponding = new();
    private static readonly HashSet<int> Boosted = new();

    /// <summary>
    /// The cap to use right now: the boost while the client's window is "Not Responding" (and for
    /// BoostRecovery after), the normal limit otherwise. A very low cap can starve a client badly
    /// enough to freeze it - mostly while it loads a new area - and more room is exactly what gets
    /// it moving again; once it's fine, the low cap comes back.
    /// </summary>
    private static int EffectiveLimit(Process client, int limitMb, int boostMb)
    {
        if (boostMb <= limitMb)
        {
            Boosted.Remove(client.Id);
            return limitMb;
        }

        client.Refresh();
        var hwnd = client.MainWindowHandle;
        // IsHungAppWindow answers instantly (Windows' own "Not Responding" test), where
        // Process.Responding waits up to 5 seconds per client for a reply.
        if (hwnd != IntPtr.Zero && Native.IsHungAppWindow(hwnd))
            LastNotResponding[client.Id] = DateTime.UtcNow;

        var boost = LastNotResponding.TryGetValue(client.Id, out var at) && DateTime.UtcNow - at < BoostRecovery;
        if (boost && Boosted.Add(client.Id))
            Log.Info($"Client limits: PID {client.Id} is not responding - memory cap raised to {boostMb} MB until it recovers.");
        else if (!boost && Boosted.Remove(client.Id))
            Log.Info($"Client limits: PID {client.Id} is responding again - memory cap back to {limitMb} MB.");

        return boost ? boostMb : limitMb;
    }

    private static void ApplyMemory(Process client, int limitMb, int boostMb, Dictionary<int, int> limited)
    {
        if (limitMb <= 0)
        {
            if (limited.Remove(client.Id))
            {
                // Real sizes, not -1/-1: that pair means "empty the working set" and the flags are ignored.
                Native.GetProcessWorkingSetSizeEx(client.Handle, out var oldMin, out var oldMax, out _);
                Native.SetProcessWorkingSetSizeEx(client.Handle, oldMin, oldMax, QuotaLimitsHardwsMinDisable | QuotaLimitsHardwsMaxDisable);
                Log.Info($"Client limits: memory limit removed from PID {client.Id}.");
            }
            return;
        }

        if (DateTime.Now - SafeStartTime(client) < MemoryLimitDelay) return;

        limitMb = EffectiveLimit(client, limitMb, boostMb);
        var max = (long)limitMb * 1024 * 1024;
        var min = Math.Min(16L * 1024 * 1024, max / 4);
        var firstTime = !limited.ContainsKey(client.Id);

        // Checked every pass, not set once: in testing the cap was accepted and Roblox still grew
        // well past it, so something on Roblox's side resets it. Put it back whenever it's gone.
        var capInPlace = Native.GetProcessWorkingSetSizeEx(client.Handle, out _, out var currentMax, out var flags)
                         && (flags & QuotaLimitsHardwsMaxEnable) != 0 && (long)currentMax == max;
        if (!capInPlace)
        {
            if (Native.SetProcessWorkingSetSizeEx(client.Handle, (IntPtr)min, (IntPtr)max,
                    QuotaLimitsHardwsMinDisable | QuotaLimitsHardwsMaxEnable))
            {
                if (firstTime) Log.Info($"Client limits: PID {client.Id} limited to {limitMb} MB of RAM.");
                else if (Reapplied.Add(client.Id)) Log.Info($"Client limits: PID {client.Id} had its memory cap removed - putting it back (logged once).");
            }
            else if (firstTime)
            {
                Log.Warn($"Client limits: could not cap PID {client.Id}'s memory (Win32 error {Marshal.GetLastWin32Error()}) - trimming it instead.");
            }
        }
        limited[client.Id] = limitMb;

        // And the part that holds regardless of the cap: whenever the client is over its limit, move
        // everything it isn't actively using out of RAM. Windows pages it back in on demand.
        client.Refresh();
        if (client.WorkingSet64 > max && Native.K32EmptyWorkingSet(client.Handle) && Trimmed.Add(client.Id))
            Log.Info($"Client limits: PID {client.Id} was over {limitMb} MB - trimming it whenever that happens (logged once).");
    }

    private static DateTime _lastMemoryReport = DateTime.MinValue;
    private static readonly HashSet<int> Reapplied = new();
    private static readonly HashSet<int> Trimmed = new();

    /// <summary>
    /// Asks the window to be 1x1 and lets Roblox refuse: a window enforces its own minimum size, so
    /// whatever it ends up at IS the smallest Roblox allows, without hardcoding a number that a
    /// future Roblox update could change. Keeps the window where it is, and only happens once.
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
