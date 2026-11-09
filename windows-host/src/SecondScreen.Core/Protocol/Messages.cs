using System.Buffers.Binary;
using System.Text;

namespace SecondScreen.Core.Protocol;

public sealed record HelloMessage(
    ushort NativeWidth,
    ushort NativeHeight,
    ushort MaxFps,
    ushort PreferredBitrateKbps,
    byte FeatureFlags,
    string DeviceName)
{
    public const int PayloadSize = 32;

    public void WritePayloadTo(Span<byte> destination)
    {
        if (destination.Length < PayloadSize)
            throw new ArgumentException("Buffer too small for HelloMessage payload.", nameof(destination));

        destination.Slice(0, PayloadSize).Clear();
        BinaryPrimitives.WriteUInt16BigEndian(destination[0..2], NativeWidth);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..4], NativeHeight);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..6], MaxFps);
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..8], PreferredBitrateKbps);
        destination[8] = FeatureFlags;
        // 9..15 reserved (zeros)
        byte[] nameBytes = Encoding.UTF8.GetBytes(DeviceName ?? string.Empty);
        int copyLen = Math.Min(nameBytes.Length, 16);
        nameBytes.AsSpan(0, copyLen).CopyTo(destination.Slice(16, copyLen));
    }

    public static HelloMessage ReadPayloadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < PayloadSize)
            throw new ArgumentException("Buffer too small for HelloMessage payload.", nameof(source));

        ushort width = BinaryPrimitives.ReadUInt16BigEndian(source[0..2]);
        ushort height = BinaryPrimitives.ReadUInt16BigEndian(source[2..4]);
        ushort fps = BinaryPrimitives.ReadUInt16BigEndian(source[4..6]);
        ushort bitrate = BinaryPrimitives.ReadUInt16BigEndian(source[6..8]);
        byte features = source[8];
        
        // Read 16-byte DeviceName, trim null terminators
        ReadOnlySpan<byte> nameSpan = source.Slice(16, 16);
        int nullIdx = nameSpan.IndexOf((byte)0);
        if (nullIdx >= 0)
            nameSpan = nameSpan.Slice(0, nullIdx);
        string deviceName = Encoding.UTF8.GetString(nameSpan);

        return new HelloMessage(width, height, fps, bitrate, features, deviceName);
    }
}

public sealed record HelloAckMessage(
    byte Status,
    byte CodecId,
    ushort StreamWidth,
    ushort StreamHeight,
    ushort TargetFps,
    uint BitrateBps,
    uint SessionId)
{
    public const int PayloadSize = 16;

    public void WritePayloadTo(Span<byte> destination)
    {
        if (destination.Length < PayloadSize)
            throw new ArgumentException("Buffer too small for HelloAckMessage payload.", nameof(destination));

        destination[0] = Status;
        destination[1] = CodecId;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..4], StreamWidth);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..6], StreamHeight);
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..8], TargetFps);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..12], BitrateBps);
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..16], SessionId);
    }

    public static HelloAckMessage ReadPayloadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < PayloadSize)
            throw new ArgumentException("Buffer too small for HelloAckMessage payload.", nameof(source));

        byte status = source[0];
        byte codecId = source[1];
        ushort width = BinaryPrimitives.ReadUInt16BigEndian(source[2..4]);
        ushort height = BinaryPrimitives.ReadUInt16BigEndian(source[4..6]);
        ushort fps = BinaryPrimitives.ReadUInt16BigEndian(source[6..8]);
        uint bitrate = BinaryPrimitives.ReadUInt32BigEndian(source[8..12]);
        uint sessionId = BinaryPrimitives.ReadUInt32BigEndian(source[12..16]);

        return new HelloAckMessage(status, codecId, width, height, fps, bitrate, sessionId);
    }
}

public sealed record VideoConfigMessage(byte[] SpsPpsData)
{
    public void WritePayloadTo(Span<byte> destination)
    {
        if (destination.Length < SpsPpsData.Length)
            throw new ArgumentException("Buffer too small for VideoConfigMessage payload.", nameof(destination));
        SpsPpsData.CopyTo(destination);
    }

    public static VideoConfigMessage ReadPayloadFrom(ReadOnlySpan<byte> source)
    {
        return new VideoConfigMessage(source.ToArray());
    }
}

