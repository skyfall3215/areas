using SecondScreen.Network;
using Xunit;

namespace SecondScreen.Tests;

public class HotspotManagerTests
{
    [Fact]
    public void PrioritizeAddresses_PutsHotspotIpFirst()
    {
        var rawIps = new List<string>
        {
            "10.20.30.40",            // Campus / Dormitory Wi-Fi
            "192.168.1.50",           // Home Wi-Fi
            "192.168.137.1",          // Windows Mobile Hotspot
            "172.20.10.2"             // Other interface
        };

        var sorted = HotspotManager.PrioritizeAddresses(rawIps);

        Assert.Equal("192.168.137.1", sorted[0]);
    }

    [Fact]
    public void IsHotspotClient_DetectsHotspotSubnet()
    {
        Assert.True(HotspotManager.IsHotspotClient("192.168.137.45:51234"));
        Assert.True(HotspotManager.IsHotspotClient("192.168.137.1"));
        Assert.False(HotspotManager.IsHotspotClient("192.168.1.50:51234"));
        Assert.False(HotspotManager.IsHotspotClient("127.0.0.1:27183"));
        Assert.False(HotspotManager.IsHotspotClient(null));
        Assert.False(HotspotManager.IsHotspotClient(""));
    }
}

