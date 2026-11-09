using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SecondScreen.Network;

public static class HotspotManager
{
    public const string DefaultWindowsHotspotIp = "192.168.137.1";

    /// <summary>
    /// Checks if the Windows Mobile Hotspot virtual adapter (192.168.137.1) is active.
    /// </summary>
    public static bool IsHotspotActive()
    {
        var ips = NetworkHelper.GetLocalIPv4Addresses();
        return ips.Contains(DefaultWindowsHotspotIp);
    }

    /// <summary>
    /// Checks if an IP belongs to the Windows Mobile Hotspot subnet.
    /// </summary>
    public static bool IsHotspotClient(string? remoteIpOrEndpoint)
    {
        if (string.IsNullOrEmpty(remoteIpOrEndpoint)) return false;
        return remoteIpOrEndpoint.Contains("192.168.137.");
    }

    /// <summary>
    /// Sorts IP addresses putting the dedicated Hotspot address first,
    /// followed by common LAN subnets, then other IPs.
    /// </summary>
    public static List<string> PrioritizeAddresses(IEnumerable<string> addresses)
    {
        return addresses
            .Distinct()
            .OrderByDescending(ip => ip == DefaultWindowsHotspotIp ? 3 :
                                     ip.StartsWith("192.168.") ? 2 :
                                     ip.StartsWith("10.") || ip.StartsWith("172.") ? 1 : 0)
            .ThenBy(ip => ip)
            .ToList();
    }

    /// <summary>
    /// Opens the Windows 10/11 Mobile Hotspot settings page.
    /// </summary>
    public static bool OpenHotspotSettings()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:network-mobilehotspot",
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}

