using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SecondScreen.Network;

public static class NetworkHelper
{
    /// <summary>
    /// Gets all active, non-loopback, non-APIPA IPv4 addresses assigned to operational network interfaces.
    /// </summary>
    public static List<string> GetLocalIPv4Addresses()
    {
        var addresses = new List<string>();

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var iface in interfaces)
            {
                if (iface.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var ipProps = iface.GetIPProperties();
                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var ip = unicast.Address.ToString();
                        // Ignore APIPA link-local 169.254.x.x
                        if (!ip.StartsWith("169.254.") && !ip.StartsWith("127."))
                        {
                            addresses.Add(ip);
                        }
                    }
                }
            }
        }
        catch
        {
            // Fallback to DNS host entry
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var s = ip.ToString();
                        if (!s.StartsWith("169.254.") && !s.StartsWith("127."))
                        {
                            addresses.Add(s);
                        }
                    }
                }
            }
            catch { }
        }

        return addresses.Distinct().ToList();
    }
}

