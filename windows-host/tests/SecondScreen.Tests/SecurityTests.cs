using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using SecondScreen.Core.Protocol;
using SecondScreen.Core.Transport;
using Xunit;

namespace SecondScreen.Tests;

public class SecurityTests
{
    [Fact]
    public void HelloMessage_Preserves_SixDigit_PinCode()
    {
        var original = new HelloMessage(
            NativeWidth: 2304,
            NativeHeight: 1440,
            MaxFps: 60,
            PreferredBitrateKbps: 15000,
            FeatureFlags: ProtocolConstants.FeatureFlagTouch,
            DeviceName: "GalaxyTab",
            PinCode: "482910");

        byte[] buffer = new byte[HelloMessage.PayloadSize];
        original.WritePayloadTo(buffer);

        var decoded = HelloMessage.ReadPayloadFrom(buffer);
        Assert.Equal("482910", decoded.PinCode);
        Assert.Equal("GalaxyTab", decoded.DeviceName);
    }

    [Fact]
    public async Task TcpTransport_TlsHandshake_And_EncryptedPacketRoundtrip_Succeeds()
    {
        using var cert = TcpTransport.CreateEphemeralServerCertificate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var acceptTask = listener.AcceptTcpClientAsync(cts.Token).AsTask();

        using var clientTcp = new TcpClient();
        await clientTcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);

        using var sslClient = new SslStream(clientTcp.GetStream(), leaveInnerStreamOpen: false,
            (_, _, _, _) => true);

        var serverAcceptClient = await acceptTask;
        var serverTransportTask = TcpTransport.CreateServerTransportAsync(serverAcceptClient, cert, cts.Token);
        var clientAuthTask = sslClient.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            },
            cts.Token);

        await Task.WhenAll(serverTransportTask, clientAuthTask);
        await using var serverTransport = await serverTransportTask;
        await using var clientTransport = new TcpTransport(clientTcp, sslClient, isEncrypted: true);

        Assert.True(serverTransport.IsEncrypted);

        var hello = new HelloMessage(2304, 1440, 60, 15000, 1, "TestTab", "918273");
        byte[] payload = new byte[HelloMessage.PayloadSize];
        hello.WritePayloadTo(payload);
        var header = FrameHeader.Create(MessageType.Hello, (uint)payload.Length, sequence: 1);

        await clientTransport.SendPacketAsync(header, payload, cts.Token);
        var (rxHeader, rxPayload) = await serverTransport.ReceivePacketAsync(cts.Token);

        Assert.Equal(MessageType.Hello, rxHeader.MsgType);
        var rxHello = HelloMessage.ReadPayloadFrom(rxPayload);
        Assert.Equal("918273", rxHello.PinCode);
    }
}

