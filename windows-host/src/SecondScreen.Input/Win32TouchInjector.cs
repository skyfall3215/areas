using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using SecondScreen.Core.Protocol;

namespace SecondScreen.Input;

public enum TouchFeedbackMode : uint
{
    Default = 0x1,
    Indirect = 0x2,
    None = 0x3
}

public interface ITouchInjector : IDisposable
{
    bool IsInitialized { get; }
    Rectangle ScreenBounds { get; set; }
    bool Initialize(uint maxPointers = 10, TouchFeedbackMode feedbackMode = TouchFeedbackMode.Default);
    bool InjectTouch(TouchEventMessage touchEvent);
    void CancelAllPointers();
}

public sealed class Win32TouchInjector : ITouchInjector
{
    private const uint PT_TOUCH = 0x00000002;
    private const uint TOUCH_MASK_CONTACTAREA = 0x00000001;

    [Flags]
    public enum PointerFlags : uint
    {
        NONE = 0x00000000,
        NEW = 0x00000001,
        INRANGE = 0x00000002,
        INCONTACT = 0x00000004,
        FIRSTBUTTON = 0x00000010,
        PRIMARY = 0x00002000,
        CONFIDENCE = 0x00004000,
        CANCELED = 0x00008000,
        DOWN = 0x00010000,
        UPDATE = 0x00020000,
        UP = 0x00040000
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public nint sourceDevice;
        public nint hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InjectTouchInput(uint count, [MarshalAs(UnmanagedType.LPArray)] POINTER_TOUCH_INFO[] contacts);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, nint dwExtraInfo);

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private readonly object _lock = new();
    private readonly Dictionary<byte, (int X, int Y)> _activePointers = new();
    private bool _isInitialized;
    private uint _maxPointers = 10;
    private Rectangle _screenBounds;

    public bool IsInitialized => _isInitialized;

    public Rectangle ScreenBounds
    {
        get => _screenBounds;
        set => _screenBounds = value;
    }

    public Win32TouchInjector()
    {
        // Default to Primary Screen bounds
        int width = GetSystemMetrics(SM_CXSCREEN);
        int height = GetSystemMetrics(SM_CYSCREEN);
        _screenBounds = new Rectangle(0, 0, width > 0 ? width : 1920, height > 0 ? height : 1080);
    }

    public bool Initialize(uint maxPointers = 10, TouchFeedbackMode feedbackMode = TouchFeedbackMode.Default)
    {
        lock (_lock)
        {
            if (_isInitialized) return true;

            _maxPointers = Math.Min(maxPointers, 10);
            try
            {
                bool rawInit = InitializeTouchInjection(_maxPointers, (uint)feedbackMode);
                int err = Marshal.GetLastPInvokeError();
                Console.WriteLine($"[Win32TouchInjector] InitializeTouchInjection rawInit={rawInit}, err={err}");

                if (rawInit)
                {
                    _isInitialized = true;
                    return true;
                }

                // 0x5 = ERROR_ACCESS_DENIED, 0x57 = ERROR_INVALID_PARAMETER (often already initialized by process)
                if (err == 87)
                {
                    _isInitialized = true;
                    return true;
                }

                Console.WriteLine($"[Win32TouchInjector] InitializeTouchInjection failed with Win32 error: {err}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Win32TouchInjector] InitializeTouchInjection exception: {ex.Message}");
                return false;
            }
        }
    }

