using System.Drawing;
using System.Runtime.InteropServices;

namespace SecondScreen.Media.Capture;

public sealed class GdiScreenCapture : IDisposable
{
    private const int DIB_RGB_COLORS = 0;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    private const int CURSOR_SHOWING = 0x00000001;
    private const uint DI_NORMAL = 0x0003;

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(out CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(
        IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyWidth,
        uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BITMAPINFO pbmi,
        uint iUsage,
        out IntPtr ppvBits,
        IntPtr hSection,
        uint dwOffset);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    private IntPtr _hdcDesktop;
    private IntPtr _hdcMem;
    private IntPtr _hBitmap;
    private IntPtr _hOldBmp;
    private IntPtr _pBits;
    private int _x;
    private int _y;
    private int _width;
    private int _height;
    private byte[]? _buffer;
    private bool _isInitialized;

    public int Width => _width;
    public int Height => _height;
    public bool IsInitialized => _isInitialized;

    public bool Initialize(Rectangle bounds)
    {
        Dispose();

        _x = bounds.X;
        _y = bounds.Y;
        _width = bounds.Width & ~1;   // Ensure even width for H.264
        _height = bounds.Height & ~1; // Ensure even height for H.264

        if (_width <= 0 || _height <= 0) return false;

        _hdcDesktop = GetDC(IntPtr.Zero);
        if (_hdcDesktop == IntPtr.Zero) return false;

        _hdcMem = CreateCompatibleDC(_hdcDesktop);
        if (_hdcMem == IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, _hdcDesktop);
            _hdcDesktop = IntPtr.Zero;
            return false;
        }

        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = _width;
        bmi.bmiHeader.biHeight = -_height; // Negative = top-down DIB (matches video order)
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0; // BI_RGB

        _hBitmap = CreateDIBSection(_hdcMem, ref bmi, DIB_RGB_COLORS, out _pBits, IntPtr.Zero, 0);
        if (_hBitmap == IntPtr.Zero || _pBits == IntPtr.Zero)
        {
            Dispose();
            return false;
        }

        _hOldBmp = SelectObject(_hdcMem, _hBitmap);
        _buffer = new byte[_width * _height * 4];
        _isInitialized = true;

        Console.WriteLine($"[GdiScreenCapture] Hazır: {_x},{_y} {_width}x{_height}");
        return true;
    }

    public CapturedFrame? AcquireNextFrame()
    {
        if (!_isInitialized || _buffer == null || _pBits == IntPtr.Zero) return null;

        // Blit from target screen coordinates directly to DIB section
        bool success = BitBlt(_hdcMem, 0, 0, _width, _height, _hdcDesktop, _x, _y, SRCCOPY | CAPTUREBLT);
        if (!success)
        {
            // Try without CAPTUREBLT
            success = BitBlt(_hdcMem, 0, 0, _width, _height, _hdcDesktop, _x, _y, SRCCOPY);
        }

        if (!success) return null;

        // Overlay active Windows mouse cursor onto the captured frame
        DrawCursorIfOnScreen();

        int totalBytes = _width * _height * 4;
        Marshal.Copy(_pBits, _buffer, 0, totalBytes);

        return new CapturedFrame
        {
            RgbaData = _buffer,
            Width = _width,
            Height = _height,
            TimestampUs = DateTime.UtcNow.Ticks / 10,
            IsNew = true
        };
    }

    private void DrawCursorIfOnScreen()
    {
        var ci = new CURSORINFO();
        ci.cbSize = Marshal.SizeOf<CURSORINFO>();

        if (GetCursorInfo(out ci) && (ci.flags & CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero)
        {
            int relX = ci.ptScreenPos.x - _x;
            int relY = ci.ptScreenPos.y - _y;

            // Only draw if cursor is on or near this display
            if (relX >= -64 && relX <= _width && relY >= -64 && relY <= _height)
            {
                if (GetIconInfo(ci.hCursor, out var ii))
                {
                    int drawX = relX - ii.xHotspot;
                    int drawY = relY - ii.yHotspot;

                    DrawIconEx(
                        _hdcMem,
                        drawX,
                        drawY,
                        ci.hCursor,
                        0,
                        0,
                        0,
                        IntPtr.Zero,
                        DI_NORMAL);

                    if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                    if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                }
            }
        }
    }

    public void Dispose()
    {
        _isInitialized = false;

        if (_hOldBmp != IntPtr.Zero && _hdcMem != IntPtr.Zero)
        {
            SelectObject(_hdcMem, _hOldBmp);
            _hOldBmp = IntPtr.Zero;
        }

        if (_hBitmap != IntPtr.Zero)
        {
            DeleteObject(_hBitmap);
            _hBitmap = IntPtr.Zero;
        }

        if (_hdcMem != IntPtr.Zero)
        {
            DeleteDC(_hdcMem);
            _hdcMem = IntPtr.Zero;
        }

        if (_hdcDesktop != IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, _hdcDesktop);
            _hdcDesktop = IntPtr.Zero;
        }

        _pBits = IntPtr.Zero;
        _buffer = null;
    }
}

