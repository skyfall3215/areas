using SecondScreen.Core.Diagnostics;
using SecondScreen.Core.Protocol;
using Xunit;

namespace SecondScreen.Tests;

public class RttTrackerTests
{
    [Fact]
    public void PingMessage_And_PongMessage_RoundTripSerialization()
    {
        var ping = new PingMessage(PingId: 42, OriginTimeUs: 1_234_567);
        Span<byte> pingPayload = stackalloc byte[PingMessage.PayloadSize];
        ping.WritePayloadTo(pingPayload);

        var deserializedPing = PingMessage.ReadPayloadFrom(pingPayload);
        Assert.Equal(ping.PingId, deserializedPing.PingId);
        Assert.Equal(ping.OriginTimeUs, deserializedPing.OriginTimeUs);

        var pong = new PongMessage(PingId: 42, OriginTimeUs: 1_234_567);
        Span<byte> pongPayload = stackalloc byte[PongMessage.PayloadSize];
        pong.WritePayloadTo(pongPayload);

        var deserializedPong = PongMessage.ReadPayloadFrom(pongPayload);
        Assert.Equal(pong.PingId, deserializedPong.PingId);
        Assert.Equal(pong.OriginTimeUs, deserializedPong.OriginTimeUs);
    }

    [Fact]
    public void RttTracker_Simulated20MsRoundTrip_CalculatesExpectedLatency()
    {
        var tracker = new RttTracker();
        var ping = tracker.CreatePing();

        // Simulate 20,000 microseconds (20 ms) network round-trip time
        uint receiveTimeUs = ping.OriginTimeUs + 20_000;

        var pong = tracker.CreatePong(ping);
        bool recorded = tracker.RecordPong(pong, receiveTimeUs);

        Assert.True(recorded);
        Assert.Equal(20.0, tracker.LastRttMs, precision: 2);
        Assert.Equal(10.0, tracker.LastOneWayMs, precision: 2);

        var stats = tracker.RttStats.GetStats();
        Assert.Equal(1, stats.SampleCount);
        Assert.Equal(20.0, stats.Average, precision: 2);
    }
}

