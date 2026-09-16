using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Lingstrap.Services;

/// <summary>
/// Real CPU topology via GetLogicalProcessorInformationEx - physical cores (both SMT threads
/// grouped together) and L3 cache domains (CCDs on multi-chiplet chips). No assumptions about
/// how many logical processors share a core; the OS is asked directly.
/// </summary>
public static class CpuTopologyService
{
    private const int RelationProcessorCore = 0;
    private const int RelationCache = 2;

    public readonly record struct PhysicalCore(ulong Mask);
    public readonly record struct CacheDomain(ulong Mask, uint SizeBytes);

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetLogicalProcessorInformationEx(
            int relationshipType, IntPtr buffer, ref uint returnedLength);
    }

    /// <summary>One entry per physical core (mask covers 1 or 2 logical processors), or null if the query failed.</summary>
    public static List<PhysicalCore>? GetPhysicalCores()
    {
        var buffer = QueryRaw(RelationProcessorCore);
        if (buffer is null) return null;

        var cores = new List<PhysicalCore>();
        Walk(buffer, ptr =>
        {
            var groupCount = Marshal.ReadInt16(ptr, 22);
            for (var g = 0; g < groupCount; g++)
            {
                var groupPtr = IntPtr.Add(ptr, 24 + g * 16);
                var mask = unchecked((ulong)Marshal.ReadIntPtr(groupPtr, 0).ToInt64());
                var group = Marshal.ReadInt16(groupPtr, 8);
                if (group == 0) cores.Add(new PhysicalCore(mask));
            }
        });

        return cores.Count > 0 ? cores : null;
    }

    /// <summary>One entry per distinct L3 cache domain (processor group 0 only), or null if the query failed.</summary>
    public static List<CacheDomain>? GetL3CacheDomains()
    {
        var buffer = QueryRaw(RelationCache);
        if (buffer is null) return null;

        var domains = new List<CacheDomain>();
        Walk(buffer, ptr =>
        {
            var level = Marshal.ReadByte(ptr, 0);
            if (level != 3) return;

            var cacheSize = unchecked((uint)Marshal.ReadInt32(ptr, 4));
            var groupAffinityPtr = IntPtr.Add(ptr, 32);
            var mask = unchecked((ulong)Marshal.ReadIntPtr(groupAffinityPtr, 0).ToInt64());
            var group = Marshal.ReadInt16(groupAffinityPtr, 8);
            if (group == 0) domains.Add(new CacheDomain(mask, cacheSize));
        });

        return domains.Count > 0 ? domains : null;
    }

    private static byte[]? QueryRaw(int relationshipType)
    {
        uint length = 0;
        Native.GetLogicalProcessorInformationEx(relationshipType, IntPtr.Zero, ref length);
        if (length == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!Native.GetLogicalProcessorInformationEx(relationshipType, buffer, ref length))
                return null;

            var managed = new byte[length];
            Marshal.Copy(buffer, managed, 0, (int)length);
            return managed;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Walks a buffer of variable-length SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX records, passing the pointer to each record's relationship-specific data (8 bytes past the record start).</summary>
    private static void Walk(byte[] buffer, Action<IntPtr> onRecord)
    {
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var basePtr = handle.AddrOfPinnedObject();
            var offset = 0;
            while (offset + 8 <= buffer.Length)
            {
                var recordPtr = IntPtr.Add(basePtr, offset);
                var size = Marshal.ReadInt32(recordPtr, 4);
                if (size <= 0) break;

                onRecord(IntPtr.Add(recordPtr, 8));

                offset += size;
            }
        }
        finally
        {
            handle.Free();
        }
    }
}
