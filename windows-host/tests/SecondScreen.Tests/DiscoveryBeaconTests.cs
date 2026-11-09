using System.Text;
using SecondScreen.Core.Protocol;
using SecondScreen.Network;
using Xunit;

namespace SecondScreen.Tests;

public class DiscoveryBeaconTests
{
    [Fact]
    public void SerializeAndDeserialize_JsonBeacon_Success()
    {
        var beacon = new DiscoveryBeacon
        {
            HostName = "TestHost",
            Port = 27183,
            Addresses = new List<string> { "192.168.1.100", "10.0.0.5" }
        };

        byte[] bytes = beacon.ToBytes();
        var parsed = DiscoveryBeacon.TryParse(bytes);

        Assert.NotNull(parsed);
        Assert.Equal(DiscoveryBeacon.DefaultMagic, parsed.Magic);
        Assert.Equal("TestHost", parsed.HostName);
        Assert.Equal(27183, parsed.Port);
        Assert.Contains("192.168.1.100", parsed.Addresses);
        Assert.Contains("10.0.0.5", parsed.Addresses);
    }

    [Fact]
    public void TryParse_TextBeaconFormat_Success()
    {
        string text = "SECOND_SCREEN_HOST:27183:MyGamingPC";
        var parsed = DiscoveryBeacon.TryParse(text);

        Assert.NotNull(parsed);
        Assert.Equal("MyGamingPC", parsed.HostName);
        Assert.Equal(27183, parsed.Port);
        Assert.Equal(DiscoveryBeacon.DefaultMagic, parsed.Magic);
    }

    [Fact]
    public void TryParse_InvalidOrGarbageInput_ReturnsNull()
    {
        Assert.Null(DiscoveryBeacon.TryParse((byte[])null!));
        Assert.Null(DiscoveryBeacon.TryParse(Array.Empty<byte>()));
        Assert.Null(DiscoveryBeacon.TryParse(""));
        Assert.Null(DiscoveryBeacon.TryParse("RANDOM_GARBAGE_DATA_12345"));
        Assert.Null(DiscoveryBeacon.TryParse("{\"magic\":\"UNKNOWN_APP\",\"version\":1}"));
    }

    [Fact]
    public void NetworkHelper_ExcludesLoopbackAndLinkLocal()
    {
        var ips = NetworkHelper.GetLocalIPv4Addresses();
        Assert.NotNull(ips);

        foreach (var ip in ips)
        {
            Assert.False(ip.StartsWith("127."), $"Should not include loopback address: {ip}");
            Assert.False(ip.StartsWith("169.254."), $"Should not include APIPA link-local: {ip}");
        }
    }
}

