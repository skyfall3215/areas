using System.Net.Sockets;
using SecondScreen.Core.Protocol;

namespace SecondScreen.Core.Transport;

public sealed class TcpTransport : ITransport
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _isDisposed;

    public TcpTransport(TcpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.NoDelay = true;
        _client.SendBufferSize = 64 * 1024;
        _client.ReceiveBufferSize = 64 * 1024;
        _stream = _client.GetStream();
    }

    public bool IsConnected => _isDisposed == 0 && _client.Connected;

    public async Task SendPacketAsync(
        FrameHeader header,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed != 0, this);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PacketWriter.WritePacketAsync(_stream, header, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<(FrameHeader Header, byte[] Payload)> ReceivePacketAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
        return await PacketReader.ReadPacketAsync(_stream, cancellationToken).ConfigureAwait(false);
    }

    public Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
        {
            try { _stream.Dispose(); } catch { }
            try { _client.Close(); } catch { }
            _sendLock.Dispose();
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }
}