    public bool InjectTouch(TouchEventMessage touchEvent)
    {
        if (!_isInitialized)
        {
            if (!Initialize(_maxPointers))
                return false;
        }

        if (touchEvent.PointerCount == 0 || touchEvent.Pointers.Length == 0)
            return false;

        lock (_lock)
        {
            var primary = touchEvent.Pointers[0];
            if (primary.Action is TouchAction.LeftClick or TouchAction.RightClick or TouchAction.Scroll)
            {
                var pixel = TouchCoordinateMapper.NormalizeToScreen(primary.NormalizedX, primary.NormalizedY, _screenBounds);
                SetCursorPos(pixel.X, pixel.Y);

                uint normX = (uint)Math.Clamp((int)Math.Round((pixel.X - _screenBounds.Left) * 65535.0 / Math.Max(1, _screenBounds.Width - 1)), 0, 65535);
                uint normY = (uint)Math.Clamp((int)Math.Round((pixel.Y - _screenBounds.Top) * 65535.0 / Math.Max(1, _screenBounds.Height - 1)), 0, 65535);

                switch (primary.Action)
                {
                    case TouchAction.LeftClick:
                        mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE | MOUSEEVENTF_LEFTDOWN, normX, normY, 0, 0);
                        mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE | MOUSEEVENTF_LEFTUP, normX, normY, 0, 0);
                        break;
                    case TouchAction.RightClick:
                        mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE | MOUSEEVENTF_RIGHTDOWN, normX, normY, 0, 0);
                        mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE | MOUSEEVENTF_RIGHTUP, normX, normY, 0, 0);
                        break;
                    case TouchAction.Scroll:
                        // Scroll delta passed via NormalizedY as signed 16-bit
                        short scrollDelta = (short)primary.NormalizedY;
                        mouse_event(MOUSEEVENTF_WHEEL, 0, 0, (uint)(int)scrollDelta, 0);
                        break;
                }
                return true;
            }

            int count = Math.Min((int)touchEvent.PointerCount, touchEvent.Pointers.Length);
            var contacts = new POINTER_TOUCH_INFO[count];

            for (int i = 0; i < count; i++)
            {
                var p = touchEvent.Pointers[i];
                var pixel = TouchCoordinateMapper.NormalizeToScreen(p.NormalizedX, p.NormalizedY, _screenBounds);

                uint flags = (uint)PointerFlags.INRANGE;
                int radius = p.ContactRadius > 0 ? p.ContactRadius : 10;

                switch (p.Action)
                {
                    case TouchAction.Down:
                        flags = (uint)(PointerFlags.DOWN | PointerFlags.INRANGE | PointerFlags.INCONTACT);
                        _activePointers[p.PointerId] = (pixel.X, pixel.Y);
                        break;

                    case TouchAction.Move:
                        flags = (uint)(PointerFlags.UPDATE | PointerFlags.INRANGE | PointerFlags.INCONTACT);
                        _activePointers[p.PointerId] = (pixel.X, pixel.Y);
                        break;

                    case TouchAction.Up:
                        flags = (uint)PointerFlags.UP;
                        _activePointers.Remove(p.PointerId);
                        break;

                    case TouchAction.Cancel:
                        flags = (uint)(PointerFlags.UP | PointerFlags.CANCELED);
                        _activePointers.Remove(p.PointerId);
                        break;
                }

                var rect = new RECT
                {
                    left = pixel.X - radius,
                    top = pixel.Y - radius,
                    right = pixel.X + radius,
                    bottom = pixel.Y + radius
                };

                contacts[i] = new POINTER_TOUCH_INFO
                {
                    pointerInfo = new POINTER_INFO
                    {
                        pointerType = PT_TOUCH,
                        pointerId = p.PointerId,
                        pointerFlags = flags,
                        ptPixelLocation = new POINT { x = pixel.X, y = pixel.Y },
                        ptPixelLocationRaw = new POINT { x = pixel.X, y = pixel.Y }
                    },
                    touchMask = TOUCH_MASK_CONTACTAREA | 0x00000004 /* PRESSURE */,
                    rcContact = rect,
                    rcContactRaw = rect,
                    orientation = 90,
                    pressure = 32000
                };
            }

            bool injected = false;
            try
            {
                injected = InjectTouchInput((uint)contacts.Length, contacts);
            }
            catch { }

            if (injected)
            {
                return true;
            }

            // Fallback: Windows mouse emulation (works on all Windows systems without elevated UIPI or digitizer drivers)
            try
            {
                primary = touchEvent.Pointers[0];
                var pixel = TouchCoordinateMapper.NormalizeToScreen(primary.NormalizedX, primary.NormalizedY, _screenBounds);
                SetCursorPos(pixel.X, pixel.Y);

                uint normX = (uint)Math.Clamp((int)Math.Round((pixel.X - _screenBounds.Left) * 65535.0 / Math.Max(1, _screenBounds.Width - 1)), 0, 65535);
                uint normY = (uint)Math.Clamp((int)Math.Round((pixel.Y - _screenBounds.Top) * 65535.0 / Math.Max(1, _screenBounds.Height - 1)), 0, 65535);

                switch (primary.Action)
                {
                    case TouchAction.Down:
                        mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE | MOUSEEVENTF_LEFTDOWN, normX, normY, 0, 0);
                        break;
                    case TouchAction.Move:
                        mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE, normX, normY, 0, 0);
                        break;
                    case TouchAction.Up:
                    case TouchAction.Cancel:
                        mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE | MOUSEEVENTF_LEFTUP, normX, normY, 0, 0);
                        break;
                    case TouchAction.LeftClick:
                        mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE | MOUSEEVENTF_LEFTDOWN, normX, normY, 0, 0);
                        mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE | MOUSEEVENTF_LEFTUP, normX, normY, 0, 0);
                        break;
                    case TouchAction.RightClick:
                        mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE | MOUSEEVENTF_RIGHTDOWN, normX, normY, 0, 0);
                        mouse_event(MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE | MOUSEEVENTF_RIGHTUP, normX, normY, 0, 0);
                        break;
                    case TouchAction.Scroll:
                        // Scroll delta passed via NormalizedY as signed 16-bit
                        short scrollDelta = (short)primary.NormalizedY;
                        mouse_event(MOUSEEVENTF_WHEEL, 0, 0, (uint)(int)scrollDelta, 0);
                        break;
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Win32TouchInjector] Mouse fallback exception: {ex.Message}");
                return false;
            }
        }
    }

    public void CancelAllPointers()
    {
        lock (_lock)
        {
            try
            {
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
            }
            catch { }

            if (_activePointers.Count == 0 || !_isInitialized)
                return;

            var contacts = new POINTER_TOUCH_INFO[_activePointers.Count];
            int i = 0;

            foreach (var kvp in _activePointers)
            {
                contacts[i++] = new POINTER_TOUCH_INFO
                {
                    pointerInfo = new POINTER_INFO
                    {
                        pointerType = PT_TOUCH,
                        pointerId = kvp.Key,
                        pointerFlags = (uint)(PointerFlags.UP | PointerFlags.CANCELED),
                        ptPixelLocation = new POINT { x = kvp.Value.X, y = kvp.Value.Y }
                    }
                };
            }

            try
            {
                InjectTouchInput((uint)contacts.Length, contacts);
            }
            catch { }
            finally
            {
                _activePointers.Clear();
            }
        }
    }

    public void Dispose()
    {
        CancelAllPointers();
        _isInitialized = false;
    }
}

