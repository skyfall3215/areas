using System.Drawing;
using System.Runtime.InteropServices;

namespace SecondScreen.Media.Capture;

public sealed class GdiScreenCapture : IDisposable
{
    private const int DIB_RGB_COLORS = 0;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint SRCAND = 0x008800C6;
    private const uint SRCINVERT = 0x00660046;
    private const uint CAPTUREBLT = 0x40000000;
    private const int CURSOR_SHOWING = 0x00000001;
    private const uint DI_NORMAL = 0x0003;
    private const int IDC_ARROW = 32512;
    private const uint DESKTOP_ALL = 0x01FF;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

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
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

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
    private bool _usedCreateDc;
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

        // Use the global virtual desktop DC so DWM composited surfaces across all monitors
        // (including IddCx virtual displays at X=1920) are captured cleanly at (_x, _y).
        _usedCreateDc = false;
        _hdcSrc = GetDC(IntPtr.Zero);
        if (_hdcSrc == IntPtr.Zero)
        {
            _hdcSrc = CreateDC("DISPLAY", null, null, IntPtr.Zero);
            _usedCreateDc = (_hdcSrc != IntPtr.Zero);
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

    // Reusable 96x96 mini-DIB for ultra-fast (< 0.05 ms) native cursor compositing onto DXGI or GDI buffers
    private const int CursorCanvasSize = 96;
    [ThreadStatic] private static IntPtr _tsCursorHdc;
    [ThreadStatic] private static IntPtr _tsCursorBmp;
    [ThreadStatic] private static IntPtr _tsCursorOldBmp;
    [ThreadStatic] private static IntPtr _tsCursorBits;
    [ThreadStatic] private static IntPtr _tsDefaultArrow;

    private static void EnsureCursorCanvasInitialized()
    {
        if (_tsCursorHdc != IntPtr.Zero && _tsCursorBits != IntPtr.Zero) return;

        IntPtr hdcScreen = GetDC(IntPtr.Zero);
        _tsCursorHdc = CreateCompatibleDC(hdcScreen);
        if (hdcScreen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdcScreen);

        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = CursorCanvasSize;
        bmi.bmiHeader.biHeight = -CursorCanvasSize; // top-down
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0;

        _tsCursorBmp = CreateDIBSection(_tsCursorHdc, ref bmi, DIB_RGB_COLORS, out _tsCursorBits, IntPtr.Zero, 0);
        if (_tsCursorBmp != IntPtr.Zero)
        {
            _tsCursorOldBmp = SelectObject(_tsCursorHdc, _tsCursorBmp);
        }
        _tsDefaultArrow = LoadCursor(IntPtr.Zero, IDC_ARROW);
    }

    /// <summary>
    /// Composites the genuine native Windows cursor (arrow, hand, I-beam, resize, etc.) directly onto
    /// a BGRA pixel buffer in ~0.04 ms by copying only the 96x96 patch around the cursor into a cached
    /// mini-DIB, invoking GDI DrawIconEx/XOR mask on that 96x96 patch, and writing it back.
    /// </summary>
    public static unsafe void DrawCursorOntoBgraBuffer(byte[] bgraBuffer, int width, int height, int monitorX, int monitorY)
    {
        DrawCursorOntoBgraBuffer(bgraBuffer, width, height, monitorX, monitorY, out _);
    }

    public static unsafe void DrawCursorOntoBgraBuffer(byte[] bgraBuffer, int width, int height, int monitorX, int monitorY, out Rectangle drawnPatch)
    {
        drawnPatch = default;
        if (bgraBuffer == null || bgraBuffer.Length < width * height * 4) return;

        var ci = new CURSORINFO();
        ci.cbSize = Marshal.SizeOf<CURSORINFO>();

        IntPtr hCursor = IntPtr.Zero;
        int curX = 0;
        int curY = 0;

        if (GetCursorInfo(out ci) && (ci.flags & CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero)
        {
            hCursor = ci.hCursor;
            curX = ci.ptScreenPos.x;
            curY = ci.ptScreenPos.y;
        }
        else if (GetCursorPos(out var pt))
        {
            EnsureCursorCanvasInitialized();
            hCursor = _tsDefaultArrow;
            curX = pt.x;
            curY = pt.y;
        }

        if (hCursor == IntPtr.Zero) return;

        int relX = curX - monitorX;
        int relY = curY - monitorY;
        if (relX < -64 || relX >= width + 64 || relY < -64 || relY >= height + 64)
            return;

        int hotspotX = 0;
        int hotspotY = 0;
        bool isMonochromeXor = false;
        IntPtr hbmMask = IntPtr.Zero;
        IntPtr hbmColor = IntPtr.Zero;

        if (GetIconInfo(hCursor, out var ii))
        {
            hotspotX = ii.xHotspot;
            hotspotY = ii.yHotspot;
            hbmMask = ii.hbmMask;
            hbmColor = ii.hbmColor;
            isMonochromeXor = (hbmColor == IntPtr.Zero && hbmMask != IntPtr.Zero);
        }

        int topLeftX = relX - hotspotX;
        int topLeftY = relY - hotspotY;

        int clipX0 = Math.Max(0, topLeftX);
        int clipY0 = Math.Max(0, topLeftY);
        int clipX1 = Math.Min(width, topLeftX + CursorCanvasSize);
        int clipY1 = Math.Min(height, topLeftY + CursorCanvasSize);
        int copyW = clipX1 - clipX0;
        int copyH = clipY1 - clipY0;

        if (copyW <= 0 || copyH <= 0)
        {
            if (hbmMask != IntPtr.Zero) DeleteObject(hbmMask);
            if (hbmColor != IntPtr.Zero) DeleteObject(hbmColor);
            return;
        }

        EnsureCursorCanvasInitialized();
        if (_tsCursorHdc == IntPtr.Zero || _tsCursorBits == IntPtr.Zero)
        {
            if (hbmMask != IntPtr.Zero) DeleteObject(hbmMask);
            if (hbmColor != IntPtr.Zero) DeleteObject(hbmColor);
            return;
        }

        int canvasOffsetX = clipX0 - topLeftX;
        int canvasOffsetY = clipY0 - topLeftY;
        byte* pCanvas = (byte*)_tsCursorBits.ToPointer();

        fixed (byte* pBuf = bgraBuffer)
        {
            // 1. Copy 96x96 background patch from frame buffer into mini-DIB
            for (int row = 0; row < copyH; row++)
            {
                byte* srcRow = pBuf + ((clipY0 + row) * width + clipX0) * 4;
                byte* dstRow = pCanvas + ((canvasOffsetY + row) * CursorCanvasSize + canvasOffsetX) * 4;
                Buffer.MemoryCopy(srcRow, dstRow, copyW * 4, copyW * 4);
            }

            // 2. Render cursor at (0, 0) of mini-DIB
            bool drawn = false;
            if (isMonochromeXor && GetObject(hbmMask, Marshal.SizeOf<BITMAP>(), out var bmp) > 0 && bmp.bmHeight >= 2)
            {
                int curW = bmp.bmWidth;
                int curH = bmp.bmHeight / 2;
                IntPtr hdcMask = CreateCompatibleDC(_tsCursorHdc);
                if (hdcMask != IntPtr.Zero)
                {
                    IntPtr oldMask = SelectObject(hdcMask, hbmMask);
                    BitBlt(_tsCursorHdc, 0, 0, curW, curH, hdcMask, 0, 0, SRCAND);
                    drawn = BitBlt(_tsCursorHdc, 0, 0, curW, curH, hdcMask, 0, curH, SRCINVERT);
                    SelectObject(hdcMask, oldMask);
                    DeleteDC(hdcMask);
                }
            }

            if (!drawn)
            {
                drawn = DrawIconEx(_tsCursorHdc, 0, 0, hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
            }

            // 3. Copy composited 96x96 patch back to frame buffer
            if (drawn)
            {
                for (int row = 0; row < copyH; row++)
                {
                    byte* srcRow = pCanvas + ((canvasOffsetY + row) * CursorCanvasSize + canvasOffsetX) * 4;
                    byte* dstRow = pBuf + ((clipY0 + row) * width + clipX0) * 4;
                    Buffer.MemoryCopy(srcRow, dstRow, copyW * 4, copyW * 4);
                }
                drawnPatch = new Rectangle(clipX0, clipY0, copyW, copyH);
            }
        }

        if (hbmMask != IntPtr.Zero) DeleteObject(hbmMask);
        if (hbmColor != IntPtr.Zero) DeleteObject(hbmColor);
    }

    public static void EnsureThreadDesktopAttached()
    {
        try
        {
            IntPtr hDesktop = OpenInputDesktop(0, false, DESKTOP_ALL);
            if (hDesktop != IntPtr.Zero)
            {
                SetThreadDesktop(hDesktop);
                CloseDesktop(hDesktop);
            }
        }
        catch
        {
            // Ignore if unable to switch desktop
        }
    }

    public CapturedFrame? AcquireNextFrame()
    {
        if (!_isInitialized || _buffer == null || _pBits == IntPtr.Zero) return null;

        try
        {
            bool success = _hdcSrc != IntPtr.Zero && BitBlt(_hdcMem, 0, 0, _width, _height, _hdcSrc, _x, _y, SRCCOPY);
            if (!success)
            {
                EnsureThreadDesktopAttached();
                IntPtr hdcScreen = GetDC(IntPtr.Zero);
                if (hdcScreen != IntPtr.Zero)
                {
                    if (_hdcSrc != IntPtr.Zero)
                    {
                        if (_usedCreateDc) DeleteDC(_hdcSrc);
                        else ReleaseDC(IntPtr.Zero, _hdcSrc);
                    }
                    _hdcSrc = hdcScreen;
                    _usedCreateDc = false;
                    success = BitBlt(_hdcMem, 0, 0, _width, _height, _hdcSrc, _x, _y, SRCCOPY);
                }
            }

            if (!success) return null;

            // Draw the genuine native Windows cursor (arrow, hand, I-beam, resize, etc.) onto _hdcMem before copying pixels
            bool nativeCursorDrawn = DrawNativeCursorOnDc();

            int totalBytes = _width * _height * 4;
            Marshal.Copy(_pBits, _buffer, 0, totalBytes);

            // Fallback to software cursor only if native DrawIconEx did not draw
            if (!nativeCursorDrawn && GetCursorPos(out var pt))
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
        catch
        {
            return null;
        }
    }

    private bool DrawNativeCursorOnDc()
    {
        var ci = new CURSORINFO();
        ci.cbSize = Marshal.SizeOf<CURSORINFO>();

        IntPtr hCursor = IntPtr.Zero;
        int curX = 0;
        int curY = 0;

        if (GetCursorInfo(out ci) && (ci.flags & CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero)
        {
            hCursor = ci.hCursor;
            curX = ci.ptScreenPos.x;
            curY = ci.ptScreenPos.y;
        }
        else if (GetCursorPos(out var pt))
        {
            hCursor = _hDefaultCursor;
            curX = pt.x;
            curY = pt.y;
        }

        if (hCursor == IntPtr.Zero) return false;

        int relX = curX - _x;
        int relY = curY - _y;
        if (relX < -64 || relX >= _width + 64 || relY < -64 || relY >= _height + 64)
            return true; // Cursor is on another monitor, no fallback needed

        int hotspotX = 0;
        int hotspotY = 0;
        bool isMonochromeXor = false;
        IntPtr hbmMask = IntPtr.Zero;
        IntPtr hbmColor = IntPtr.Zero;

        if (GetIconInfo(hCursor, out var ii))
        {
            hotspotX = ii.xHotspot;
            hotspotY = ii.yHotspot;
            hbmMask = ii.hbmMask;
            hbmColor = ii.hbmColor;
            isMonochromeXor = (hbmColor == IntPtr.Zero && hbmMask != IntPtr.Zero);
        }

        int drawX = relX - hotspotX;
        int drawY = relY - hotspotY;
        bool drawn = false;

        if (isMonochromeXor && GetObject(hbmMask, Marshal.SizeOf<BITMAP>(), out var bmp) > 0 && bmp.bmHeight >= 2)
        {
            int curW = bmp.bmWidth;
            int curH = bmp.bmHeight / 2;
            IntPtr hdcMask = CreateCompatibleDC(_hdcMem);
            if (hdcMask != IntPtr.Zero)
            {
                IntPtr oldMask = SelectObject(hdcMask, hbmMask);
                BitBlt(_hdcMem, drawX, drawY, curW, curH, hdcMask, 0, 0, SRCAND);
                drawn = BitBlt(_hdcMem, drawX, drawY, curW, curH, hdcMask, 0, curH, SRCINVERT);
                SelectObject(hdcMask, oldMask);
                DeleteDC(hdcMask);
            }
        }

        if (!drawn)
        {
            drawn = DrawIconEx(
                _hdcMem,
                drawX,
                drawY,
                hCursor,
                0,
                0,
                0,
                IntPtr.Zero,
                DI_NORMAL);
        }

        if (hbmMask != IntPtr.Zero) DeleteObject(hbmMask);
        if (hbmColor != IntPtr.Zero) DeleteObject(hbmColor);

        return drawn;
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
            if (_usedCreateDc)
                DeleteDC(_hdcSrc);
            else
                ReleaseDC(IntPtr.Zero, _hdcSrc);
            _hdcSrc = IntPtr.Zero;
        }

        _pBits = IntPtr.Zero;
        _buffer = null;
    }
}

