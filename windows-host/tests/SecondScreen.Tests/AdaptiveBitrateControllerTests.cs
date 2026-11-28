using SecondScreen.Core.Diagnostics;
using Xunit;

namespace SecondScreen.Tests;

public class AdaptiveBitrateControllerTests
{
    private const double FrameInterval60FpsMs = 1000.0 / 60.0; // ~16.67 ms

    [Fact]
    public void HighSendDuration_ThreeConsecutiveSamples_TriggersMultiplicativeDecrease()
    {
        long nowMs = 0;
        var controller = new AdaptiveBitrateController(
            maxBitrateBps: 8_000_000,
            minBitrateBps: 2_000_000,
            clockMs: () => nowMs);

        // Two slow samples (> 1.1x interval = ~18.33 ms) should not trigger decrease yet
        controller.RecordSendSample(20.0, FrameInterval60FpsMs);
        nowMs += 17;
        controller.RecordSendSample(20.0, FrameInterval60FpsMs);
        Assert.False(controller.TryGetBitrateUpdate(out _));

        // Third consecutive slow sample triggers 0.75x decrease (8M -> 6M)
        nowMs += 17;
        controller.RecordSendSample(20.0, FrameInterval60FpsMs);
        Assert.True(controller.TryGetBitrateUpdate(out uint updated));
        Assert.Equal(6_000_000u, updated);

        // Second poll without further changes returns false
        Assert.False(controller.TryGetBitrateUpdate(out _));
    }

    [Fact]
    public void SevereSendSpike_SingleSampleOverDoubleInterval_TriggersImmediateDecrease()
    {
        long nowMs = 0;
        var controller = new AdaptiveBitrateController(
            maxBitrateBps: 10_000_000,
            minBitrateBps: 2_000_000,
            clockMs: () => nowMs);

        // > 2.0x interval (~33.33 ms) triggers immediately on a single sample
        controller.RecordSendSample(40.0, FrameInterval60FpsMs);
        Assert.True(controller.TryGetBitrateUpdate(out uint updated));
        Assert.Equal(7_500_000u, updated);
        Assert.Equal(40.0, controller.EmaSendDurationMs, precision: 2);
    }

    [Fact]
    public void HighRtt_Above40Ms_TriggersMultiplicativeDecrease()
    {
        long nowMs = 0;
        var controller = new AdaptiveBitrateController(
            maxBitrateBps: 8_000_000,
            minBitrateBps: 2_000_000,
            clockMs: () => nowMs);

        // RTT <= 40 ms is considered healthy
        controller.RecordRttSample(40.0);
        Assert.False(controller.TryGetBitrateUpdate(out _));

        // RTT > 40 ms triggers 0.75x decrease
        nowMs += 500;
        controller.RecordRttSample(45.5);
        Assert.True(controller.TryGetBitrateUpdate(out uint updated));
        Assert.Equal(6_000_000u, updated);
    }

    [Fact]
    public void RepeatedKeyframeRequests_WithinThreeSeconds_TriggerDecrease()
    {
        long nowMs = 0;
        var controller = new AdaptiveBitrateController(
            maxBitrateBps: 8_000_000,
            minBitrateBps: 2_000_000,
            clockMs: () => nowMs);

        // First request alone does not step down
        controller.OnKeyframeRequested();
        Assert.False(controller.TryGetBitrateUpdate(out _));

        // Request > 3000 ms later does not step down
        nowMs += 3_500;
        controller.OnKeyframeRequested();
        Assert.False(controller.TryGetBitrateUpdate(out _));

        // Second request within 3000 ms triggers step-down
        nowMs += 2_000;
        controller.OnKeyframeRequested();
        Assert.True(controller.TryGetBitrateUpdate(out uint updated));
        Assert.Equal(6_000_000u, updated);
    }

    [Fact]
    public void Cooldown_SuppressesRepeatedDecreasesWithin1500Ms()
    {
        long nowMs = 1_000;
        var controller = new AdaptiveBitrateController(
            maxBitrateBps: 8_000_000,
            minBitrateBps: 2_000_000,
            clockMs: () => nowMs);

        controller.RecordRttSample(60.0);
        Assert.True(controller.TryGetBitrateUpdate(out uint first));
        Assert.Equal(6_000_000u, first);

        // Within 1500 ms cooldown -> suppressed
        nowMs += 1_000;
        controller.RecordRttSample(80.0);
        controller.RecordSendSample(50.0, FrameInterval60FpsMs);
        Assert.False(controller.TryGetBitrateUpdate(out _));
        Assert.Equal(6_000_000u, controller.TargetBitrateBps);

        // Once 1500 ms have elapsed since first decrease -> allowed
        nowMs += 500;
        controller.RecordRttSample(80.0);
        Assert.True(controller.TryGetBitrateUpdate(out uint second));
        Assert.Equal(4_500_000u, second);
    }

