using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using SecondScreen.Core.Diagnostics;
using SecondScreen.Core.Protocol;
using SecondScreen.Core.Transport;
using SecondScreen.Display;
using SecondScreen.Input;
using SecondScreen.Media.Capture;
using SecondScreen.Media.Encoding;
using SecondScreen.Network;

namespace SecondScreen.App;

public sealed class SecondScreenServer : IDisposable
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;
    private LanDiscoveryBroadcaster? _broadcaster;
    private TcpListener? _listener;

    public bool IsRunning { get; private set; }
    public int Port { get; }
    public string? ConnectedClientName { get; private set; }
    public string? ConnectedClientDetails { get; private set; }
    public bool IsClientConnected => !string.IsNullOrEmpty(ConnectedClientName);

    public SecondScreenServer(int port = ProtocolConstants.DefaultPort)
    {
        Port = port;
    }

    public event Action<string>? OnLog;
    public event Action<string>? OnStatusChanged;
    public event Action<string?>? OnClientSessionChanged;
    public event Action<double, double, double>? OnMetricsUpdated; // rttMs, fps, bitrateMbps

    public async Task StartAsync()
    {
        if (IsRunning) return;

        TimeBeginPeriod(1);
        _serverCts = new CancellationTokenSource();
        IsRunning = true;
        OnStatusChanged?.Invoke("Başlatılıyor...");
        Log("[+] Areas Sunucusu başlatılıyor...");

        // 1. USB Tunnel (ADB Reverse) - only in standard port
        if (Port == ProtocolConstants.DefaultPort)
        {
            await RefreshUsbTunnelAsync();
        }

        // 2. UDP Discovery Broadcaster
        if (Port == ProtocolConstants.DefaultPort)
        {
            try
            {
                _broadcaster = new LanDiscoveryBroadcaster();
                _broadcaster.Start();
                Log($"[+] UDP Keşif Yayını (Port: {ProtocolConstants.DiscoveryPort}) aktif.");
            }
            catch (Exception ex)
            {
                Log($"[!] UDP Keşif hatası: {ex.Message}");
            }
        }

        // 3. Start TCP Listener
        try
        {
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();
            Log($"[+] TCP Dinleyici 0.0.0.0:{Port} üzerinde hazır (USB + Wi-Fi + Hotspot).");
            OnStatusChanged?.Invoke("Dinliyor (Bağlantı Bekleniyor)");
        }
        catch (Exception ex)
        {
            Log($"[!] TCP Dinleyici başlatılamadı: {ex.Message}");
            OnStatusChanged?.Invoke("Hata: Port Dinlenemiyor");
            IsRunning = false;
            return;
        }

        _serverTask = Task.Run(() => ServerLoopAsync(_serverCts.Token));
    }

    public async Task RefreshUsbTunnelAsync()
    {
        try
        {
            var (success, msg) = await AdbHelper.ReversePortAsync(Port, _serverCts?.Token ?? CancellationToken.None);
            Log(success ? $"[OK] USB: {msg}" : $"[BİLGİ] USB: {msg}");
        }
        catch (Exception ex)
        {
            Log($"[!] ADB tünel hatası: {ex.Message}");
        }
    }

    private async Task ServerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_listener == null) break;
                using var tcpClient = await _listener.AcceptTcpClientAsync(ct);

                var remoteEp = tcpClient.Client.RemoteEndPoint?.ToString() ?? "Bilinmiyor";
                bool isUsb = remoteEp.StartsWith("127.0.0.1") || remoteEp.StartsWith("[::1]");
                bool isHotspot = HotspotManager.IsHotspotClient(remoteEp);
                string modeStr = isUsb ? "USB-C (Kablo)" : isHotspot ? "Hotspot (192.168.137.1)" : "Wi-Fi LAN";

                Log($"[+] Tablet bağlandı: {remoteEp} [Mod: {modeStr}]");

                await HandleClientSessionAsync(tcpClient, modeStr, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"[!] Bağlantı hatası: {ex.Message}");
            }
            finally
            {
                bool wasConnected = IsClientConnected;
                ConnectedClientName = null;
                ConnectedClientDetails = null;
                if (wasConnected)
                {
                    try { DisplayTopologyManager.RestoreInternalOnly(); } catch { }
                }
                OnClientSessionChanged?.Invoke(null);
                if (IsRunning)
                {
                    OnStatusChanged?.Invoke("Dinliyor (Bağlantı Bekleniyor)");
                }
            }
        }
    }

    private async Task HandleClientSessionAsync(TcpClient tcpClient, string transportMode, CancellationToken ct)
    {
        await using var transport = new TcpTransport(tcpClient);
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var rttTracker = new RttTracker();

        // 1. Wait for HELLO
        var (header, payload) = await transport.ReceivePacketAsync(sessionCts.Token);
        if (header.MsgType != MessageType.Hello)
        {
            Log($"[!] Beklenmeyen mesaj: {header.MsgType}, oturum sonlandırılıyor.");
            return;
        }

        var hello = HelloMessage.ReadPayloadFrom(payload);
        ConnectedClientName = string.IsNullOrWhiteSpace(hello.DeviceName) ? "Android Tablet" : hello.DeviceName;
        ConnectedClientDetails = $"{ConnectedClientName} ({hello.NativeWidth}x{hello.NativeHeight} @ {hello.MaxFps}Hz) via {transportMode}";
        OnClientSessionChanged?.Invoke(ConnectedClientDetails);
        OnStatusChanged?.Invoke($"Bağlı: {ConnectedClientName}");
        Log($"[+] HELLO alındı: {ConnectedClientDetails}, İstenen Bitrate: {hello.PreferredBitrateKbps} kbps");

        // 2. Automatically extend Windows desktop so the virtual second screen is attached
        try
        {
            if (DisplayTopologyManager.ExtendDesktop())
            {
                Log("[+] Tablet bağlandı: Masaüstü otomatik olarak İkinci Ekran (Extend) moduna geçirildi.");
            }
            // Wait up to 800ms for the secondary display to appear in Windows topology
            for (int waitIdx = 0; waitIdx < 8; waitIdx++)
            {
                await Task.Delay(100, sessionCts.Token);
                if (DisplayEnumerator.GetDisplays().Any(d => !d.IsPrimary))
                    break;
            }
        }
        catch { }

        // Select target display (prioritizing 2nd / virtual screen)
        var targetDisplay = DisplayEnumerator.GetTargetDisplay();
        Log($"[+] Hedef Monitör: {targetDisplay.DeviceName} ({targetDisplay.FriendlyName}) - {targetDisplay.Bounds.Width}x{targetDisplay.Bounds.Height}");

        ushort streamWidth = targetDisplay.Bounds.Width > 0 ? (ushort)(targetDisplay.Bounds.Width & ~1) : (ushort)(hello.NativeWidth & ~1);
        ushort streamHeight = targetDisplay.Bounds.Height > 0 ? (ushort)(targetDisplay.Bounds.Height & ~1) : (ushort)(hello.NativeHeight & ~1);
        ushort targetFps = 60;
        uint bitrateBps = hello.PreferredBitrateKbps > 0 ? (uint)hello.PreferredBitrateKbps * 1000 : 20_000_000;
        uint sessionId = (uint)Random.Shared.Next(1, int.MaxValue);

        // 4. Send HELLO_ACK
        var ack = new HelloAckMessage(
            Status: ProtocolConstants.HelloStatusOk,
            CodecId: ProtocolConstants.CodecIdH264,
            StreamWidth: streamWidth,
            StreamHeight: streamHeight,
            TargetFps: targetFps,
            BitrateBps: bitrateBps,
            SessionId: sessionId);

        byte[] ackPayload = new byte[HelloAckMessage.PayloadSize];
        ack.WritePayloadTo(ackPayload);
        var ackHeader = FrameHeader.Create(MessageType.HelloAck, (uint)ackPayload.Length, sequence: 1);
        await transport.SendPacketAsync(ackHeader, ackPayload, sessionCts.Token);
        Log($"[+] Akış başlatıldı: {streamWidth}x{streamHeight} @ {targetFps} FPS, {bitrateBps / 1_000_000} Mbps");

        int sequence = 2;
        uint sentFrameCount = 0;
        int requestKeyframeFlag = 0;

        using var touchInjector = new Win32TouchInjector();
        touchInjector.ScreenBounds = targetDisplay.Bounds;
        touchInjector.Initialize(10);

        var keyboardInjector = new Win32KeyboardInjector();

        // 6. Receive background packets
        var receiveTask = Task.Run(async () =>
        {
            try
            {
                GdiScreenCapture.EnsureThreadDesktopAttached();
                while (!sessionCts.Token.IsCancellationRequested)
                {
                    var (inHeader, inPayload) = await transport.ReceivePacketAsync(sessionCts.Token);
                    if (inHeader.MsgType == MessageType.KeyframeReq)
                    {
                        Interlocked.Exchange(ref requestKeyframeFlag, 1);
                    }
                    else if (inHeader.MsgType == MessageType.Ping)
                    {
                        var ping = PingMessage.ReadPayloadFrom(inPayload);
                        var pong = new PongMessage(ping.PingId, ping.OriginTimeUs, (uint)(bitrateBps / 1000), sentFrameCount);
                        byte[] pongPayload = new byte[PongMessage.PayloadSize];
                        pong.WritePayloadTo(pongPayload);

                        var pongHeader = FrameHeader.Create(
                            MessageType.Pong,
                            (uint)pongPayload.Length,
                            sequence: (ushort)(Interlocked.Increment(ref sequence) & 0xFFFF),
                            timestampUs: rttTracker.CurrentTimeUs);

                        await transport.SendPacketAsync(pongHeader, pongPayload, sessionCts.Token);
                    }
                    else if (inHeader.MsgType == MessageType.Pong)
                    {
                        var pong = PongMessage.ReadPayloadFrom(inPayload);
                        if (rttTracker.RecordPong(pong, rttTracker.CurrentTimeUs))
                        {
                            OnMetricsUpdated?.Invoke(rttTracker.LastRttMs, 60.0, bitrateBps / 1_000_000.0);
                        }
                    }
                    else if (inHeader.MsgType == MessageType.TouchEvent)
                    {
                        var touchMsg = TouchEventMessage.ReadPayloadFrom(inPayload);
                        touchInjector.InjectTouch(touchMsg);
                    }
                    else if (inHeader.MsgType == MessageType.KeyboardEvent)
                    {
                        var keyMsg = KeyboardEventMessage.ReadPayloadFrom(inPayload);
                        keyboardInjector.InjectKey(keyMsg);
                    }
                    else if (inHeader.MsgType == MessageType.Disconnect)
                    {
                        Log("[i] Tablet normal bağlantı kapatma bildirdi.");
                        sessionCts.Cancel();
                        break;
                    }
                }
            }
            catch
            {
                sessionCts.Cancel();
            }
        }, sessionCts.Token);

        // 7. Ping task
        var pingTask = Task.Run(async () =>
        {
            try
            {
                while (!sessionCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(1000, sessionCts.Token);
                    var ping = rttTracker.CreatePing();
                    byte[] pingPayload = new byte[PingMessage.PayloadSize];
                    ping.WritePayloadTo(pingPayload);

                    var pingHeader = FrameHeader.Create(
                        MessageType.Ping,
                        (uint)pingPayload.Length,
                        sequence: (ushort)(Interlocked.Increment(ref sequence) & 0xFFFF),
                        timestampUs: ping.OriginTimeUs);

                    await transport.SendPacketAsync(pingHeader, pingPayload, sessionCts.Token);
                }
            }
            catch { }
        }, sessionCts.Token);

        // 8. Decoupled video sender (small capacity = zero bufferbloat, ultra-low latency)
        var videoChannel = Channel.CreateBounded<(FrameHeader Header, byte[] Payload)>(
            new BoundedChannelOptions(16)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });

        var senderTask = Task.Run(async () =>
        {
            try
            {
                var reader = videoChannel.Reader;
                while (await reader.WaitToReadAsync(sessionCts.Token).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var item))
                    {
                        await transport.SendPacketAsync(item.Header, item.Payload, sessionCts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch { }
        }, sessionCts.Token);

        // 9. Dedicated synchronous capture & encode thread (guarantees clean WinSta0\Default desktop attachment and 60.0 FPS pacing)
        var captureCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        uint finalFrameNumber = 0;

        var captureThread = new Thread(() =>
        {
            uint frameNumber = 0;
            try
            {
                GdiScreenCapture.EnsureThreadDesktopAttached();

                using var capture = new ScreenCaptureEngine();
                bool captureOk = capture.Initialize(targetDisplay.DeviceName, targetDisplay.Bounds);

                using var encoder = new MfH264Encoder();
                encoder.Initialize(streamWidth, streamHeight, targetFps, (int)bitrateBps);

                long targetFrameIntervalTicks = Stopwatch.Frequency / targetFps;
                var stopwatch = Stopwatch.StartNew();
                long nextTargetTicks = stopwatch.ElapsedTicks;
                bool configSent = false;
                bool sawFirstNewDxgiFrame = false;
                byte[] testPatternBgra = new byte[streamWidth * streamHeight * 4];

                byte[]? lastValidBgra = null;

                while (!sessionCts.Token.IsCancellationRequested)
                {
                    long frameStartTicks = stopwatch.ElapsedTicks;
                    nextTargetTicks += targetFrameIntervalTicks;
                    if (nextTargetTicks < frameStartTicks)
                    {
                        // Reset target if a single frame took slightly longer than 16.6ms so we don't burst
                        nextTargetTicks = frameStartTicks + targetFrameIntervalTicks;
                    }
                    long frameStartUs = (frameStartTicks * 1_000_000) / Stopwatch.Frequency;

                    try
                    {
                        if (Interlocked.Exchange(ref requestKeyframeFlag, 0) == 1 ||
                            frameNumber == 45 || frameNumber == 90 || frameNumber == 150)
                        {
                            encoder.RequestKeyframe();
                        }

                        byte[] bgraData;
                        ushort captureDurationUs = 0;

                        if (captureOk && capture.IsInitialized)
                        {
                            var capSw = Stopwatch.StartNew();
                            var frame = capture.AcquireNextFrame(0);
                            capSw.Stop();
                            captureDurationUs = (ushort)Math.Min(capSw.Elapsed.TotalMicroseconds, ushort.MaxValue);

                            if (frame != null && frame.RgbaData != null && frame.RgbaData.Length >= streamWidth * streamHeight * 4)
                            {
                                bgraData = frame.RgbaData;
                                lastValidBgra = bgraData;
                                // When DWM finishes painting the newly extended virtual desktop for the first time,
                                // or immediately after returning from a UAC prompt, force an IDR keyframe!
                                if (frame.ForceKeyframe || (frame.IsNew && !sawFirstNewDxgiFrame))
                                {
                                    sawFirstNewDxgiFrame = true;
                                    encoder.RequestKeyframe();
                                }
                            }
                            else if (lastValidBgra != null)
                            {
                                bgraData = lastValidBgra;
                                sawFirstNewDxgiFrame = false;
                            }
                            else
                            {
                                GenerateTestPattern(testPatternBgra, streamWidth, streamHeight, frameNumber);
                                bgraData = testPatternBgra;
                            }
                        }
                        else if (lastValidBgra != null)
                        {
                            bgraData = lastValidBgra;
                            sawFirstNewDxgiFrame = false;
                            captureDurationUs = 500;
                        }
                        else
                        {
                            GenerateTestPattern(testPatternBgra, streamWidth, streamHeight, frameNumber);
                            bgraData = testPatternBgra;
                            captureDurationUs = 500;
                        }

                        // Encode
                        var encSw = Stopwatch.StartNew();
                        var encoded = encoder.EncodeBgraFrame(bgraData, frameStartUs);
                        encSw.Stop();
                        ushort encodeDurationUs = (ushort)Math.Min(encSw.Elapsed.TotalMicroseconds, ushort.MaxValue);

                        if (encoded != null)
                        {
                            if (encoded.SpsPpsConfig != null && (!configSent || encoded.IsKeyframe))
                            {
                                var cfgHeader = FrameHeader.Create(
                                    MessageType.VideoConfig,
                                    (uint)encoded.SpsPpsConfig.Length,
                                    sequence: (ushort)(Interlocked.Increment(ref sequence) & 0xFFFF),
                                    timestampUs: (uint)frameStartUs);
                                videoChannel.Writer.TryWrite((cfgHeader, encoded.SpsPpsConfig));
                                configSent = true;
                            }

                            if (videoChannel.Reader.Count < 4 || encoded.IsKeyframe)
                            {
                                sentFrameCount = ++frameNumber;
                                var frameMsg = new VideoFrameMessage(
                                    FrameNumber: frameNumber,
                                    CaptureDurationUs: captureDurationUs,
                                    EncodeDurationUs: encodeDurationUs,
                                    NalData: encoded.Data,
                                    IsKeyframe: encoded.IsKeyframe);

                                byte[] framePayload = new byte[frameMsg.PayloadSize];
                                frameMsg.WritePayloadTo(framePayload);

                                var frameHeader = FrameHeader.Create(
                                    MessageType.VideoFrame,
                                    (uint)framePayload.Length,
                                    sequence: (ushort)(Interlocked.Increment(ref sequence) & 0xFFFF),
                                    timestampUs: (uint)frameStartUs,
                                    flags: frameMsg.GetFlags());

                                videoChannel.Writer.TryWrite((frameHeader, framePayload));
                            }
                            else
                            {
                                // Network sender is temporarily behind; drop stale frame and request fresh IDR so decoder stays in sync
                                encoder.RequestKeyframe();
                            }
                        }
                    }
                    catch (Exception frameEx)
                    {
                        Console.WriteLine($"[SecondScreenServer] Kare işleme hatası (yoksayıldı): {frameEx.Message}");
                    }

                    // High-precision frame rate pacing (solid 60.0 FPS)
                    long nowTicks = stopwatch.ElapsedTicks;
                    long diffTicks = nextTargetTicks - nowTicks;
                    if (diffTicks > 0)
                    {
                        int sleepMs = (int)((diffTicks * 1000) / Stopwatch.Frequency);
                        if (sleepMs > 2)
                        {
                            Thread.Sleep(sleepMs - 1);
                        }
                        while (stopwatch.ElapsedTicks < nextTargetTicks)
                        {
                            Thread.SpinWait(10);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"[!] Akış döngüsü kesildi: {ex.Message}");
            }
            finally
            {
                finalFrameNumber = frameNumber;
                captureCompletion.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = "AreasCaptureEncodeThread",
            Priority = ThreadPriority.AboveNormal
        };

        captureThread.Start();

        try
        {
            await captureCompletion.Task;
        }
        finally
        {
            videoChannel.Writer.TryComplete();
            try { await senderTask; } catch { }
            sessionCts.Cancel();
            try { await Task.WhenAll(receiveTask, pingTask); } catch { }
            Log($"[-] Tablet bağlantısı sonlandı. Toplam gönderilen kare: {finalFrameNumber}");

            // Tablet bağlantısı koptuğunda otomatik olarak Laptop Screen (Internal Only) moduna dön
            try
            {
                if (DisplayTopologyManager.RestoreInternalOnly())
                {
                    Log("[+] Tablet ayrıldı: Masaüstü otomatik olarak Yalnızca Laptop Ekranı (Internal Only) moduna döndürüldü.");
                }
            }
            catch (Exception ex)
            {
                Log($"[!] Ekran topolojisi sıfırlanamadı: {ex.Message}");
            }
            finally
            {
                // Prevent ServerLoopAsync finally block from calling RestoreInternalOnly a second time
                ConnectedClientName = null;
                ConnectedClientDetails = null;
            }
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;

        bool wasConnected = IsClientConnected;
        IsRunning = false;
        _serverCts?.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _broadcaster?.Dispose(); } catch { }

        _listener = null;
        _broadcaster = null;
        ConnectedClientName = null;
        ConnectedClientDetails = null;

        if (wasConnected)
        {
            try { DisplayTopologyManager.RestoreInternalOnly(); } catch { }
        }

        TimeEndPeriod(1);
        OnStatusChanged?.Invoke("Durduruldu");
        OnClientSessionChanged?.Invoke(null);
        Log("[+] Areas Sunucusu durduruldu.");
    }

    public static bool IsVirtualDisplayInstalled()
    {
        var displays = DisplayEnumerator.GetDisplays();
        if (displays.Any(d => !d.IsPrimary ||
                              d.FriendlyName.Contains("VDD", StringComparison.OrdinalIgnoreCase) ||
                              d.FriendlyName.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                              d.FriendlyName.Contains("İkinci", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        try
        {
            var driverDir = GetDriverDirectory();
            var devcon = Path.Combine(driverDir, "devcon.exe");
            if (File.Exists(devcon))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = devcon,
                    Arguments = "status Root\\MttVDD",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(1000);
                    if (output.Contains("matching device", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("running", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch { }

        return false;
    }

    private static string GetDriverDirectory()
    {
        var localDir = Path.Combine(AppContext.BaseDirectory, "VirtualDisplayDriver");
        if (File.Exists(Path.Combine(localDir, "devcon.exe")))
            return localDir;
        if (Directory.Exists(@"C:\VirtualDisplayDriver") && File.Exists(@"C:\VirtualDisplayDriver\devcon.exe"))
            return @"C:\VirtualDisplayDriver";
        return localDir;
    }

    public static async Task InstallVirtualDisplayDriverAsync()
    {
        await Task.Run(() =>
        {
            var driverDir = GetDriverDirectory();
            var devcon = Path.Combine(driverDir, "devcon.exe");
            var inf = Path.Combine(driverDir, "MttVDD.inf");
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{devcon}\" install \"{inf}\" Root\\MttVDD\"",
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
        });
    }

    public static async Task UninstallVirtualDisplayDriverAsync()
    {
        await Task.Run(() =>
        {
            var driverDir = GetDriverDirectory();
            var devcon = Path.Combine(driverDir, "devcon.exe");
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{devcon}\" remove Root\\MttVDD\"",
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
        });
    }

    private static void GenerateTestPattern(byte[] buffer, int width, int height, uint frameNum)
    {
        int barWidth = 60;
        int barPos = (int)((frameNum * 8) % (uint)Math.Max(1, width - barWidth));

        for (int y = 0; y < height; y++)
        {
            int rowOff = y * width * 4;
            byte rBase = (byte)((y * 255) / height);
            for (int x = 0; x < width; x++)
            {
                int px = rowOff + (x * 4);
                if (x >= barPos && x < barPos + barWidth)
                {
                    buffer[px] = 255;     // B
                    buffer[px + 1] = 255; // G
                    buffer[px + 2] = 255; // R
                    buffer[px + 3] = 255; // A
                }
                else
                {
                    buffer[px] = (byte)((x * 255) / width); // B
                    buffer[px + 1] = 120;                  // G
                    buffer[px + 2] = rBase;                // R
                    buffer[px + 3] = 255;                  // A
                }
            }
        }
    }

    private void Log(string msg)
    {
        Console.WriteLine(msg);
        OnLog?.Invoke(msg);
    }

    public void Dispose()
    {
        Stop();
        _serverCts?.Dispose();
    }
}
