using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace Lingstrap.Services;

/// <summary>Finds a Roblox client window and its client area on screen.</summary>
public static class RobloxWindowLocator
{
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);

        // Windows 10 1607+. GetClientRect/ClientToScreen return physical pixels, but WPF's own
        // Window.Left/Top/Width/Height are always in device-independent units (1/96 inch) - on a
        // monitor running anything other than 100% scaling, handing WPF the raw physical-pixel
        // numbers places the window at (value * scale) physical pixels instead of the intended
        // spot, since WPF re-scales whatever DIU value it's given by the monitor's own factor.
        [DllImport("user32.dll")] public static extern int GetDpiForWindow(IntPtr hWnd);
    }

    /// <summary>
    /// Quick, unlogged check for whether a join's overlay lookup even has a chance yet. A cold
    /// Roblox launch can have its join line appear in the log several seconds before its window
    /// actually exists, so callers can poll this cheaply instead of repeatedly attempting (and
    /// logging) the real, per-client lookup while waiting for it to show up.
    /// </summary>
    public static bool HasAnyReadyClientWindow() =>
        Process.GetProcessesByName("RobloxPlayerBeta")
            .Any(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } });

    /// <summary>
    /// The client area (and window handle) of whichever Roblox client this specific log file
    /// belongs to - matters with multi-instance, where more than one RobloxPlayerBeta.exe can be
    /// running and "just pick one" would put every client's overlay banner on the same single
    /// window. There's no direct filename-to-process mapping, but each client creates its log file
    /// within a moment of starting, so the process whose start time is closest to the file's
    /// creation time is it. Falls back to the first window found if there's only one client, no
    /// file to match against, or nothing lines up closely enough to trust. The handle is returned
    /// alongside the rect so a caller can position itself in that specific window's z-order.
    /// </summary>
    public static (Rect Rect, IntPtr Hwnd)? FindClientRectForLogFile(string? logFile)
    {
        var candidates = Process.GetProcessesByName("RobloxPlayerBeta")
            .Select(p => { try { return (Process: p, Handle: p.MainWindowHandle); } catch { return (Process: p, Handle: IntPtr.Zero); } })
            .Where(x => x.Handle != IntPtr.Zero)
            .ToList();

        if (candidates.Count == 0)
        {
            Log.Info("Roblox window lookup: no RobloxPlayerBeta process with a main window.");
            return null;
        }

        var hwnd = candidates[0].Handle;

        if (candidates.Count > 1 && logFile != null && File.Exists(logFile))
        {
            try
            {
                var fileCreated = File.GetCreationTimeUtc(logFile);
                var best = candidates
                    .Select(x => new { x.Handle, Diff = Math.Abs((x.Process.StartTime.ToUniversalTime() - fileCreated).TotalSeconds) })
                    .OrderBy(x => x.Diff)
                    .First();

                if (best.Diff <= 15)
                {
                    hwnd = best.Handle;
                }
                else
                {
                    Log.Warn($"Roblox window lookup: couldn't confidently match {Path.GetFileName(logFile)} to a specific client " +
                             $"(closest process start time was {best.Diff:0.#}s away) - using the first window found instead.");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Roblox window lookup: could not match log file to a process ({ex.Message}) - using the first window found instead.");
            }
        }

        var rect = RectFor(hwnd);
        return rect is { } r ? (r, hwnd) : null;
    }

    private static Rect? RectFor(IntPtr hwnd)
    {
        var result = GetClientRectQuiet(hwnd, out var reason);
        if (result is { } r)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            Log.Info($"Roblox window lookup: hwnd={hwnd} client rect (DIU) at " +
                     $"({r.Left.ToString("0.#", ic)},{r.Top.ToString("0.#", ic)}) {r.Width.ToString("0.#", ic)}x{r.Height.ToString("0.#", ic)}.");
        }
        else if (reason != null)
        {
            Log.Warn($"Roblox window lookup: {reason}");
        }
        return result;
    }

    /// <summary>
    /// Same lookup as <see cref="RectFor"/> but without logging - meant to be polled many times a
    /// second (e.g. to keep the overlay banner following the Roblox window while it's dragged),
    /// where logging every tick would flood the log file for no benefit.
    /// </summary>
    public static Rect? GetClientRectQuiet(IntPtr hwnd) => GetClientRectQuiet(hwnd, out _);

    private static Rect? GetClientRectQuiet(IntPtr hwnd, out string? failureReason)
    {
        failureReason = null;

        if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd))
        {
            failureReason = $"hwnd={hwnd} found but not usable (visible={Native.IsWindowVisible(hwnd)}, minimized={Native.IsIconic(hwnd)}).";
            return null;
        }

        if (!Native.GetClientRect(hwnd, out var rect))
        {
            failureReason = $"GetClientRect failed for hwnd={hwnd}.";
            return null;
        }

        var topLeft = new Native.POINT { X = rect.Left, Y = rect.Top };
        if (!Native.ClientToScreen(hwnd, ref topLeft))
        {
            failureReason = $"ClientToScreen failed for hwnd={hwnd}.";
            return null;
        }

        // Convert physical pixels (what Win32 just gave us) to device-independent units (what WPF
        // expects for Window.Left/Top/Width/Height) using this specific window's own DPI - handles
        // the common case (a scaled single monitor) directly, and the per-monitor case correctly
        // too, since GetDpiForWindow reports whichever monitor the window actually is on.
        var dpi = Native.GetDpiForWindow(hwnd);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;

        return new Rect(topLeft.X / scale, topLeft.Y / scale,
            (rect.Right - rect.Left) / scale, (rect.Bottom - rect.Top) / scale);
    }
}
