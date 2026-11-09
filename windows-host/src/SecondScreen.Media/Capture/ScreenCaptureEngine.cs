using System.Drawing;

namespace SecondScreen.Media.Capture;

public sealed class ScreenCaptureEngine : IDisposable
{
    private readonly DxgiScreenCapture _dxgi = new();
    private readonly GdiScreenCapture _gdi = new();
    private bool _useGdi;

    public int Width => _useGdi ? _gdi.Width : _dxgi.Width;
    public int Height => _useGdi ? _gdi.Height : _dxgi.Height;
    public bool IsInitialized => _useGdi ? _gdi.IsInitialized : _dxgi.IsInitialized;

    public bool Initialize(string? targetDeviceName, Rectangle targetBounds)
    {
        // 1. Try DXGI hardware capture first
        if (!string.IsNullOrEmpty(targetDeviceName))
        {
            try
            {
                if (_dxgi.Initialize(targetDeviceName))
                {
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

        // 2. High-performance GDI fallback (bulletproof across hybrid GPUs, virtual screens, IddCx)
        if (_gdi.Initialize(targetBounds, targetDeviceName))
        {
            _useGdi = true;
            Console.WriteLine($"[ScreenCapture] Yüksek Hızlı GDI Yakalama devrede: {targetBounds.X},{targetBounds.Y} {targetBounds.Width}x{targetBounds.Height}");
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

        return _dxgi.AcquireNextFrame(timeoutMs);
    }

    public void Dispose()
    {
        _dxgi.Dispose();
        _gdi.Dispose();
    }
}

