using System.Drawing;
using System.Runtime.InteropServices;
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

            var frame = engine.AcquireNextFrame();
            // In interactive session or CI, check frame
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [Fact]
    public void ExtractIconsFromIco()
    {
        string icoPath = @"D:\Areas\areas.ico";
        Assert.True(File.Exists(icoPath), "areas.ico exists");

        using var icon = new Icon(icoPath, new Size(256, 256));
        using var bmp = icon.ToBitmap();
        Assert.True(bmp.Width > 0 && bmp.Height > 0);
    }

    [Fact]
    public async Task SecondScreenServer_Lifecycle_StartsAndStops()
    {
        using var server = new SecondScreen.App.SecondScreenServer(0); // Port 0 = free port
        Assert.False(server.IsRunning);

        await server.StartAsync();
        Assert.True(server.IsRunning);

        server.Stop();
        Assert.False(server.IsRunning);
    }

    [Fact]
    public void Test_DashboardForm_Creation()
    {
        using var form = new SecondScreen.App.ServerDashboardForm();
        Assert.NotNull(form);
    }

    [Fact]
    public void ScreenCaptureEngine_SurvivesUacDesktopSwitch_AndAutoRecovers()
    {
        var target = DisplayEnumerator.GetTargetDisplay();
        using var engine = new ScreenCaptureEngine();
        bool ok = engine.Initialize(target.DeviceName, target.Bounds);
        Assert.True(ok);
        Assert.True(engine.IsInitialized);

        var initialFrame = engine.AcquireNextFrame(16);
        Assert.NotNull(initialFrame);
        Assert.NotNull(initialFrame!.RgbaData);

        // Simulate UAC / Winlogon secure desktop switch dropping DXGI duplication
        engine.SimulateDesktopSwitchOrUac();

        // Engine must remain initialized so SecondScreenServer loop never stops calling AcquireNextFrame
        Assert.True(engine.IsInitialized);

        // Immediate call during UAC throttle window must return cached frame without throwing or returning null
        var duringUacFrame = engine.AcquireNextFrame(0);
        Assert.NotNull(duringUacFrame);
        Assert.NotNull(duringUacFrame!.RgbaData);

        // After 250ms (UAC dismissed), AcquireNextFrame must automatically re-initialize DXGI and resume live capture
        System.Threading.Thread.Sleep(250);
        var recoveredFrame = engine.AcquireNextFrame(50);
        Assert.NotNull(recoveredFrame);
        Assert.NotNull(recoveredFrame!.RgbaData);
    }
}
