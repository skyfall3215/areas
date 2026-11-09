using System.Drawing;
using System.Runtime.InteropServices;

namespace SecondScreen.Display;

public static class DisplayEnumerator
{
    private const int CCHDEVICENAME = 32;
    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion; public short dmDriverVersion; public short dmSize; public short dmDriverExtra;
        public int dmFields; public int dmPositionX; public int dmPositionY;
        public int dmDisplayOrientation; public int dmDisplayFixedOutput;
        public short dmColor; public short dmDuplex; public short dmYResolution; public short dmTTOption; public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel; public int dmPelsWidth; public int dmPelsHeight;
        public int dmDisplayFlags; public int dmDisplayFrequency;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    public static IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var list = new List<DisplayInfo>();
        int index = 0;

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
        {
            var mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf<MONITORINFOEX>();

            if (GetMonitorInfo(hMon, ref mi))
            {
                string devName = mi.szDevice ?? $"\\\\.\\DISPLAY{index + 1}";
                var bounds = new Rectangle(
                    mi.rcMonitor.left,
                    mi.rcMonitor.top,
                    mi.rcMonitor.right - mi.rcMonitor.left,
                    mi.rcMonitor.bottom - mi.rcMonitor.top
                );

                // Use physical device mode settings to avoid High-DPI virtualization skew
                var dm = new DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
                if (EnumDisplaySettings(devName, -1, ref dm) && dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
                {
                    bounds = new Rectangle(dm.dmPositionX, dm.dmPositionY, dm.dmPelsWidth, dm.dmPelsHeight);
                }

                bool isPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
                bool isVirtual = devName.Contains("VDD", StringComparison.OrdinalIgnoreCase) ||
                                 !isPrimary;

                list.Add(new DisplayInfo(
                    Index: index,
                    DeviceName: devName,
                    FriendlyName: isPrimary ? "Ana Ekran (Laptop)" : $"İkinci Ekran ({bounds.Width}x{bounds.Height})",
                    Bounds: bounds,
                    IsPrimary: isPrimary,
                    IsVirtual: isVirtual
                ));
                index++;
            }
            return true;
        }, IntPtr.Zero);

        if (list.Count == 0)
        {
            // Fallback: 1080p primary display
            list.Add(new DisplayInfo(
                Index: 0,
                DeviceName: @"\\.\DISPLAY1",
                FriendlyName: "Varsayılan Ekran",
                Bounds: new Rectangle(0, 0, 1920, 1080),
                IsPrimary: true,
                IsVirtual: false
            ));
        }

        return list;
    }

    /// <summary>
    /// Gets the target display to stream to the tablet.
    /// If a secondary monitor (virtual or physical) exists, it is prioritized.
    /// Otherwise falls back to primary screen.
    /// </summary>
    public static DisplayInfo GetTargetDisplay(int preferredIndex = -1)
    {
        var displays = GetDisplays();

        if (preferredIndex >= 0 && preferredIndex < displays.Count)
        {
            return displays[preferredIndex];
        }

        // Prioritize secondary display (the extended tablet screen)
        var secondary = displays.FirstOrDefault(d => !d.IsPrimary);
        if (secondary != null)
        {
            return secondary;
        }

        return displays[0];
    }
}

