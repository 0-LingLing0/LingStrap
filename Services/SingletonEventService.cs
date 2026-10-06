using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Lingstrap.Services;

/// <summary>
/// Finds, and releases, ROBLOX_singletonEvent handles inside running Roblox clients.
///
/// Roblox keeps itself to one window per session with that event: every client waits on it, and a
/// newly started client signals it, which makes every older client shut down. Multi-instance works
/// by making sure the name can't be an event at all (see MultiInstanceWatcher) - but a client that
/// started before that, e.g. one opened without Lingstrap, already holds a real one. Closing that
/// handle inside it (what Process Explorer's "Close Handle" does) removes the event again without
/// touching the client, which keeps running normally.
/// </summary>
public static class SingletonEventService
{
    public const string EventName = "ROBLOX_singletonEvent";

    /// <summary>
    /// Every process in this session holding a real ROBLOX_singletonEvent - not just Roblox clients:
    /// on one PC the event stayed alive after it was released in every client Lingstrap could see,
    /// held by something it hadn't looked at. Also returns the Roblox clients that couldn't be
    /// inspected at all, which may be holding it too.
    /// </summary>
    public static (List<int> Holders, List<int> Uninspectable) Scan()
    {
        var self = Environment.ProcessId;
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var pids = new List<int>();
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            using (p)
            {
                try { if (p.SessionId == session && p.Id != self) pids.Add(p.Id); }
                catch { /* exited */ }
            }
        }

        var clients = new HashSet<int>();
        foreach (var c in RobloxProcesses.Clients())
            using (c) clients.Add(c.Id);

        var scan = EventHandlesIn(pids, out var unopened);
        var uninspectable = unopened.Keys.Where(clients.Contains).ToList();
        foreach (var pid in uninspectable)
            Log.Warn($"Multi-instance: couldn't look inside Roblox PID {pid} (Win32 error {unopened[pid]}) - it may be holding the single-window signal.");

        return (scan.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToList(), uninspectable);
    }

    /// <summary>Processes in this session holding a real ROBLOX_singletonEvent, by PID.</summary>
    public static List<int> FindHolders() => Scan().Holders;

    /// <summary>Closes every ROBLOX_singletonEvent handle inside these processes. Returns how many.</summary>
    public static int Release(IEnumerable<int> pids)
    {
        var closed = 0;
        foreach (var (pid, handles) in EventHandlesIn(pids, out _))
        {
            if (handles.Count == 0) continue;
            var process = Native.OpenProcess(ProcessDupHandle, false, pid);
            if (process == IntPtr.Zero) continue;
            try
            {
                var here = handles.Count(h => Native.DuplicateHandle(process, h, IntPtr.Zero, out _, 0, false, DuplicateCloseSource));
                if (here > 0) Log.Info($"Multi-instance: released Roblox's single-window signal inside PID {pid}.");
                closed += here;
            }
            finally
            {
                Native.CloseHandle(process);
            }
        }
        return closed;
    }

    /// <summary>For each process: the handle values of every Event named ...\ROBLOX_singletonEvent
    /// inside it. One pass over the system's handle list for all of them. Processes that couldn't be
    /// opened come back in <paramref name="unopened"/> with the Win32 error.</summary>
    private static Dictionary<int, List<IntPtr>> EventHandlesIn(IEnumerable<int> pids, out Dictionary<int, int> unopened)
    {
        unopened = new Dictionary<int, int>();
        var result = new Dictionary<int, List<IntPtr>>();
        var processes = new Dictionary<int, IntPtr>();
        foreach (var pid in pids.Distinct())
        {
            result[pid] = new List<IntPtr>();
            var handle = Native.OpenProcess(ProcessDupHandle, false, pid);
            if (handle != IntPtr.Zero) processes[pid] = handle;
            else unopened[pid] = Marshal.GetLastWin32Error();
        }
        if (processes.Count == 0) return result;

        var buffer = IntPtr.Zero;
        // An event of our own, to learn which object-type number this Windows build uses for events -
        // so only handles of that type get copied and inspected, not every handle of every process.
        using var probe = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset);
        try
        {
            buffer = QuerySystemHandles();
            if (buffer == IntPtr.Zero) return result;

            var count = (long)Marshal.ReadIntPtr(buffer);
            var entrySize = Marshal.SizeOf<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();
            var self = Native.GetCurrentProcess();
            var selfPid = (ulong)Environment.ProcessId;
            var probeHandle = probe.SafeWaitHandle.DangerousGetHandle();

            int eventType = -1;
            for (long i = 0; i < count && eventType < 0; i++)
            {
                var entry = Marshal.PtrToStructure<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>(buffer + 2 * IntPtr.Size + (int)(i * entrySize));
                if ((ulong)entry.UniqueProcessId == selfPid && entry.HandleValue == probeHandle) eventType = entry.ObjectTypeIndex;
            }

            for (long i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>(buffer + 2 * IntPtr.Size + (int)(i * entrySize));
                if (eventType >= 0 && entry.ObjectTypeIndex != eventType) continue;
                if (!processes.TryGetValue((int)(ulong)entry.UniqueProcessId, out var process)) continue;
                if (!Native.DuplicateHandle(process, entry.HandleValue, self, out var copy, 0, false, DuplicateSameAccess)) continue;

                try
                {
                    // Type first, and the name only for events: asking some other kinds of handle (a
                    // synchronous pipe) for their name can block forever.
                    if (QueryString(copy, ObjectTypeInformation) != "Event") continue;
                    var name = QueryString(copy, ObjectNameInformation);
                    if (name != null && name.EndsWith("\\" + EventName, StringComparison.Ordinal))
                        result[(int)(ulong)entry.UniqueProcessId].Add(entry.HandleValue);
                }
                finally
                {
                    Native.CloseHandle(copy);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Multi-instance: could not look inside the Roblox clients: {ex.Message}");
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            foreach (var handle in processes.Values) Native.CloseHandle(handle);
        }

        return result;
    }
    private static IntPtr QuerySystemHandles()
    {
        var size = 1 << 20;
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(size);
            var status = Native.NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, size, out var needed);
            if (status == 0) return buffer;

            Marshal.FreeHGlobal(buffer);
            if (status != StatusInfoLengthMismatch || size > 256 << 20) return IntPtr.Zero;
            size = Math.Max(size * 2, needed + 65536);
        }
    }

    private static string? QueryString(IntPtr handle, int infoClass)
    {
        var buffer = Marshal.AllocHGlobal(4096);
        try
        {
            if (Native.NtQueryObject(handle, infoClass, buffer, 4096, out _) != 0) return null;
            // Both info classes used here start with a UNICODE_STRING.
            var length = Marshal.ReadInt16(buffer);
            var text = Marshal.ReadIntPtr(buffer + IntPtr.Size);
            return length == 0 || text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const int SystemExtendedHandleInformation = 64;
    private const int ObjectNameInformation = 1;
    private const int ObjectTypeInformation = 2;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int ProcessDupHandle = 0x0040;
    private const int DuplicateCloseSource = 0x1;
    private const int DuplicateSameAccess = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
    {
        public IntPtr Object;
        public UIntPtr UniqueProcessId;
        public IntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    private static class Native
    {
        [DllImport("ntdll.dll")] public static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int needed);
        [DllImport("ntdll.dll")] public static extern int NtQueryObject(IntPtr handle, int infoClass, IntPtr buffer, int length, out int returned);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess,
            out IntPtr target, int access, bool inherit, int options);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    }
}
