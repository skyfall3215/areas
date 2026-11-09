using System.Drawing;
using SecondScreen.Input;
using Xunit;

namespace SecondScreen.Tests;

public class TouchCoordinateMapperTests
{
    [Fact]
    public void NormalizeToScreen_MapsCornersAccurately()
    {
        var screen = new Rectangle(0, 0, 1920, 1080);

        // Top-Left (0, 0)
        var pTopLeft = TouchCoordinateMapper.NormalizeToScreen(0, 0, screen);
        Assert.Equal(0, pTopLeft.X);
        Assert.Equal(0, pTopLeft.Y);

        // Bottom-Right (65535, 65535)
        var pBottomRight = TouchCoordinateMapper.NormalizeToScreen(65535, 65535, screen);
        Assert.Equal(1919, pBottomRight.X);
        Assert.Equal(1079, pBottomRight.Y);

        // Center (~32767, ~32767)
        var pCenter = TouchCoordinateMapper.NormalizeToScreen(32768, 32768, screen);
        Assert.True(Math.Abs(pCenter.X - 960) <= 1);
        Assert.True(Math.Abs(pCenter.Y - 540) <= 1);
    }

    [Fact]
    public void NormalizeToScreen_SupportsVirtualDesktopOffsets()
    {
        // Second monitor positioned to the right of primary monitor (X=1920..3840, Y=0..1200)
        var secondaryScreen = new Rectangle(1920, 0, 1920, 1200);

        var pTopLeft = TouchCoordinateMapper.NormalizeToScreen(0, 0, secondaryScreen);
        Assert.Equal(1920, pTopLeft.X);
        Assert.Equal(0, pTopLeft.Y);

        var pBottomRight = TouchCoordinateMapper.NormalizeToScreen(65535, 65535, secondaryScreen);
        Assert.Equal(1920 + 1919, pBottomRight.X);
        Assert.Equal(1199, pBottomRight.Y);
    }

    [Fact]
    public void NormalizeToScreen_EmptyBoundsReturnsEmpty()
    {
        var empty = new Rectangle(0, 0, 0, 0);
        var p = TouchCoordinateMapper.NormalizeToScreen(32768, 32768, empty);
        Assert.Equal(Point.Empty, p);
    }
}

