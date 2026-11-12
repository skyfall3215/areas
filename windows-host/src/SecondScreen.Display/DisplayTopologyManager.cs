using System.Runtime.InteropServices;

namespace SecondScreen.Display;

public static class DisplayTopologyManager
{
    private const uint SDC_APPLY = 0x00000080;
    private const uint SDC_TOPOLOGY_INTERNAL = 0x00000001;
    private const uint SDC_TOPOLOGY_CLONE = 0x00000002;
    private const uint SDC_TOPOLOGY_EXTEND = 0x00000004;

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(
        uint numPathArrayElements,
        IntPtr pathArray,
        uint numModeInfoArrayElements,
        IntPtr modeInfoArray,
        uint flags
    );

    public static int SetDisplayConfigRaw()
    {
        return SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_EXTEND);
    }

    /// <summary>
    /// Extends the Windows desktop to include all detected monitors.
    /// </summary>
    public static bool ExtendDesktop()
    {
        try
        {
            int result = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_EXTEND);
            if (result == 0) return true;
        }
        catch { }

        try
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "displayswitch.exe",
                Arguments = "/extend",
                UseShellExecute = true,
                CreateNoWindow = true
            });
            if (proc == null) return false;
            proc.WaitForExit(2000);
            return !proc.HasExited || proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Restores single-display (internal) mode when tablet disconnects,
    /// ensuring open windows are not lost on an invisible virtual screen.
    /// </summary>
    public static bool RestoreInternalOnly()
    {
        try
        {
            int result = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_INTERNAL);
            if (result == 0) return true;
        }
        catch { }

        try
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "displayswitch.exe",
                Arguments = "/internal",
                UseShellExecute = true,
                CreateNoWindow = true
            });
            if (proc == null) return false;
            proc.WaitForExit(2000);
            return !proc.HasExited || proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

