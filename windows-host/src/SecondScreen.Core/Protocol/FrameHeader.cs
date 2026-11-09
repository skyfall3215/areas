using System.Buffers.Binary;

namespace SecondScreen.Core.Protocol;

public readonly record struct FrameHeader(
    byte Magic0,
    byte Magic1,
    byte Version,
    MessageType MsgType,
    byte Flags,
    byte Reserved,
    ushort Sequence,
    uint TimestampUs,
    uint PayloadLength)
{
    public static FrameHeader Create(
        MessageType msgType,
        uint payloadLength,
        ushort sequence = 0,
        uint timestampUs = 0,
        byte flags = 0)
    {
        return new FrameHeader(
            ProtocolConstants.Magic0,
            ProtocolConstants.Magic1,
            ProtocolConstants.Version1,
            msgType,
            flags,
            0,
            sequence,
            timestampUs,
            payloadLength);
    }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < ProtocolConstants.HeaderSize)
            throw new ArgumentException("Destination buffer is too small for FrameHeader.", nameof(destination));

        destination[0] = Magic0;
        destination[1] = Magic1;
        destination[2] = Version;
        destination[3] = (byte)MsgType;
        destination[4] = Flags;
        destination[5] = Reserved;
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..8], Sequence);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..12], TimestampUs);
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..16], PayloadLength);
    }

    public static FrameHeader ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < ProtocolConstants.HeaderSize)
            throw new ArgumentException("Source buffer is too small for FrameHeader.", nameof(source));

        byte m0 = source[0];
        byte m1 = source[1];
        byte ver = source[2];
        MessageType type = (MessageType)source[3];
        byte flags = source[4];
        byte reserved = source[5];
        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(source[6..8]);
        uint ts = BinaryPrimitives.ReadUInt32BigEndian(source[8..12]);
        uint len = BinaryPrimitives.ReadUInt32BigEndian(source[12..16]);

        return new FrameHeader(m0, m1, ver, type, flags, reserved, seq, ts, len);
    }

    public bool Validate(out string? error)
    {
        if (Magic0 != ProtocolConstants.Magic0 || Magic1 != ProtocolConstants.Magic1)
        {
            error = $"Invalid magic bytes: 0x{Magic0:X2} 0x{Magic1:X2}, expected 'SS'";
            return false;
        }

        if (Version != ProtocolConstants.Version1)
        {
            error = $"Unsupported protocol version: {Version}, expected {ProtocolConstants.Version1}";
            return false;
        }

        if (PayloadLength > ProtocolConstants.MaxPayloadLength)
        {
            error = $"Payload length exceeds maximum allowed limit: {PayloadLength} > {ProtocolConstants.MaxPayloadLength}";
            return false;
        }

        error = null;
        return true;
    }
}

