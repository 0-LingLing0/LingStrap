using System;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace Lingstrap.Services;

/// <summary>
/// Measures round-trip time to the current game server via a plain ICMP echo (System.Net's Ping) -
/// unlike RobloxFpsTracker's ETW session, this needs no administrator rights at all, so a ping-only
/// overlay never has to go through the FPS overlay's elevation setup. This is an approximation, not
/// Roblox's own reported ping: Roblox measures round-trip time over its own UDP game protocol, while
/// this measures a plain ICMP echo to the same server IP - usually close, but not guaranteed
/// identical, and some server hosts firewall ICMP entirely, which would show as a permanent timeout
/// even though the actual game connection is fine.
/// </summary>
public sealed class RobloxPingTracker : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private const int TimeoutMs = 1000;

    private readonly string _targetIp;
    private Timer? _timer;
    private Ping? _ping;
    private bool _pinging;

    /// <summary>Milliseconds, or null if the last attempt timed out/failed (e.g. ICMP blocked by
    /// the server's host).</summary>
    public event Action<long?>? PingUpdated;

    public RobloxPingTracker(string targetIp)
    {
        _targetIp = targetIp;
    }

    public void Start()
    {
        _ping = new Ping();
        _timer = new Timer(_ => _ = PingOnceAsync(), null, TimeSpan.Zero, Interval);
    }

    private async Task PingOnceAsync()
    {
        // Skip this tick rather than let pings queue up if one is already slow/hanging.
        if (_pinging || _ping is null) return;
        _pinging = true;

        try
        {
            var reply = await _ping.SendPingAsync(_targetIp, TimeoutMs);
            if (reply.Status == IPStatus.Success)
            {
                Log.Info($"Ping overlay: {_targetIp} replied in {reply.RoundtripTime}ms.");
                PingUpdated?.Invoke(reply.RoundtripTime);
            }
            else
            {
                Log.Info($"Ping overlay: {_targetIp} did not reply (status={reply.Status}).");
                PingUpdated?.Invoke(null);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Ping overlay: could not ping {_targetIp}: {ex.Message}");
            PingUpdated?.Invoke(null);
        }
        finally
        {
            _pinging = false;
        }
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _ping?.Dispose();
        _ping = null;
    }

    public void Dispose() => Stop();
}
