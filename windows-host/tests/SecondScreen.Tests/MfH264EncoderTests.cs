using SecondScreen.Media.Encoding;
using Xunit;
using Xunit.Abstractions;

namespace SecondScreen.Tests;

public class MfH264EncoderTests
{
    private readonly ITestOutputHelper _output;

    public MfH264EncoderTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Initialize_And_Encode_ProducesValidH264Packet_WithStatusLabel()
    {
        using var encoder = new MfH264Encoder();
        encoder.Initialize(640, 360, fps: 60, bitrateBps: 4_000_000);

        _output.WriteLine($"Encoder StatusLabel: {encoder.StatusLabel}");
        _output.WriteLine($"IsHardwareAccelerated: {encoder.IsHardwareAccelerated}");
        _output.WriteLine($"FallbackReason: {encoder.FallbackReason ?? "None"}");

        Assert.False(string.IsNullOrWhiteSpace(encoder.EncoderName));
        Assert.NotEqual("None", encoder.EncoderName);

        byte[] bgra = new byte[640 * 360 * 4];
        EncodedPacket? firstPacket = null;

        for (int i = 0; i < 5; i++)
        {
            var pkt = encoder.EncodeBgraFrame(bgra, timestampUs: i * 16_666L);
            if (pkt != null && firstPacket == null)
            {
                firstPacket = pkt;
            }
        }

        Assert.NotNull(firstPacket);
        Assert.True(firstPacket!.Data.Length > 0);
        Assert.True(firstPacket.IsKeyframe);
        Assert.NotNull(encoder.CachedSpsPps);
    }
}

