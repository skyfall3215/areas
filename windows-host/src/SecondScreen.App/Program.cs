using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using SecondScreen.Core.Diagnostics;
using SecondScreen.Core.Protocol;
using SecondScreen.Core.Transport;
using SecondScreen.Media.Capture;
using SecondScreen.Media.Encoding;
using SecondScreen.Network;

namespace SecondScreen.App;

public static class Program
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    private static readonly CancellationTokenSource Cts = new();

    public static async Task Main(string[] args)
    {
        TimeBeginPeriod(1); // Set Windows timer precision to 1 ms
        try
        {
            await RunAsync(args);
        }
        finally
        {
            TimeEndPeriod(1);
        }
    }

    private static async Task RunAsync(string[] args)
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Cts.Cancel();
        };

        Console.WriteLine("=================================================");
        Console.WriteLine(" SecondScreen Windows Host - Aşama 4 (Hotspot + Wi-Fi + USB)");
        Console.WriteLine("=================================================");
        Console.WriteLine();

        bool autoOpenClock = args.Any(a => a.Equals("--test-clock", StringComparison.OrdinalIgnoreCase) || a.Equals("-t", StringComparison.OrdinalIgnoreCase));
        if (autoOpenClock)
        {
            Console.WriteLine("[+] Gecikme test kronometresi açılıyor...");
            LatencyStopwatchForm.ShowInThread();
        }

        // Background listener for 'T' (Stopwatch) and 'H' (Hotspot Settings)
        _ = Task.Run(() =>
        {
            while (!Cts.Token.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.T)
                    {
                        Console.WriteLine("[+] Gecikme test kronometresi açılıyor...");
                        LatencyStopwatchForm.ShowInThread();
                    }
                    else if (key.Key == ConsoleKey.H)
                    {
                        Console.WriteLine("[+] Windows Mobil Etkin Nokta ayarları açılıyor...");
                        HotspotManager.OpenHotspotSettings();
                    }
                }
                Thread.Sleep(100);
            }
        });

        Console.WriteLine("[İPUCU] Kronometre için 'T' tuşuna, Hotspot ayarları için 'H' tuşuna basın.");
        Console.WriteLine();

        // 1. ADB Reverse (USB-C Mode)
        Console.WriteLine("[1/4] USB tüneli (adb reverse) kontrol ediliyor...");
        var (adbSuccess, adbMessage) = await AdbHelper.ReversePortAsync(ProtocolConstants.DefaultPort, Cts.Token);
        if (adbSuccess)
        {
            Console.WriteLine($"[OK] {adbMessage}");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[BİLGİ] {adbMessage}");
            Console.WriteLine("USB kablosu takılı değilse Wi-Fi veya Hotspot modunu kullanabilirsiniz.");
            Console.ResetColor();
        }

        // 2. Discover local IP addresses for Wi-Fi LAN & Hotspot
        var localIps = NetworkHelper.GetLocalIPv4Addresses();
        Console.WriteLine("[2/4] Kullanılabilir Ağ Arayüzleri:");
        bool hotspotActive = HotspotManager.IsHotspotActive();
        if (hotspotActive)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"      [★] Mobil Etkin Nokta (Hotspot): 192.168.137.1 (Yurt/Okul için en kararlı mod!)");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"      [i] Mobil Etkin Nokta kapalı. Açmak için 'H' tuşuna basın.");
            Console.ResetColor();
        }

        if (localIps.Count == 0)
        {
            Console.WriteLine("      (Etkin yerel ağ adaptörü bulunamadı)");
        }
        else
        {
            foreach (var ip in localIps)
            {
                if (ip == HotspotManager.DefaultWindowsHotspotIp) continue;
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"      -> Wi-Fi LAN IP: {ip} (Port: {ProtocolConstants.DefaultPort})");
                Console.ResetColor();
            }
        }

        // 3. Start UDP Discovery Broadcaster
        using var broadcaster = new LanDiscoveryBroadcaster();
        broadcaster.Start();
        Console.WriteLine($"[3/4] UDP Keşif Yayını (Port: {ProtocolConstants.DiscoveryPort}) başlatıldı.");

        // 4. Start TCP Listener on all interfaces (0.0.0.0)
        var listener = new TcpListener(IPAddress.Any, ProtocolConstants.DefaultPort);
        listener.Start();
        Console.WriteLine($"[4/4] Sunucu 0.0.0.0:{ProtocolConstants.DefaultPort} üzerinde dinliyor (USB + Wi-Fi + Hotspot)...");
        Console.WriteLine("Android tablet bağlantısı bekleniyor (Ctrl+C ile çıkış)...");
        Console.WriteLine();

        while (!Cts.Token.IsCancellationRequested)
        {
            try
            {
                using var tcpClient = await listener.AcceptTcpClientAsync(Cts.Token);
                var remoteEp = tcpClient.Client.RemoteEndPoint?.ToString() ?? "Bilinmiyor";
                bool isUsb = remoteEp.StartsWith("127.0.0.1") || remoteEp.StartsWith("[::1]");
                bool isHotspot = HotspotManager.IsHotspotClient(remoteEp);

                Console.ForegroundColor = ConsoleColor.Green;
                if (isUsb)
                {
                    Console.WriteLine($"[+] Tablet bağlandı: {remoteEp} [Mod: USB-C (adb reverse)]");
                }
                else if (isHotspot)
                {
                    Console.WriteLine($"[+] Tablet bağlandı: {remoteEp} [Mod: Mobil Etkin Nokta (Hotspot 192.168.137.1)]");
                }
                else
                {
                    Console.WriteLine($"[+] Tablet bağlandı: {remoteEp} [Mod: Wi-Fi LAN]");
                }
                Console.ResetColor();

                await HandleClientSessionAsync(tcpClient, Cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[!] Oturum hatası: {ex.Message}");
                Console.ResetColor();
            }

            Console.WriteLine("[*] Yeni bağlantı için bekleniyor...");
        }

        listener.Stop();
        Console.WriteLine("Sunucu durduruldu.");
    }

    private static async Task HandleClientSessionAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        await using var transport = new TcpTransport(tcpClient);
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var rttTracker = new RttTracker();

        // 1. Handshake: Wait for HELLO
        Console.WriteLine("[-] Tablet HELLO mesajı bekleniyor...");
        var (header, payload) = await transport.ReceivePacketAsync(sessionCts.Token);
        if (header.MsgType != MessageType.Hello)
        {
            Console.WriteLine($"[!] Beklenmeyen mesaj: {header.MsgType}, oturum sonlandırılıyor.");
            return;
        }

        var hello = HelloMessage.ReadPayloadFrom(payload);
        Console.WriteLine($"[+] HELLO alındı: Cihaz='{hello.DeviceName}', Çözünürlük={hello.NativeWidth}x{hello.NativeHeight}@{hello.MaxFps}Hz, İstenenBitrate={hello.PreferredBitrateKbps} kbps");

        // 2. Initialize Screen Capture to detect actual desktop resolution
        using var capture = new DxgiScreenCapture();
        bool captureOk = capture.Initialize();

        ushort streamWidth;
        ushort streamHeight;
        if (captureOk && capture.Width > 0 && capture.Height > 0)
        {
            streamWidth = (ushort)(capture.Width & ~1);
            streamHeight = (ushort)(capture.Height & ~1);
            Console.WriteLine($"[+] DXGI Ekran Yakalama başlatıldı: {streamWidth}x{streamHeight}");
        }
        else
        {
            streamWidth = hello.NativeWidth > 0 ? Math.Min(hello.NativeWidth, (ushort)1920) : (ushort)1920;
            streamHeight = hello.NativeHeight > 0 ? Math.Min(hello.NativeHeight, (ushort)1080) : (ushort)1080;
            streamWidth = (ushort)(streamWidth & ~1);
            streamHeight = (ushort)(streamHeight & ~1);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[!] DXGI yakalama başlatılamadı. Test deseni yayını: {streamWidth}x{streamHeight}");
            Console.ResetColor();
        }

        ushort targetFps = 60;
        uint bitrateBps = hello.PreferredBitrateKbps > 0 ? (uint)hello.PreferredBitrateKbps * 1000 : 15_000_000;
        uint sessionId = (uint)Random.Shared.Next(1, int.MaxValue);

        // 3. Send HELLO_ACK with actual stream resolution
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
        Console.WriteLine($"[+] HELLO_ACK gönderildi: Akış {streamWidth}x{streamHeight} @ {targetFps} FPS, {bitrateBps / 1_000_000} Mbps");

        // 4. Initialize Encoder with the matching stream dimensions
        using var encoder = new MfH264Encoder();
        encoder.Initialize(streamWidth, streamHeight, targetFps, (int)bitrateBps);
        Console.WriteLine("[+] Media Foundation H.264 Donanımsal/Düşük Gecikmeli Kodlayıcı hazır.");

        var clientIp = (tcpClient.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.Loopback;
        bool isUsbClient = clientIp.Equals(IPAddress.Loopback) || clientIp.Equals(IPAddress.IPv6Loopback);

        Console.WriteLine($"[+] Taşıma Modu: STANDART (TCP NoDelay){(isUsbClient ? " [USB-C Kablo]" : " [Wi-Fi]")}");

        int sequence = 2;
        uint sentFrameCount = 0;

        // 5. Background task for incoming packets (KEYFRAME_REQ, PING, PONG, DISCONNECT)
        var receiveTask = Task.Run(async () =>
        {
            try
            {
                while (!sessionCts.Token.IsCancellationRequested)
                {
                    var (inHeader, inPayload) = await transport.ReceivePacketAsync(sessionCts.Token);
                    if (inHeader.MsgType == MessageType.KeyframeReq)
                    {
                        var req = KeyframeReqMessage.ReadPayloadFrom(inPayload);
                        Console.WriteLine($"[i] Tablet KEYFRAME_REQ istedi (Son iyi kare: {req.LastGoodFrameNumber})");
                        encoder.RequestKeyframe();
                    }
                    else if (inHeader.MsgType == MessageType.Ping)
                    {
                        // Reply with PONG immediately
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
                            Console.WriteLine($"[RTT] Ağ gecikmesi: {rttTracker.LastRttMs:F1} ms (Tek yön RTT/2: {rttTracker.LastOneWayMs:F1} ms)");
                        }
                    }
                    else if (inHeader.MsgType == MessageType.Disconnect)
                    {
                        Console.WriteLine("[i] Tablet normal bağlantı kapatma bildirdi.");
                        sessionCts.Cancel();
                        break;
                    }
                }
            }
            catch (Exception)
            {
                sessionCts.Cancel();
            }
        }, sessionCts.Token);

        // 6. Periodic Ping Task (every 1000 ms)
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
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }, sessionCts.Token);

        // 7. Decoupled Video Sender Task (Buffer 4 frames for jitter; auto-heals with IDR keyframe on backlog)
        var videoChannel = Channel.CreateBounded<(FrameHeader Header, byte[] Payload)>(
            new BoundedChannelOptions(4)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });

        var videoSenderTask = Task.Run(async () =>
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
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                sessionCts.Cancel();
            }
        }, sessionCts.Token);

        // 8. Video Capture and Encoding Loop (Solid 60.0 FPS)
        uint frameNumber = 0;
        bool configSent = false;
        byte[]? lastSpsPps = null;
        var stopwatch = Stopwatch.StartNew();
        byte[] testPatternBgra = new byte[streamWidth * streamHeight * 4];

        Console.WriteLine("[+] Canlı video akışı başladı (60 FPS)...");

        try
        {
            long targetFrameIntervalTicks = Stopwatch.Frequency / targetFps;

            while (!sessionCts.Token.IsCancellationRequested)
            {
                long frameStartTicks = stopwatch.ElapsedTicks;
                long nextTargetTicks = frameStartTicks + targetFrameIntervalTicks;
                long frameStartUs = (frameStartTicks * 1_000_000) / Stopwatch.Frequency;

                // Acquire frame or generate test pattern
                byte[] bgraData;
                ushort captureDurationUs = 0;

                if (captureOk && capture.IsInitialized)
                {
                    var capSw = Stopwatch.StartNew();
                    var capturedFrame = capture.AcquireNextFrame(timeoutMs: 0);
                    capSw.Stop();
                    captureDurationUs = (ushort)Math.Min(capSw.Elapsed.TotalMicroseconds, ushort.MaxValue);

                    if (capturedFrame?.RgbaData != null)
                    {
                        bgraData = capturedFrame.RgbaData;
                    }
                    else
                    {
                        // Wait for next tick
                        Thread.Sleep(1);
                        continue;
                    }
                }
                else
                {
                    // Generate smooth moving test pattern with timestamp and color gradient
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
                    // Send VIDEO_CONFIG on initial frame and on every keyframe (Item 4)
                    if (encoded.SpsPpsConfig != null && (!configSent || encoded.IsKeyframe))
                    {
                        var cfgHeader = FrameHeader.Create(
                            MessageType.VideoConfig,
                            (uint)encoded.SpsPpsConfig.Length,
                            sequence: (ushort)(Interlocked.Increment(ref sequence) & 0xFFFF),
                            timestampUs: (uint)frameStartUs);
                        await transport.SendPacketAsync(cfgHeader, encoded.SpsPpsConfig, sessionCts.Token);
                        configSent = true;
                        lastSpsPps = encoded.SpsPpsConfig;
                    }

                    // Push VIDEO_FRAME to decoupled TCP queue
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

                    if (videoChannel.Reader.Count >= 2)
                    {
                        encoder.RequestKeyframe();
                    }

                    videoChannel.Writer.TryWrite((frameHeader, framePayload));
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
            Console.WriteLine($"[!] Akış döngüsü kesildi: {ex.Message}");
        }
        finally
        {
            videoChannel.Writer.TryComplete();
            try { await videoSenderTask; } catch { }
            sessionCts.Cancel();
            try { await Task.WhenAll(receiveTask, pingTask); } catch { }
            Console.WriteLine($"[-] Tablet bağlantısı sonlandı. Toplam gönderilen kare: {frameNumber}");
        }
    }

    private static void GenerateTestPattern(byte[] buffer, int width, int height, uint frameNum)
    {
        int barWidth = 60;
        int barPos = (int)((frameNum * 8) % (uint)(width - barWidth));

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
}
