using System.Diagnostics;
using SecondScreen.Core.Protocol;

namespace SecondScreen.Core.Diagnostics;

public sealed class RttTracker
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly RollingLatencyTracker _rttStats = new(120);
    private readonly RollingLatencyTracker _oneWayStats = new(120);
    private uint _nextPingId = 1;
    private double _lastRttMs;
    private double _lastOneWayMs;

    public uint CurrentTimeUs => (uint)((_stopwatch.ElapsedTicks * 1_000_000) / Stopwatch.Frequency);

    public double LastRttMs => _lastRttMs;
    public double LastOneWayMs => _lastOneWayMs;

    public RollingLatencyTracker RttStats => _rttStats;
    public RollingLatencyTracker OneWayStats => _oneWayStats;

    public PingMessage CreatePing()
    {
        uint pingId = Interlocked.Increment(ref _nextPingId);
        uint timeUs = CurrentTimeUs;
        return new PingMessage(pingId, timeUs);
    }

    public PongMessage CreatePong(PingMessage ping)
    {
        return new PongMessage(ping.PingId, ping.OriginTimeUs);
    }

    public bool RecordPong(PongMessage pong, uint receiveTimeUs)
    {
        long diffUs = (long)receiveTimeUs - pong.OriginTimeUs;
        if (diffUs < 0)
        {
            // Clock wraparound or invalid time
            return false;
        }

        double rttMs = diffUs / 1000.0;
        double oneWayMs = rttMs / 2.0;

        _lastRttMs = rttMs;
        _lastOneWayMs = oneWayMs;

        _rttStats.AddSample(rttMs);
        _oneWayStats.AddSample(oneWayMs);

        return true;
    }
}

