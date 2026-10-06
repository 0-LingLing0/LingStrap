using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Lingstrap.Services;

/// <summary>
/// A different path to the same Roblox install for every client that runs at once.
///
/// A new Roblox client looks for one already running from the SAME exe path - Roblox names its
/// shared memory after that path - and if it finds one, hands its launch over to it ("NoReload") and
/// exits: the older client switches to the new game instead of a second window opening. A client
/// started from another path doesn't find it. Each extra client gets a directory junction to the
/// version folder (Lingstrap\Instances\N) - another path to the very same files, costing no disk
/// space, so mods, FastFlags and updates all still apply to every client.
/// </summary>
public static class MultiInstancePaths
{
    private static string InstancesRoot => Path.Combine(Paths.Root, "Instances");

    /// <summary>
    /// The RobloxPlayerBeta.exe path to start the next client from: the real one if no client in this
    /// session is running from it, otherwise the first free junction (created or re-pointed at the
    /// current version as needed). Falls back to the real path if a junction can't be made.
    /// </summary>
    public static string ChooseLaunchExe(string versionFolder)
    {
        var realExe = Path.Combine(versionFolder, "RobloxPlayerBeta.exe");
        var inUse = RunningExePaths();
        if (!inUse.Contains(Normalize(realExe))) return realExe;

        for (var slot = 1; slot <= 64; slot++)
        {
            var junction = Path.Combine(InstancesRoot, slot.ToString());
            var exe = Path.Combine(junction, "RobloxPlayerBeta.exe");
            if (inUse.Contains(Normalize(exe))) continue;

            if (EnsureJunction(junction, versionFolder))
            {
                Log.Info($"Multi-instance: another client is already running from {realExe} - starting this one from {exe}.");
                return exe;
            }
            break;
        }

        Log.Warn("Multi-instance: no separate path available - this client may hand over to the one already running.");
        return realExe;
    }

    /// <summary>The version folder an exe really lives in, seeing through Lingstrap's junctions -
    /// so a client started from Instances\3 still counts as running from the version folder.</summary>
    public static string ResolveExePath(string exePath)
    {
        try
        {
            var full = Path.GetFullPath(exePath);
            var root = Path.GetFullPath(InstancesRoot).TrimEnd('\\') + "\\";
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return full;

            var slotDir = Path.Combine(root, full[root.Length..].Split('\\')[0]);
            var target = new DirectoryInfo(slotDir).LinkTarget;
            return target == null ? full : Path.Combine(target, Path.GetRelativePath(slotDir, full));
        }
        catch
        {
            return exePath;
        }
    }

    /// <summary>Makes sure Instances\N is a junction to this version folder.</summary>
    private static bool EnsureJunction(string junction, string versionFolder)
    {
        try
        {
            var target = Path.GetFullPath(versionFolder).TrimEnd('\\');
            if (Directory.Exists(junction))
            {
                var current = new DirectoryInfo(junction).LinkTarget?.TrimEnd('\\');
                if (current != null && current.Equals(target, StringComparison.OrdinalIgnoreCase)) return true;
                // Points at an old version (or isn't a junction) - remove just the link, never the files
                // behind it: a non-recursive delete of a junction removes only the junction itself.
                if (current == null) return false;
                Directory.Delete(junction, recursive: false);
            }

            Directory.CreateDirectory(InstancesRoot);
            var mklink = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                ArgumentList = { "/c", "mklink", "/J", junction, target },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            mklink.WaitForExit(5000);
            return File.Exists(Path.Combine(junction, "RobloxPlayerBeta.exe"));
        }
        catch (Exception ex)
        {
            Log.Warn($"Multi-instance: could not prepare {junction}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The exe paths this session's Roblox clients are running from, as launched (not
    /// resolved through junctions - which junction is taken is exactly what matters here).</summary>
    private static HashSet<string> RunningExePaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var client in RobloxProcesses.Clients())
        {
            using (client)
            {
                var exe = ExePathOf(client.Id);
                if (exe != null) paths.Add(Normalize(exe));
            }
        }
        return paths;
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\');

    /// <summary>
    /// The path a client was STARTED from. Not QueryFullProcessImageName: Windows resolves junctions
    /// for the image file, so a client started from Instances\1 reports the real version folder - and
    /// the next launch, seeing Instances\1 as free, started from it again and handed over to that
    /// client. The command line still has the path as launched; Windows hands it out with the same
    /// limited query right, without reading the process's memory.
    /// </summary>
    private static string? ExePathOf(int pid)
    {
        var handle = Native.OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var fromCommandLine = FirstArgument(CommandLineOf(handle));
            if (fromCommandLine != null && fromCommandLine.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return fromCommandLine;

            var buffer = new StringBuilder(1024);
            var length = buffer.Capacity;
            return Native.QueryFullProcessImageName(handle, 0, buffer, ref length) ? buffer.ToString(0, length) : null;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    private static string? CommandLineOf(IntPtr process)
    {
        const int ProcessCommandLineInformation = 60;
        var size = 4096;
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try
        {
            if (Native.NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, size, out _) != 0) return null;
            var length = System.Runtime.InteropServices.Marshal.ReadInt16(buffer);
            var text = System.Runtime.InteropServices.Marshal.ReadIntPtr(buffer + IntPtr.Size);
            return length == 0 || text == IntPtr.Zero ? null : System.Runtime.InteropServices.Marshal.PtrToStringUni(text, length / 2);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The program part of a command line: quoted, or up to the first space.</summary>
    private static string? FirstArgument(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var s = commandLine.TrimStart();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }
        var space = s.IndexOf(' ');
        return space < 0 ? s : s[..space];
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("ntdll.dll")]
        public static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer, int length, out int returned);
    }
}
