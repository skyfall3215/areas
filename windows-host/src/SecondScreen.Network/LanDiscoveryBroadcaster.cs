using System.Net;
using System.Net.Sockets;
using SecondScreen.Core.Protocol;

namespace SecondScreen.Network;

public sealed class LanDiscoveryBroadcaster : IDisposable, IAsyncDisposable
{
    private readonly int _servicePort;
    private readonly int _discoveryPort;
    private readonly TimeSpan _broadcastInterval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _broadcastTask;
    private UdpClient? _udpClient;
    private int _isDisposed;

    public LanDiscoveryBroadcaster(
        int servicePort = ProtocolConstants.DefaultPort,
        int discoveryPort = ProtocolConstants.DiscoveryPort,
        TimeSpan? broadcastInterval = null)
    {
        _servicePort = servicePort;
        _discoveryPort = discoveryPort;
        _broadcastInterval = broadcastInterval ?? TimeSpan.FromSeconds(1);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
        if (_broadcastTask != null) return;

        try
        {
            _udpClient = new UdpClient();
            _udpClient.EnableBroadcast = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UDP Keşif] UdpClient başlatılamadı: {ex.Message}");
            return;
        }

        _broadcastTask = Task.Run(() => BroadcastLoopAsync(_cts.Token));
    }

    private async Task BroadcastLoopAsync(CancellationToken ct)
    {
        var targetEndpoint = new IPEndPoint(IPAddress.Broadcast, _discoveryPort);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var localIps = NetworkHelper.GetLocalIPv4Addresses();
                var beacon = new DiscoveryBeacon
                {
                    HostName = Environment.MachineName,
                    Port = _servicePort,
                    Addresses = localIps
                };

                byte[] data = beacon.ToBytes();
                if (_udpClient != null)
                {
                    await _udpClient.SendAsync(data, data.Length, targetEndpoint).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Network down or broadcast failure, retry next tick
            }

            try
            {
                await Task.Delay(_broadcastInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
        {
            _cts.Cancel();
            try { _udpClient?.Close(); } catch { }
            try { _udpClient?.Dispose(); } catch { }
            _cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_broadcastTask != null)
        {
            try { await _broadcastTask.ConfigureAwait(false); } catch { }
        }
    }
}
