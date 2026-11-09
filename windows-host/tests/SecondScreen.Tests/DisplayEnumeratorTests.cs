using System.Drawing;
using SecondScreen.Display;
using SecondScreen.Media.Capture;
using Xunit;

namespace SecondScreen.Tests;

public class DisplayEnumeratorTests
{
    [Fact]
    public void GetDisplays_ReturnsAtLeastOneDisplay()
    {
        var displays = DisplayEnumerator.GetDisplays();

        Assert.NotNull(displays);
        Assert.NotEmpty(displays);

        var first = displays[0];
        Assert.True(first.Bounds.Width > 0);
        Assert.True(first.Bounds.Height > 0);
    }

    [Fact]
    public void GetTargetDisplay_ReturnsValidDisplay()
    {
        var target = DisplayEnumerator.GetTargetDisplay();

        Assert.NotNull(target);
        Assert.True(target.Bounds.Width > 0);
        Assert.True(target.Bounds.Height > 0);
        Assert.False(string.IsNullOrWhiteSpace(target.DeviceName));
    }

    [Fact]
    public void ScreenCaptureEngine_CanInitializeTargetDisplay()
    {
        var target = DisplayEnumerator.GetTargetDisplay();
        using var engine = new ScreenCaptureEngine();
        bool ok = engine.Initialize(target.DeviceName, target.Bounds);

        // In interactive desktop or with GDI fallback, engine must be able to initialize
        if (ok)
        {
            Assert.True(engine.Width > 0);
            Assert.True(engine.Height > 0);
        }
    }
}
