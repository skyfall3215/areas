using System.Buffers;

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
        int totalLength = ProtocolConstants.HeaderSize + payload.Length;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            header.WriteTo(buffer.AsSpan(0, ProtocolConstants.HeaderSize));
            if (!payload.IsEmpty)
            {
                payload.Span.CopyTo(buffer.AsSpan(ProtocolConstants.HeaderSize, payload.Length));
            }

            await stream.WriteAsync(buffer.AsMemory(0, totalLength), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

