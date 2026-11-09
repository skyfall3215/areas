using SecondScreen.Core.Protocol;
using Xunit;

namespace SecondScreen.Tests;

public class ProtocolTests
{
    private static string FindTestVectorsDir()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "protocol", "test-vectors");
            if (Directory.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir)!;
        }
        return @"d:\Areas\protocol\test-vectors";
    }

    private static readonly string TestVectorsDir = FindTestVectorsDir();

    [Fact]
    public void FrameHeader_RoundTrip_SerializesAndDeserializesCorrectly()
    {
        var header = FrameHeader.Create(
            MessageType.VideoFrame,
            payloadLength: 1024,
            sequence: 42,
            timestampUs: 987654,
            flags: ProtocolConstants.VideoFlagKeyframe);

        Span<byte> buffer = stackalloc byte[ProtocolConstants.HeaderSize];
        header.WriteTo(buffer);

        var parsed = FrameHeader.ReadFrom(buffer);
        Assert.Equal(ProtocolConstants.Magic0, parsed.Magic0);
        Assert.Equal(ProtocolConstants.Magic1, parsed.Magic1);
        Assert.Equal(ProtocolConstants.Version1, parsed.Version);
        Assert.Equal(MessageType.VideoFrame, parsed.MsgType);
        Assert.Equal(ProtocolConstants.VideoFlagKeyframe, parsed.Flags);
        Assert.Equal(0, parsed.Reserved);
        Assert.Equal((ushort)42, parsed.Sequence);
        Assert.Equal((uint)987654, parsed.TimestampUs);
        Assert.Equal((uint)1024, parsed.PayloadLength);
        Assert.True(parsed.Validate(out string? err));
        Assert.Null(err);
    }

    [Fact]
    public void FrameHeader_Validation_FailsOnInvalidMagic()
    {
        byte[] buffer = new byte[ProtocolConstants.HeaderSize];
        buffer[0] = 0xAA; // Wrong magic
        buffer[1] = 0xBB;
        buffer[2] = 0x01;

        var header = FrameHeader.ReadFrom(buffer);
        Assert.False(header.Validate(out string? error));
        Assert.Contains("Invalid magic", error);
    }

    [Fact]
    public void FrameHeader_Validation_FailsOnUnsupportedVersion()
    {
        byte[] buffer = new byte[ProtocolConstants.HeaderSize];
        buffer[0] = ProtocolConstants.Magic0;
        buffer[1] = ProtocolConstants.Magic1;
        buffer[2] = 0x99; // Version 99

        var header = FrameHeader.ReadFrom(buffer);
        Assert.False(header.Validate(out string? error));
        Assert.Contains("Unsupported protocol version", error);
    }

    [Fact]
    public void FrameHeader_Validation_FailsOnOversizedPayload()
    {
        var header = FrameHeader.Create(
            MessageType.VideoFrame,
            payloadLength: ProtocolConstants.MaxPayloadLength + 1);

        Assert.False(header.Validate(out string? error));
        Assert.Contains("exceeds maximum allowed limit", error);
    }

    [Fact]
    public void HelloMessage_RoundTrip_MatchesExpectedValues()
    {
        var original = new HelloMessage(
            NativeWidth: 2560,
            NativeHeight: 1600,
            MaxFps: 120,
            PreferredBitrateKbps: 20000,
            FeatureFlags: ProtocolConstants.FeatureFlagTouch | ProtocolConstants.FeatureFlagLowLatencyDecoder,
            DeviceName: "Galaxy Tab S9+");

        byte[] payload = new byte[HelloMessage.PayloadSize];
        original.WritePayloadTo(payload);

        var deserialized = HelloMessage.ReadPayloadFrom(payload);
        Assert.Equal(original.NativeWidth, deserialized.NativeWidth);
        Assert.Equal(original.NativeHeight, deserialized.NativeHeight);
        Assert.Equal(original.MaxFps, deserialized.MaxFps);
        Assert.Equal(original.PreferredBitrateKbps, deserialized.PreferredBitrateKbps);
        Assert.Equal(original.FeatureFlags, deserialized.FeatureFlags);
        Assert.Equal(original.DeviceName, deserialized.DeviceName);
    }

    [Fact]
    public void HelloAckMessage_RoundTrip_MatchesExpectedValues()
    {
        var original = new HelloAckMessage(
            Status: ProtocolConstants.HelloStatusOk,
            CodecId: ProtocolConstants.CodecIdH264,
            StreamWidth: 1920,
            StreamHeight: 1080,
            TargetFps: 60,
            BitrateBps: 15_000_000,
            SessionId: 0xDEADBEEF);

        byte[] payload = new byte[HelloAckMessage.PayloadSize];
        original.WritePayloadTo(payload);

        var deserialized = HelloAckMessage.ReadPayloadFrom(payload);
        Assert.Equal(original.Status, deserialized.Status);
        Assert.Equal(original.CodecId, deserialized.CodecId);
        Assert.Equal(original.StreamWidth, deserialized.StreamWidth);
        Assert.Equal(original.StreamHeight, deserialized.StreamHeight);
        Assert.Equal(original.TargetFps, deserialized.TargetFps);
        Assert.Equal(original.BitrateBps, deserialized.BitrateBps);
        Assert.Equal(original.SessionId, deserialized.SessionId);
    }

    [Fact]
    public void VideoConfigMessage_RoundTrip_MatchesExpectedValues()
    {
        byte[] spsPps = [0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00, 0x00, 0x00, 0x01, 0x68, 0xCE];
        var config = new VideoConfigMessage(spsPps);

        byte[] dest = new byte[spsPps.Length];
        config.WritePayloadTo(dest);

        var parsed = VideoConfigMessage.ReadPayloadFrom(dest);
        Assert.Equal(spsPps, parsed.SpsPpsData);
    }

    [Fact]
    public void VideoFrameMessage_RoundTrip_MatchesExpectedValues()
    {
        byte[] nalData = [0x00, 0x00, 0x00, 0x01, 0x65, 0x01, 0x02, 0x03, 0x04];
        var frame = new VideoFrameMessage(
            FrameNumber: 1234,
            CaptureDurationUs: 1500,
            EncodeDurationUs: 3200,
            NalData: nalData,
            IsKeyframe: true,
            IsCursorEmbedded: false);

        byte[] payload = new byte[frame.PayloadSize];
        frame.WritePayloadTo(payload);

        byte flags = frame.GetFlags();
        var parsed = VideoFrameMessage.ReadPayloadFrom(payload, flags);

        Assert.Equal((uint)1234, parsed.FrameNumber);
        Assert.Equal((ushort)1500, parsed.CaptureDurationUs);
        Assert.Equal((ushort)3200, parsed.EncodeDurationUs);
        Assert.True(parsed.IsKeyframe);
        Assert.False(parsed.IsCursorEmbedded);
        Assert.Equal(nalData, parsed.NalData);
    }

    [Fact]
    public void TestVector_HelloSample_ValidatesAndParses()
    {
        string path = Path.Combine(TestVectorsDir, "hello_sample.bin");
        Assert.True(File.Exists(path), $"Test vector not found at {path}");

        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(48, bytes.Length);

        var header = FrameHeader.ReadFrom(bytes.AsSpan(0, ProtocolConstants.HeaderSize));
        Assert.True(header.Validate(out string? err));
        Assert.Equal(MessageType.Hello, header.MsgType);
        Assert.Equal((uint)32, header.PayloadLength);
        Assert.Equal((ushort)1, header.Sequence);
        Assert.Equal((uint)1000000, header.TimestampUs);

        var hello = HelloMessage.ReadPayloadFrom(bytes.AsSpan(ProtocolConstants.HeaderSize));
        Assert.Equal((ushort)2560, hello.NativeWidth);
        Assert.Equal((ushort)1600, hello.NativeHeight);
        Assert.Equal((ushort)60, hello.MaxFps);
        Assert.Equal((ushort)15000, hello.PreferredBitrateKbps);
        Assert.Equal(0x03, hello.FeatureFlags);
        Assert.Equal("Galaxy Tab S9", hello.DeviceName);
    }

    [Fact]
    public void TestVector_HelloAckSample_ValidatesAndParses()
    {
        string path = Path.Combine(TestVectorsDir, "hello_ack_sample.bin");
        Assert.True(File.Exists(path));

        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(32, bytes.Length);

        var header = FrameHeader.ReadFrom(bytes.AsSpan(0, ProtocolConstants.HeaderSize));
        Assert.True(header.Validate(out string? err));
        Assert.Equal(MessageType.HelloAck, header.MsgType);
        Assert.Equal((uint)16, header.PayloadLength);
        Assert.Equal((ushort)1, header.Sequence);

        var ack = HelloAckMessage.ReadPayloadFrom(bytes.AsSpan(ProtocolConstants.HeaderSize));
        Assert.Equal(ProtocolConstants.HelloStatusOk, ack.Status);
        Assert.Equal(ProtocolConstants.CodecIdH264, ack.CodecId);
        Assert.Equal((ushort)1920, ack.StreamWidth);
        Assert.Equal((ushort)1200, ack.StreamHeight);
        Assert.Equal((ushort)60, ack.TargetFps);
        Assert.Equal((uint)15000000, ack.BitrateBps);
        Assert.Equal((uint)0x12345678, ack.SessionId);
    }

    [Fact]
    public void TestVector_VideoConfigSample_ValidatesAndParses()
    {
        string path = Path.Combine(TestVectorsDir, "video_config_sample.bin");
        Assert.True(File.Exists(path));

        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(37, bytes.Length);

        var header = FrameHeader.ReadFrom(bytes.AsSpan(0, ProtocolConstants.HeaderSize));
        Assert.True(header.Validate(out string? err));
        Assert.Equal(MessageType.VideoConfig, header.MsgType);
        Assert.Equal((uint)21, header.PayloadLength);

        var config = VideoConfigMessage.ReadPayloadFrom(bytes.AsSpan(ProtocolConstants.HeaderSize));
        Assert.Equal(21, config.SpsPpsData.Length);
        // Verify start codes
        Assert.Equal(0x00, config.SpsPpsData[0]);
        Assert.Equal(0x00, config.SpsPpsData[1]);
        Assert.Equal(0x00, config.SpsPpsData[2]);
        Assert.Equal(0x01, config.SpsPpsData[3]);
        Assert.Equal(0x67, config.SpsPpsData[4]); // SPS NAL
    }

    [Fact]
    public void TestVector_VideoFrameSample_ValidatesAndParses()
    {
        string path = Path.Combine(TestVectorsDir, "video_frame_sample.bin");
        Assert.True(File.Exists(path));

        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(34, bytes.Length);

        var header = FrameHeader.ReadFrom(bytes.AsSpan(0, ProtocolConstants.HeaderSize));
        Assert.True(header.Validate(out string? err));
        Assert.Equal(MessageType.VideoFrame, header.MsgType);
        Assert.Equal((byte)ProtocolConstants.VideoFlagKeyframe, header.Flags);
        Assert.Equal((uint)18, header.PayloadLength);

        var frame = VideoFrameMessage.ReadPayloadFrom(bytes.AsSpan(ProtocolConstants.HeaderSize), header.Flags);
        Assert.Equal((uint)1, frame.FrameNumber);
        Assert.Equal((ushort)2500, frame.CaptureDurationUs);
        Assert.Equal((ushort)4200, frame.EncodeDurationUs);
        Assert.True(frame.IsKeyframe);
        Assert.Equal(10, frame.NalData.Length);
    }
}
