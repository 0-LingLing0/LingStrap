using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Lingstrap.Services;

/// <summary>Lists connected monitors and can relocate the Roblox window onto whichever one is preferred.</summary>
public static class MonitorService
{
    public record MonitorInfo(string DeviceName, System.Drawing.Rectangle Bounds, bool IsPrimary);

    private const int SW_RESTORE = 9;

    private static class Native
    {
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
    }

    /// <summary>All connected monitors, in the physical-pixel coordinate space Win32 window positioning uses.</summary>
    public static List<MonitorInfo> GetMonitors() =>
        Screen.AllScreens.Select(s => new MonitorInfo(s.DeviceName, s.Bounds, s.Primary)).ToList();

    /// <summary>
    /// Moves and resizes a window to fill whichever monitor is saved as preferred - a no-op if none
    /// is set, or if the saved monitor isn't connected any more (Roblox is simply left wherever
    /// Windows already put it either way). Restores the window first since a maximized window
    /// ignores MoveWindow otherwise.
    /// </summary>
    public static void MoveToPreferredMonitor(IntPtr hwnd)
    {
        var deviceName = SettingsService.Current.PreferredMonitorDeviceName;
        if (string.IsNullOrEmpty(deviceName)) return;

        var target = GetMonitors().FirstOrDefault(m => m.DeviceName == deviceName);
        if (target is null)
        {
            Log.Warn($"Preferred launch monitor \"{deviceName}\" is no longer connected - leaving Roblox where Windows put it.");
            return;
        }

        try
        {
            Native.ShowWindow(hwnd, SW_RESTORE);
            Native.MoveWindow(hwnd, target.Bounds.X, target.Bounds.Y, target.Bounds.Width, target.Bounds.Height, true);
            Log.Info($"Moved Roblox to preferred monitor {deviceName} ({target.Bounds.Width}x{target.Bounds.Height} at {target.Bounds.X},{target.Bounds.Y}).");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not move Roblox to preferred monitor: {ex.Message}");
        }
    }
}
