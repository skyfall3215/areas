using System.Drawing;

namespace SecondScreen.Media.Capture;

public sealed class ScreenCaptureEngine : IDisposable
{
    private readonly DxgiScreenCapture _dxgi = new();
    private readonly GdiScreenCapture _gdi = new();
    private bool _useGdi;

    private Rectangle _targetBounds;
    private string? _targetDeviceName;
    private int _dxgiNullStreak;

    public int Width => _useGdi ? _gdi.Width : _dxgi.Width;
    public int Height => _useGdi ? _gdi.Height : _dxgi.Height;
    public bool IsInitialized => _useGdi ? _gdi.IsInitialized : _dxgi.IsInitialized;

    public bool Initialize(string? targetDeviceName, Rectangle targetBounds)
    {
        GdiScreenCapture.EnsureThreadDesktopAttached();
        _targetDeviceName = targetDeviceName;
        _targetBounds = targetBounds;
        _dxgiNullStreak = 0;

        // Always initialize GDI as an instant fallback if DXGI is warming up or loses access
        bool gdiOk = _gdi.Initialize(targetBounds, targetDeviceName);

        // 1. Hardware DXGI Output Duplication first (~0.05-1.5 ms capture latency, works on IddCx virtual displays and hybrid GPUs, with native cursor overlay)
        if (!string.IsNullOrEmpty(targetDeviceName))
        {
            try
            {
                if (_dxgi.Initialize(targetDeviceName))
                {
                    // Prime initial desktop frame so immediate 0ms queries succeed
                    _dxgi.AcquireNextFrame(50);
                    _dxgi.AcquireNextFrame(50);
                    _useGdi = false;
                    Console.WriteLine($"[ScreenCapture] Donanımsal DXGI Yakalama devrede: {targetDeviceName} ({_dxgi.Width}x{_dxgi.Height})");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenCapture] DXGI denenirken istisna: {ex.Message}");
            }
        }

        // 2. GDI capture fallback
        if (gdiOk)
        {
            _useGdi = true;
            Console.WriteLine($"[ScreenCapture] GDI Yakalama devrede: {targetBounds.X},{targetBounds.Y} {targetBounds.Width}x{targetBounds.Height}");
            return true;
        }

        return false;
    }

    public CapturedFrame? AcquireNextFrame(int timeoutMs = 0)
    {
        if (_useGdi)
        {
            return _gdi.AcquireNextFrame();
        }

        var dxgiFrame = _dxgi.AcquireNextFrame(timeoutMs);
        if (dxgiFrame != null)
        {
            _dxgiNullStreak = 0;
            return dxgiFrame;
        }

        // If DXGI hasn't produced a composited desktop frame yet (e.g. during DWM ExtendDesktop transition),
        // seamlessly fall back to GDI so we never send synthetic test patterns or black frames
        _dxgiNullStreak++;
        if (_gdi.IsInitialized)
        {
            return _gdi.AcquireNextFrame();
        }

        return null;
    }

    public void Dispose()
    {
        _dxgi.Dispose();
        _gdi.Dispose();
    }
}

