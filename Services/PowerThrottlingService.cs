using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lingstrap.Services;

/// <summary>
/// Opts a process out of Windows' own power-saving throttling (EcoQoS, "Efficiency Mode" in Task
/// Manager) - a common, silent cause of a game staying capped well below its real framerate,
/// especially on laptops on battery or a Balanced power plan, completely independent of anything
/// Roblox itself is configured to do. No GBS setting or FastFlag can work around this: Windows
/// throttles the process's actual CPU execution speed before Roblox ever gets the chance to render
/// more frames, so an uncap that only touches Roblox's own files can quietly do nothing on a machine
/// where this is the real cause. Per Microsoft's own guidance, EcoQoS "should not be used for
/// performance critical or foreground user experiences" - a game the user just launched to play is
/// exactly that, so opting it out here is correcting a misclassification, not fighting the OS.
/// </summary>
public static class PowerThrottlingService
{
    private const int ProcessPowerThrottlingClass = 4; // PROCESS_INFORMATION_CLASS.ProcessPowerThrottling
    private const uint CurrentVersion = 1;              // PROCESS_POWER_THROTTLING_CURRENT_VERSION
    private const uint ExecutionSpeed = 0x1;             // PROCESS_POWER_THROTTLING_EXECUTION_SPEED

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetProcessInformation(IntPtr hProcess, int processInformationClass,
            ref PROCESS_POWER_THROTTLING_STATE processInformation, uint processInformationSize);
    }

    /// <summary>Tells Windows to never place this process into its power-saving EcoQoS state.</summary>
    public static void DisableThrottling(Process process) => Set(process, on: false);

    /// <summary>
    /// The opposite, for when using as little power as possible IS the point (the AFK preset): puts
    /// the process into EcoQoS - "Efficiency mode" in Task Manager - so Windows runs it on its most
    /// power-efficient cores and clock speeds.
    /// </summary>
    public static void EnableThrottling(Process process) => Set(process, on: true);

    private static void Set(Process process, bool on)
    {
        var what = on ? "enable" : "disable";
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = CurrentVersion,
                ControlMask = ExecutionSpeed,
                StateMask = on ? ExecutionSpeed : 0, // explicit either way, not "let the system decide"
            };

            var size = (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>();
            if (Native.SetProcessInformation(process.Handle, ProcessPowerThrottlingClass, ref state, size))
                Log.Info(on
                    ? $"Put PID {process.Id} into efficiency mode (EcoQoS) to save power."
                    : $"Disabled power throttling for PID {process.Id} (Roblox will not be put into EcoQoS/efficiency mode).");
            else
                Log.Warn($"Could not {what} power throttling for PID {process.Id}: Win32 error {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not {what} power throttling for PID {process.Id}: {ex.Message}");
        }
    }
}
