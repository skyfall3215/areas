using SecondScreen.Core.Diagnostics;
using Xunit;

namespace SecondScreen.Tests;

public class RollingLatencyTrackerTests
{
    [Fact]
    public void GetStats_EmptyTracker_ReturnsZeros()
    {
        var tracker = new RollingLatencyTracker(10);
        var stats = tracker.GetStats();

        Assert.Equal(0, stats.SampleCount);
        Assert.Equal(0.0, stats.Average);
        Assert.Equal(0.0, stats.P50);
        Assert.Equal(0.0, stats.P95);
        Assert.Equal(0.0, stats.Min);
        Assert.Equal(0.0, stats.Max);
    }

    [Fact]
    public void GetStats_KnownValues_ComputesAccurateMetrics()
    {
        var tracker = new RollingLatencyTracker(100);
        // Add values 1 to 100
        for (int i = 1; i <= 100; i++)
        {
            tracker.AddSample(i);
        }

        var stats = tracker.GetStats();
        Assert.Equal(100, stats.SampleCount);
        Assert.Equal(50.5, stats.Average, precision: 2);
        Assert.Equal(1.0, stats.Min);
        Assert.Equal(100.0, stats.Max);
        Assert.Equal(50.0, stats.P50);
        Assert.Equal(95.0, stats.P95);
    }

    [Fact]
    public void AddSample_ExceedingCapacity_RollsOverOldestValues()
    {
        var tracker = new RollingLatencyTracker(3);
        tracker.AddSample(10.0);
        tracker.AddSample(20.0);
        tracker.AddSample(30.0);
        tracker.AddSample(40.0); // 10 is replaced with 40

        var stats = tracker.GetStats();
        Assert.Equal(3, stats.SampleCount);
        Assert.Equal(20.0, stats.Min);
        Assert.Equal(40.0, stats.Max);
        Assert.Equal(30.0, stats.Average, precision: 2);
    }
}

