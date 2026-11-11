using System.Drawing;

namespace SecondScreen.Media.Capture;

public sealed class ScreenCaptureEngine : IDisposable
{
    private readonly DxgiScreenCapture _dxgi = new();
    private readonly GdiScreenCapture _gdi = new();
    private bool _useGdi;
    private bool _isInitialized;

    private Rectangle _targetBounds;
    private string? _targetDeviceName;

    public int Width => !_useGdi && _dxgi.Width > 0 ? _dxgi.Width : (_gdi.Width > 0 ? _gdi.Width : (_targetBounds.Width & ~1));
    public int Height => !_useGdi && _dxgi.Height > 0 ? _dxgi.Height : (_gdi.Height > 0 ? _gdi.Height : (_targetBounds.Height & ~1));
    public bool IsInitialized => _isInitialized;

    public bool Initialize(string? targetDeviceName, Rectangle targetBounds)
    {
        GdiScreenCapture.EnsureThreadDesktopAttached();
        _targetDeviceName = targetDeviceName;
        _targetBounds = targetBounds;

        // Always initialize GDI as an instant fallback if DXGI is warming up
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
                    _isInitialized = true;
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
            _isInitialized = true;
            Console.WriteLine($"[ScreenCapture] GDI Yakalama devrede: {targetBounds.X},{targetBounds.Y} {targetBounds.Width}x{targetBounds.Height}");
            return true;
        }

        _isInitialized = false;
        return false;
    }

    public CapturedFrame? AcquireNextFrame(int timeoutMs = 0)
    {
        try
        {
            if (_useGdi)
            {
                return _gdi.AcquireNextFrame();
            }

            var dxgiFrame = _dxgi.AcquireNextFrame(timeoutMs);
            if (dxgiFrame != null)
            {
                return dxgiFrame;
            }

            // Fallback to GDI only if DXGI hasn't acquired its very first surface yet
            if (_gdi.IsInitialized)
            {
                return _gdi.AcquireNextFrame();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ScreenCapture] Kare yakalama istisnası: {ex.Message}");
        }

        return null;
    }

    public void SimulateDesktopSwitchOrUac()
    {
        _dxgi.ReleaseDuplicationObjects();
    }

    public void Dispose()
    {
        _isInitialized = false;
        _dxgi.Dispose();
        _gdi.Dispose();
    }
}
