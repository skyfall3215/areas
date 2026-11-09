namespace SecondScreen.Core.Protocol;

public static class PacketWriter
{
    public static byte[] SerializePacket(FrameHeader header, ReadOnlySpan<byte> payload)
    {
        byte[] buffer = new byte[ProtocolConstants.HeaderSize + payload.Length];
        header.WriteTo(buffer.AsSpan(0, ProtocolConstants.HeaderSize));
        if (payload.Length > 0)
        {
            payload.CopyTo(buffer.AsSpan(ProtocolConstants.HeaderSize));
        }
        return buffer;
    }

    public static async Task WritePacketAsync(
        Stream stream,
        FrameHeader header,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[ProtocolConstants.HeaderSize + payload.Length];
        header.WriteTo(buffer.AsSpan(0, ProtocolConstants.HeaderSize));
        if (!payload.IsEmpty)
        {
            payload.Span.CopyTo(buffer.AsSpan(ProtocolConstants.HeaderSize));
        }

        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

