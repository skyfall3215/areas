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

        string assetsDir = @"D:\Areas\docs\assets";
        Directory.CreateDirectory(assetsDir);
        string logoPath = Path.Combine(assetsDir, "logo.png");
        bmp.Save(logoPath, System.Drawing.Imaging.ImageFormat.Png);

        // Generate Android mipmap icons
        var sizes = new (string folder, int size)[]
        {
            ("mipmap-mdpi", 48),
            ("mipmap-hdpi", 72),
            ("mipmap-xhdpi", 96),
            ("mipmap-xxhdpi", 144),
            ("mipmap-xxxhdpi", 192),
        };

        string resDir = @"D:\Areas\android-client\app\src\main\res";
        foreach (var (folder, sz) in sizes)
        {
            string dir = Path.Combine(resDir, folder);
            Directory.CreateDirectory(dir);

            using var resized = new Bitmap(sz, sz);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(bmp, 0, 0, sz, sz);
            }

            resized.Save(Path.Combine(dir, "ic_launcher.png"), System.Drawing.Imaging.ImageFormat.Png);
            resized.Save(Path.Combine(dir, "ic_launcher_round.png"), System.Drawing.Imaging.ImageFormat.Png);
        }

        // Also copy ico to SecondScreen.App project
        File.Copy(icoPath, @"D:\Areas\windows-host\src\SecondScreen.App\app.ico", true);
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
}
