using SecondScreen.Core.Protocol;

namespace SecondScreen.Core.Transport;

public interface ITransport : IAsyncDisposable
{
    bool IsConnected { get; }
    Task SendPacketAsync(FrameHeader header, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
    Task<(FrameHeader Header, byte[] Payload)> ReceivePacketAsync(CancellationToken cancellationToken = default);
    Task CloseAsync();
}