    [Fact]
    public void FloorClamp_NeverDropsBelowMinBitrate()
    {
        long nowMs = 0;
        var controller = new AdaptiveBitrateController(
            maxBitrateBps: 8_000_000,
            minBitrateBps: 2_000_000,
            clockMs: () => nowMs);

        for (int i = 0; i < 10; i++)
        {
            nowMs += 2_000;
            controller.RecordRttSample(90.0);
        }

        Assert.True(controller.TryGetBitrateUpdate(out uint clamped));
        Assert.Equal(2_000_000u, clamped);

        // Further congestion stays clamped at floor
        nowMs += 2_000;
        controller.RecordRttSample(120.0);
        Assert.False(controller.TryGetBitrateUpdate(out _));
        Assert.Equal(2_000_000u, controller.TargetBitrateBps);
    }

    [Fact]
    public void AdditiveRecovery_IncreasesBy500KbpsAfter4000MsStable_AndClampsAtCeiling()
    {
        long nowMs = 0;
        var controller = new AdaptiveBitrateController(
            maxBitrateBps: 6_500_000,
            minBitrateBps: 2_000_000,
            initialBitrateBps: 5_500_000,
            clockMs: () => nowMs);

        // Before 4000 ms -> no recovery
        nowMs = 3_999;
        controller.RecordSendSample(5.0, FrameInterval60FpsMs);
        controller.RecordRttSample(12.0);
        Assert.False(controller.TryGetBitrateUpdate(out _));

        // At 4000 ms -> +500_000 bps (5.5M -> 6.0M)
        nowMs = 4_000;
        controller.RecordSendSample(5.0, FrameInterval60FpsMs);
        Assert.True(controller.TryGetBitrateUpdate(out uint recovered1));
        Assert.Equal(6_000_000u, recovered1);

        // Next 4000 ms -> +500_000 bps (6.0M -> 6.5M ceiling)
        nowMs = 8_000;
        controller.RecordSendSample(4.0, FrameInterval60FpsMs);
        Assert.True(controller.TryGetBitrateUpdate(out uint recovered2));
        Assert.Equal(6_500_000u, recovered2);

        // Further stable periods remain clamped at ceiling
        nowMs = 16_000;
        controller.RecordSendSample(4.0, FrameInterval60FpsMs);
        controller.RecordRttSample(10.0);
        Assert.False(controller.TryGetBitrateUpdate(out _));
        Assert.Equal(6_500_000u, controller.TargetBitrateBps);
    }

    [Fact]
    public void TryGetBitrateUpdate_RequiresAtLeast250KbpsDelta()
    {
        long nowMs = 0;
        // Max = 2_200_000, Min = 2_000_000 -> decrease from 2.2M clamps at 2.0M (delta = 200_000 < 250_000)
        var controller = new AdaptiveBitrateController(
            maxBitrateBps: 2_200_000,
            minBitrateBps: 2_000_000,
            clockMs: () => nowMs);

        controller.RecordRttSample(75.0);
        Assert.Equal(2_000_000u, controller.TargetBitrateBps);
        Assert.False(controller.TryGetBitrateUpdate(out uint unchanged));
        Assert.Equal(2_200_000u, unchanged);
    }

    [Fact]
    public void HighRtt_BlocksAdditiveRecoveryFromFastSends()
    {
        long nowMs = 0;
        var controller = new AdaptiveBitrateController(
            maxBitrateBps: 8_000_000,
            minBitrateBps: 2_000_000,
            clockMs: () => nowMs);

        // High RTT reduces bitrate to 6M
        controller.RecordRttSample(55.0);
        Assert.True(controller.TryGetBitrateUpdate(out uint reduced));
        Assert.Equal(6_000_000u, reduced);

        // Fast sends alone should not trigger recovery while last RTT is still > 40 ms
        nowMs = 5_000;
        controller.RecordSendSample(3.0, FrameInterval60FpsMs);
        Assert.False(controller.TryGetBitrateUpdate(out _));

        // Once RTT drops back <= 40 ms, recovery proceeds
        controller.RecordRttSample(18.0);
        Assert.True(controller.TryGetBitrateUpdate(out uint recovered));
        Assert.Equal(6_500_000u, recovered);
    }
}
