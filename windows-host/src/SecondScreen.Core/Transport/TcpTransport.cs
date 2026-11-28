using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SecondScreen.Core.Protocol;

namespace SecondScreen.Core.Transport;

public sealed class TcpTransport : ITransport
{
    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly AsyncPriorityLock _sendLock = new();
    private int _isDisposed;

    public bool IsEncrypted { get; }

    public TcpTransport(TcpClient client, Stream? customStream = null, bool isEncrypted = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.NoDelay = true;
        _client.SendBufferSize = 32 * 1024;
        _client.ReceiveBufferSize = 64 * 1024;
        try
        {
            if (_client.Client.AddressFamily == AddressFamily.InterNetwork)
                _client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0x88);
        }
        catch { }
        _stream = customStream ?? _client.GetStream();
        IsEncrypted = isEncrypted;
    }

    public static X509Certificate2 CreateEphemeralServerCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=AreasSecondScreen", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
        // Re-import via PFX so Windows Schannel can access the private key
        return new X509Certificate2(ephemeral.Export(X509ContentType.Pfx));
    }

    public static async Task<TcpTransport> CreateServerTransportAsync(
        TcpClient client,
        X509Certificate2? serverCert,
        CancellationToken cancellationToken = default)
    {
        client.NoDelay = true;
        if (serverCert != null)
        {
            byte[] peek = new byte[1];
            int peeked = await client.Client.ReceiveAsync(peek, SocketFlags.Peek, cancellationToken).ConfigureAwait(false);
            if (peeked > 0 && peek[0] == 0x16) // TLS ClientHello record
            {
                var sslStream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await sslStream.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = serverCert,
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    },
                    cancellationToken).ConfigureAwait(false);
                return new TcpTransport(client, sslStream, isEncrypted: true);
            }
        }

        return new TcpTransport(client);
    }

    public bool IsConnected => _isDisposed == 0 && _client.Connected;

    public async Task SendPacketAsync(
        FrameHeader header,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed != 0, this);

        bool highPriority = header.MsgType != MessageType.VideoFrame;
        await _sendLock.WaitAsync(highPriority, cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
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

internal sealed class AsyncPriorityLock : IDisposable
{
    private readonly object _sync = new();
    private readonly Queue<Waiter> _highPriorityWaiters = new();
    private readonly Queue<Waiter> _normalPriorityWaiters = new();
    private bool _isHeld;
    private bool _disposed;

    private sealed class Waiter
    {
        public readonly TaskCompletionSource Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;
    }

    public Task WaitAsync(bool highPriority, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return Task.FromException(new ObjectDisposedException(nameof(TcpTransport)));
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            if (!_isHeld)
            {
                _isHeld = true;
                return Task.CompletedTask;
            }

            var waiter = new Waiter();
            if (cancellationToken.CanBeCanceled)
            {
                waiter.Registration = cancellationToken.Register(
                    static (state, ct) => ((Waiter)state!).Tcs.TrySetCanceled(ct),
                    waiter);
            }

            if (waiter.Tcs.Task.IsCompleted)
            {
                waiter.Registration.Unregister();
                return waiter.Tcs.Task;
            }

            var queue = highPriority ? _highPriorityWaiters : _normalPriorityWaiters;
            queue.Enqueue(waiter);
            return waiter.Tcs.Task;
        }
    }

    public void Release()
    {
        Waiter? granted = null;
        List<Waiter>? skipped = null;

        lock (_sync)
        {
            if (!_isHeld)
            {
                return;
            }

            while (true)
            {
                Waiter next;
                if (_highPriorityWaiters.Count > 0)
                {
                    next = _highPriorityWaiters.Dequeue();
                }
                else if (_normalPriorityWaiters.Count > 0)
                {
                    next = _normalPriorityWaiters.Dequeue();
                }
                else
                {
                    _isHeld = false;
                    break;
                }

                if (next.Tcs.TrySetResult())
                {
                    granted = next;
                    break;
                }

                skipped ??= new List<Waiter>();
                skipped.Add(next);
            }
        }

        if (granted != null)
        {
            granted.Registration.Dispose();
        }

        if (skipped != null)
        {
            foreach (var waiter in skipped)
            {
                waiter.Registration.Dispose();
            }
        }
    }

    public void Dispose()
    {
        List<Waiter>? toFail = null;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            int count = _highPriorityWaiters.Count + _normalPriorityWaiters.Count;
            if (count > 0)
            {
                toFail = new List<Waiter>(count);
                while (_highPriorityWaiters.Count > 0)
                {
                    toFail.Add(_highPriorityWaiters.Dequeue());
                }
                while (_normalPriorityWaiters.Count > 0)
                {
                    toFail.Add(_normalPriorityWaiters.Dequeue());
                }
            }
        }

        if (toFail != null)
        {
            var ex = new ObjectDisposedException(nameof(TcpTransport));
            foreach (var waiter in toFail)
            {
                waiter.Registration.Dispose();
                waiter.Tcs.TrySetException(ex);
            }
        }
    }
}

