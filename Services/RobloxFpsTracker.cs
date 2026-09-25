using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Lingstrap.Services;

/// <summary>
/// Measures a process's real frame rate from outside it, the same non-invasive way tools like
/// PresentMon/RTSS do: listening to the OS's own event-tracing (ETW) record of every DXGI
/// Present call, rather than reading the process's memory or injecting into it. This is why it
/// needs administrator rights - creating a real-time ETW session does, even though nothing here
/// touches the target process itself.
/// </summary>
public sealed class RobloxFpsTracker : IDisposable
{
    private const string SessionName = "LingstrapFpsSession";
    private static readonly Guid DxgiProviderGuid = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");

    // Best guess so far at the one event that fires exactly once per actually-displayed frame - a
    // live capture showed several distinct present-related events per real Present() call
    // ("IDXGISwapChain_Present/Start" among them), and matching more than one of them at once was
    // roughly doubling the real number. "Present/Start" is DXGI's own lower-level runtime task
    // rather than a specific API entry point, so it should fire the same way regardless of whether
    // the app calls the legacy Present() or the newer Present1() - but this is still a best guess,
    // not a confirmed one; see PresentLikeEventCounts below for how to actually confirm or correct
    // it from a real capture.
    private const string PresentEventName = "Present/Start";

    // How often the count is actually reported, and so how often the overlay redraws. This was 16ms
    // - 60 updates a second - on the reasoning that a 60Hz display can show that many. It can, but
    // nobody can READ them: a frame-rate number changing 60 times a second is an unreadable blur,
    // and every one of those updates costs a full Direct2D redraw plus an UpdateLayeredWindow blit
    // over the running game. Four a second is what RTSS and Steam's own counters settle on, it's
    // legible, and it does roughly a twelfth of the drawing work.
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);

    // How far back "the current FPS" looks. Widened along with the report interval above: at four
    // updates a second there's no responsiveness left to win from a 200ms window, and a longer one
    // averages out the single-frame spikes that made the number jitter. The count is normalized by
    // the window's real elapsed span rather than assumed to equal this exactly.
    private static readonly TimeSpan WindowSize = TimeSpan.FromMilliseconds(500);

    private readonly int _targetProcessId;
    private readonly object _lock = new();
    private readonly Queue<DateTime> _presentTimes = new();
    private TraceEventSession? _session;
    private Task? _processingTask;

    private DateTime _lastReportTime = DateTime.MinValue;

    public event Action<double>? FpsUpdated;

    public RobloxFpsTracker(int targetProcessId)
    {
        _targetProcessId = targetProcessId;
    }

    public void Start()
    {
        // A previous run that didn't shut down cleanly (crash, kill) can leave a session with this
        // name behind, which would otherwise make creating a new one here fail.
        try { new TraceEventSession(SessionName) { StopOnDispose = true }.Dispose(); } catch { /* nothing to clean up */ }

        _session = new TraceEventSession(SessionName) { StopOnDispose = true };
        _session.EnableProvider(DxgiProviderGuid);

        _processingTask = Task.Run(() =>
        {
            try
            {
                _session.Source.Dynamic.All += OnEvent;
                _session.Source.Process(); // blocks this task until Stop() is called
            }
            catch (Exception ex)
            {
                Log.Warn($"FPS overlay: ETW session ended unexpectedly ({ex.Message}).");
            }
        });
    }

    /// <summary>
    /// Runs for EVERY DXGI event on the machine, from every process - thousands a second while any
    /// game is rendering. Everything here is multiplied by that rate, so the two cheapest
    /// discriminators come first and nothing whatsoever happens above them.
    ///
    /// This used to, on every single event: take a lock to record whether it had seen that event
    /// name before, run a case-insensitive substring scan for "Present", take a second lock to keep
    /// per-name counters, and convert the timestamp - all before the one comparison that rejects
    /// the ~99% of events it doesn't want. That work was the FPS overlay's own cost to the frame
    /// rate it was measuring.
    /// </summary>
    private void OnEvent(TraceEvent data)
    {
        if (data.ProcessID != _targetProcessId) return;
        if (data.EventName != PresentEventName) return;

        // The event's OWN timestamp (when it actually happened), not DateTime.UtcNow (when this
        // callback happens to run). A real-time ETW session delivers events in periodic buffered
        // flushes, not one at a time as they occur - a live capture showed the computed rate swinging
        // wildly between roughly 210 and 480 every 2 seconds while Roblox's own counter sat steady
        // near 340, which is exactly what happens if you time frames by receipt: a batch of several
        // events delivered almost simultaneously looks like a burst of near-zero deltas, followed by
        // a gap until the next flush that looks like one huge delta. TimeStamp sidesteps that
        // entirely since it's stamped by the OS at the moment the event actually happened.
        var eventTime = data.TimeStamp;

        double? toReport = null;
        lock (_lock)
        {
            _presentTimes.Enqueue(eventTime);
            while (_presentTimes.Count > 0 && eventTime - _presentTimes.Peek() > WindowSize)
                _presentTimes.Dequeue();

            if (eventTime - _lastReportTime >= ReportInterval)
            {
                _lastReportTime = eventTime;
                // Normalized by the window's actual elapsed span rather than assumed to equal
                // WindowSize exactly - matters more now that the window's short enough that its real
                // span can meaningfully undershoot the nominal size (fewer presents landed in it than
                // "the window's full duration" would imply, e.g. right after a stutter).
                if (_presentTimes.Count >= 2)
                {
                    var span = (eventTime - _presentTimes.Peek()).TotalSeconds;
                    toReport = span > 0 ? (_presentTimes.Count - 1) / span : _presentTimes.Count;
                }
                else
                {
                    toReport = _presentTimes.Count;
                }
            }
        }

        if (toReport is { } fps)
            FpsUpdated?.Invoke(fps);
    }

    public void Stop()
    {
        try { _session?.Stop(); } catch (Exception ex) { Log.Warn($"FPS overlay: error stopping ETW session: {ex.Message}"); }
        _session?.Dispose();
        _session = null;
    }

    public void Dispose() => Stop();
}
