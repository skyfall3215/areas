namespace SecondScreen.Core.Protocol;

public static class PacketReader
{
    public static async Task<(FrameHeader Header, byte[] Payload)> ReadPacketAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        byte[] headerBuffer = new byte[ProtocolConstants.HeaderSize];
        await ReadExactBytesAsync(stream, headerBuffer, cancellationToken).ConfigureAwait(false);

        FrameHeader header = FrameHeader.ReadFrom(headerBuffer);
        if (!header.Validate(out string? error))
        {
            throw new InvalidDataException($"Protocol error: {error}");
        }

        byte[] payload;
        if (header.PayloadLength > 0)
        {
            payload = new byte[header.PayloadLength];
            await ReadExactBytesAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            payload = Array.Empty<byte>();
        }

        return (header, payload);
    }

    public static async Task ReadExactBytesAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.Slice(totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Unexpected end of stream while reading packet. Read {totalRead} of {buffer.Length} bytes.");
            }
            totalRead += read;
        }
    }
}

