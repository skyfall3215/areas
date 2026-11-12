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
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _isDisposed;

    public bool IsEncrypted { get; }

    public TcpTransport(TcpClient client, Stream? customStream = null, bool isEncrypted = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _client.NoDelay = true;
        _client.SendBufferSize = 64 * 1024;
        _client.ReceiveBufferSize = 512 * 1024;
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