public sealed record VideoFrameMessage(
    uint FrameNumber,
    ushort CaptureDurationUs,
    ushort EncodeDurationUs,
    byte[] NalData,
    bool IsKeyframe = false,
    bool IsCursorEmbedded = false)
{
    public const int HeaderOverhead = 8;

    public int PayloadSize => HeaderOverhead + NalData.Length;

    public byte GetFlags()
    {
        byte flags = 0;
        if (IsKeyframe) flags |= ProtocolConstants.VideoFlagKeyframe;
        if (IsCursorEmbedded) flags |= ProtocolConstants.VideoFlagCursorEmbedded;
        return flags;
    }

    public void WritePayloadTo(Span<byte> destination)
    {
        if (destination.Length < PayloadSize)
            throw new ArgumentException("Buffer too small for VideoFrameMessage payload.", nameof(destination));

        BinaryPrimitives.WriteUInt32BigEndian(destination[0..4], FrameNumber);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..6], CaptureDurationUs);
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..8], EncodeDurationUs);
        NalData.CopyTo(destination[8..]);
    }

    public static VideoFrameMessage ReadPayloadFrom(ReadOnlySpan<byte> source, byte flags = 0)
    {
        if (source.Length < HeaderOverhead)
            throw new ArgumentException("Buffer too small for VideoFrameMessage payload.", nameof(source));

        uint frameNumber = BinaryPrimitives.ReadUInt32BigEndian(source[0..4]);
        ushort captureDur = BinaryPrimitives.ReadUInt16BigEndian(source[4..6]);
        ushort encodeDur = BinaryPrimitives.ReadUInt16BigEndian(source[6..8]);
        byte[] nalData = source[8..].ToArray();

        bool isKeyframe = (flags & ProtocolConstants.VideoFlagKeyframe) != 0;
        bool isCursor = (flags & ProtocolConstants.VideoFlagCursorEmbedded) != 0;

        return new VideoFrameMessage(frameNumber, captureDur, encodeDur, nalData, isKeyframe, isCursor);
    }
}

public sealed record KeyframeReqMessage(uint LastGoodFrameNumber)
{
    public const int PayloadSize = 4;

    public void WritePayloadTo(Span<byte> destination)
    {
        if (destination.Length < PayloadSize)
            throw new ArgumentException("Buffer too small for KeyframeReqMessage payload.", nameof(destination));
        BinaryPrimitives.WriteUInt32BigEndian(destination[0..4], LastGoodFrameNumber);
    }

    public static KeyframeReqMessage ReadPayloadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < PayloadSize)
            throw new ArgumentException("Buffer too small for KeyframeReqMessage payload.", nameof(source));
        uint frameNum = BinaryPrimitives.ReadUInt32BigEndian(source[0..4]);
        return new KeyframeReqMessage(frameNum);
    }
}

public sealed record ErrorMessage(ushort ErrorCode, string Message)
{
    public void WritePayloadTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination[0..2], ErrorCode);
        byte[] msgBytes = Encoding.UTF8.GetBytes(Message ?? string.Empty);
        msgBytes.CopyTo(destination[2..]);
    }

    public static ErrorMessage ReadPayloadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < 2)
            throw new ArgumentException("Buffer too small for ErrorMessage payload.", nameof(source));
        ushort code = BinaryPrimitives.ReadUInt16BigEndian(source[0..2]);
        string msg = Encoding.UTF8.GetString(source[2..]);
        return new ErrorMessage(code, msg);
    }
}

public sealed record PingMessage(uint PingId, uint OriginTimeUs)
{
    public const int PayloadSize = 8;

    public void WritePayloadTo(Span<byte> destination)
    {
        if (destination.Length < PayloadSize)
            throw new ArgumentException("Buffer too small for PingMessage payload.", nameof(destination));
        BinaryPrimitives.WriteUInt32BigEndian(destination[0..4], PingId);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..8], OriginTimeUs);
    }

    public static PingMessage ReadPayloadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < PayloadSize)
            throw new ArgumentException("Buffer too small for PingMessage payload.", nameof(source));
        uint id = BinaryPrimitives.ReadUInt32BigEndian(source[0..4]);
        uint time = BinaryPrimitives.ReadUInt32BigEndian(source[4..8]);
        return new PingMessage(id, time);
    }
}

public sealed record PongMessage(
    uint PingId,
    uint OriginTimeUs,
    uint SentBitrateKbps = 0,
    uint SentPacketsCount = 0)
{
    public const int PayloadSize = 16;

    public void WritePayloadTo(Span<byte> destination)
    {
        if (destination.Length < 8)
            throw new ArgumentException("Buffer too small for PongMessage payload.", nameof(destination));
        BinaryPrimitives.WriteUInt32BigEndian(destination[0..4], PingId);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..8], OriginTimeUs);
        if (destination.Length >= 16)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination[8..12], SentBitrateKbps);
            BinaryPrimitives.WriteUInt32BigEndian(destination[12..16], SentPacketsCount);
        }
    }

    public static PongMessage ReadPayloadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < 8)
            throw new ArgumentException("Buffer too small for PongMessage payload.", nameof(source));
        uint id = BinaryPrimitives.ReadUInt32BigEndian(source[0..4]);
        uint time = BinaryPrimitives.ReadUInt32BigEndian(source[4..8]);
        uint bitrate = source.Length >= 16 ? BinaryPrimitives.ReadUInt32BigEndian(source[8..12]) : 0;
        uint packets = source.Length >= 16 ? BinaryPrimitives.ReadUInt32BigEndian(source[12..16]) : 0;
        return new PongMessage(id, time, bitrate, packets);
    }
}

public sealed record DisconnectMessage(byte Reason)
{
    public const int PayloadSize = 1;

    public void WritePayloadTo(Span<byte> destination)
    {
        if (destination.Length < 1)
            throw new ArgumentException("Buffer too small for DisconnectMessage payload.", nameof(destination));
        destination[0] = Reason;
    }

    public static DisconnectMessage ReadPayloadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < 1)
            throw new ArgumentException("Buffer too small for DisconnectMessage payload.", nameof(source));
        return new DisconnectMessage(source[0]);
    }
}

