using System.Drawing;
using System.Runtime.InteropServices;

namespace SecondScreen.Media.Capture;

public sealed class GdiScreenCapture : IDisposable
{
    private const int DIB_RGB_COLORS = 0;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;
    private const int CURSOR_SHOWING = 0x00000001;
    private const uint DI_NORMAL = 0x0003;
    private const int IDC_ARROW = 32512;
    private const uint DESKTOP_ALL = 0x01FF;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorInfo(out CURSORINFO pci);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DrawIconEx(
        IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyWidth,
        uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CreateDC(string? lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

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

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(
        IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    private string? _deviceName;
    private bool _isDeviceDc;
    private IntPtr _hdcSrc;
    private IntPtr _hdcMem;
    private IntPtr _hBitmap;
    private IntPtr _hOldBmp;
    private IntPtr _pBits;
    private IntPtr _hDefaultCursor;
    private int _x;
    private int _y;
    private int _width;
    private int _height;
    private byte[]? _buffer;
    private bool _isInitialized;
    private bool _desktopAttached;

    public int Width => _width;
    public int Height => _height;
    public bool IsInitialized => _isInitialized;

    public bool Initialize(Rectangle bounds, string? deviceName = null)
    {
        Dispose();

        EnsureThreadDesktopAttached();

        _x = bounds.X;
        _y = bounds.Y;
        _width = bounds.Width & ~1;   // Ensure even width for H.264
        _height = bounds.Height & ~1; // Ensure even height for H.264
        _deviceName = deviceName;

        if (_width <= 0 || _height <= 0) return false;

        // Try direct display device DC first (e.g. \\.\DISPLAY24), fallback to primary DISPLAY
        _isDeviceDc = false;
        if (!string.IsNullOrEmpty(_deviceName))
        {
            _hdcSrc = CreateDC(_deviceName, null, null, IntPtr.Zero);
            _isDeviceDc = (_hdcSrc != IntPtr.Zero);
        }

        if (_hdcSrc == IntPtr.Zero)
        {
            _hdcSrc = CreateDC("DISPLAY", null, null, IntPtr.Zero);
            _isDeviceDc = false;
        }

        if (_hdcSrc == IntPtr.Zero)
        {
            _hdcSrc = GetDC(IntPtr.Zero);
            _isDeviceDc = false;
        }

        if (_hdcSrc == IntPtr.Zero) return false;

        _hdcMem = CreateCompatibleDC(_hdcSrc);
        if (_hdcMem == IntPtr.Zero)
        {
            Dispose();
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
        _hDefaultCursor = LoadCursor(IntPtr.Zero, IDC_ARROW);
        _isInitialized = true;

        Console.WriteLine($"[GdiScreenCapture] Hazır: {_deviceName ?? "VirtualScreen"} @ {_x},{_y} {_width}x{_height}");
        return true;
    }

    private void EnsureThreadDesktopAttached()
    {
        if (_desktopAttached) return;
        try
        {
            IntPtr hDesktop = OpenInputDesktop(0, false, DESKTOP_ALL);
            if (hDesktop != IntPtr.Zero)
            {
                SetThreadDesktop(hDesktop);
                CloseDesktop(hDesktop);
                _desktopAttached = true;
            }
        }
        catch
        {
            // Ignore if unable to switch desktop
        }
    }

    public CapturedFrame? AcquireNextFrame()
    {
        if (!_isInitialized || _buffer == null || _pBits == IntPtr.Zero || _hdcSrc == IntPtr.Zero) return null;

        // If using device-specific DC (e.g. \\.\DISPLAY24), source coordinates start at (0, 0)
        int srcX = _isDeviceDc ? 0 : _x;
        int srcY = _isDeviceDc ? 0 : _y;

        bool success = BitBlt(_hdcMem, 0, 0, _width, _height, _hdcSrc, srcX, srcY, SRCCOPY);
        if (!success)
        {
            // Re-acquire desktop DC if device DC was invalidated
            IntPtr hdcScreen = GetDC(IntPtr.Zero);
            if (hdcScreen != IntPtr.Zero)
            {
                success = BitBlt(_hdcMem, 0, 0, _width, _height, hdcScreen, _x, _y, SRCCOPY);
                ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }

        if (!success) return null;

        int totalBytes = _width * _height * 4;
        Marshal.Copy(_pBits, _buffer, 0, totalBytes);

        // Always overlay crisp High-DPI mouse cursor directly onto the output frame buffer
        if (GetCursorPos(out var pt))
        {
            DrawSoftwareCursor(_buffer, pt.x, pt.y);
        }

        return new CapturedFrame
        {
            RgbaData = _buffer,
            Width = _width,
            Height = _height,
            TimestampUs = DateTime.UtcNow.Ticks / 10,
            IsNew = true
        };
    }

    private static readonly string[] CursorArrowPattern = new[]
    {
        "X...............................",
        "XX..............................",
        "X#X.............................",
        "X##X............................",
        "X###X...........................",
        "X####X..........................",
        "X#####X.........................",
        "X######X........................",
        "X#######X.......................",
        "X########X......................",
        "X#########X.....................",
        "X##########X....................",
        "X###########X...................",
        "X############X..................",
        "X#############X.................",
        "X##############X................",
        "X###############X...............",
        "X#########XXXXXXX...............",
        "X#####X###X.....................",
        "X####X.X###X....................",
        "X###X..X###X....................",
        "X##X....X###X...................",
        "X#X.....X###X...................",
        "XX.......X###X..................",
        "X........X###X..................",
        "..........X###X.................",
        "..........X###X.................",
        "...........X###X................",
        "...........X###X................",
        "............XXX.................",
        "................................",
        "................................"
    };

    private void DrawSoftwareCursor(byte[] buffer, int curX, int curY)
    {
        int relX = curX - _x;
        int relY = curY - _y;

        // If cursor is outside this monitor's bounds, nothing to draw
        if (relX < -48 || relX >= _width || relY < -48 || relY >= _height)
            return;

        const float scale = 1.5f;

        for (int r = 0; r < CursorArrowPattern.Length; r++)
        {
            string rowStr = CursorArrowPattern[r];
            for (int c = 0; c < rowStr.Length; c++)
            {
                char ch = rowStr[c];
                if (ch != 'X' && ch != '#') continue;

                byte b = (ch == '#') ? (byte)255 : (byte)0;
                byte g = (ch == '#') ? (byte)255 : (byte)0;
                byte red = (ch == '#') ? (byte)255 : (byte)0;

                int pyStart = relY + (int)(r * scale);
                int pyEnd = relY + (int)((r + 1) * scale);
                int pxStart = relX + (int)(c * scale);
                int pxEnd = relX + (int)((c + 1) * scale);

                for (int py = pyStart; py < pyEnd; py++)
                {
                    if (py < 0 || py >= _height) continue;
                    int rowOffset = py * _width * 4;

                    for (int px = pxStart; px < pxEnd; px++)
                    {
                        if (px < 0 || px >= _width) continue;
                        int offset = rowOffset + (px * 4);

                        buffer[offset] = b;
                        buffer[offset + 1] = g;
                        buffer[offset + 2] = red;
                        buffer[offset + 3] = 255;
                    }
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

        if (_hdcSrc != IntPtr.Zero)
        {
            DeleteDC(_hdcSrc);
            _hdcSrc = IntPtr.Zero;
        }

        _pBits = IntPtr.Zero;
        _buffer = null;
    }
}

