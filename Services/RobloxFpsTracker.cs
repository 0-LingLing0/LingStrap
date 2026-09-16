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

    // How often the count is actually reported. 16ms is roughly a 60Hz display's own refresh
    // interval - updating meaningfully faster than the monitor can actually show a new image is
    // pure wasted work with zero visible benefit, so this is close to the practical ceiling for
    // "faster" to actually mean anything.
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(16);

    // How far back "the current FPS" looks. Shorter than the standard 1-second window most FPS
    // counters use, again for faster reaction to an actual change - the count is normalized by the
    // window's real elapsed span (not assumed to be exactly this), so shortening it doesn't throw
    // off the math, just trades some smoothness for responsiveness.
    private static readonly TimeSpan WindowSize = TimeSpan.FromMilliseconds(200);

    // Every distinct event name containing "Present" gets its own running count, dumped to the log
    // every 2 seconds as an occurrences-per-second rate. Whichever one's rate actually matches
    // Roblox's own on-screen FPS counter (Shift+F5) is the real one-per-frame event - this turns
    // "guess an event name, ship it, wait for someone to test with a real game running" into reading
    // one log file, since PresentEventName above has already needed correcting once.
    private static readonly TimeSpan DiagnosticInterval = TimeSpan.FromSeconds(2);

    private readonly int _targetProcessId;
    private readonly object _lock = new();
    private readonly HashSet<string> _seenEventNames = new();
    private readonly Dictionary<string, int> _presentLikeCounts = new();
    private readonly Queue<DateTime> _presentTimes = new();
    private DateTime? _diagnosticWindowStart;
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

    private void OnEvent(TraceEvent data)
    {
        if (data.ProcessID != _targetProcessId) return;

        var name = data.EventName;
        if (name == null) return;

        // The event's OWN timestamp (when it actually happened), not DateTime.UtcNow (when this
        // callback happens to run). A real-time ETW session delivers events in periodic buffered
        // flushes, not one at a time as they occur - a live capture showed the computed rate swinging
        // wildly between roughly 210 and 480 every 2 seconds while Roblox's own counter sat steady
        // near 340, which is exactly what happens if you time frames by receipt: a batch of several
        // events delivered almost simultaneously looks like a burst of near-zero deltas, followed by
        // a gap until the next flush that looks like one huge delta. TimeStamp sidesteps that
        // entirely since it's stamped by the OS at the moment the event actually happened.
        var eventTime = data.TimeStamp;

        lock (_seenEventNames)
        {
            if (_seenEventNames.Add(name))
                Log.Info($"FPS overlay: saw DXGI event \"{name}\" for pid {_targetProcessId}.");
        }

        if (name.Contains("Present", StringComparison.OrdinalIgnoreCase))
            TrackDiagnosticRate(name, eventTime);

        if (name != PresentEventName) return;

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

    /// <summary>Logs occurrences-per-second for every "Present"-ish event name every 2 seconds, so a
    /// live capture directly shows which one actually fires once per real frame instead of needing
    /// another guess to be shipped and tested. Bucketed by the events' own timestamps, same reasoning
    /// as OnEvent above - otherwise this would show the same delivery-burst artifacts that made the
    /// very capture used to pick PresentEventName look unstable in the first place.</summary>
    private void TrackDiagnosticRate(string name, DateTime eventTime)
    {
        List<(string Name, int Count)>? toLog = null;
        double elapsedSeconds = 0;

        lock (_presentLikeCounts)
        {
            _presentLikeCounts[name] = _presentLikeCounts.GetValueOrDefault(name) + 1;
            _diagnosticWindowStart ??= eventTime;

            elapsedSeconds = (eventTime - _diagnosticWindowStart.Value).TotalSeconds;
            if (elapsedSeconds < DiagnosticInterval.TotalSeconds) return;

            toLog = _presentLikeCounts.Select(kv => (kv.Key, kv.Value)).OrderByDescending(x => x.Value).ToList();
            _presentLikeCounts.Clear();
            _diagnosticWindowStart = eventTime;
        }

        var summary = string.Join(", ", toLog.Select(x => $"{x.Name}={Math.Round(x.Count / elapsedSeconds, 1)}/s"));
        Log.Info($"FPS overlay: present-like event rates over {elapsedSeconds:0.#}s - {summary}");
    }

    public void Stop()
    {
        try { _session?.Stop(); } catch (Exception ex) { Log.Warn($"FPS overlay: error stopping ETW session: {ex.Message}"); }
        _session?.Dispose();
        _session = null;
    }

    public void Dispose() => Stop();
}
