using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SecondScreen.Core.Protocol;
using SecondScreen.Core.Transport;
using SecondScreen.Media.Capture;
using SecondScreen.Media.Encoding;

namespace SecondScreen.App;

public static class Program
{
    private static readonly CancellationTokenSource Cts = new();

    public static async Task Main(string[] args)
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Cts.Cancel();
        };

        Console.WriteLine("=================================================");
        Console.WriteLine(" SecondScreen Windows Host - Aşama 1 (USB-C Mode)");
        Console.WriteLine("=================================================");
        Console.WriteLine();

        // 1. ADB Reverse
        Console.WriteLine("[1/3] adb reverse yapılandırılıyor...");
        var (adbSuccess, adbMessage) = await AdbHelper.ReversePortAsync(ProtocolConstants.DefaultPort, Cts.Token);
        if (adbSuccess)
        {
            Console.WriteLine($"[OK] {adbMessage}");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[UYARI] {adbMessage}");
            Console.WriteLine("Tablet USB kablosu takılı değilse veya yetkilendirilmemişse kabloyu kontrol edin.");
            Console.ResetColor();
        }

        // 2. Start TCP Listener
        var listener = new TcpListener(IPAddress.Loopback, ProtocolConstants.DefaultPort);
        listener.Start();
        Console.WriteLine($"[2/3] Sunucu 127.0.0.1:{ProtocolConstants.DefaultPort} üzerinde dinliyor...");
        Console.WriteLine("[3/3] Android tablet alıcı bağlantısı bekleniyor (Ctrl+C ile çıkış)...");
        Console.WriteLine();

        while (!Cts.Token.IsCancellationRequested)
        {
            try
            {
                using var tcpClient = await listener.AcceptTcpClientAsync(Cts.Token);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[+] Tablet bağlandı: {tcpClient.Client.RemoteEndPoint}");
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
            Console.WriteLine($"[!] DXGI yakalama başlatılamadı (kilitli masaüstü veya oturum kısıtı). Test deseni yayını: {streamWidth}x{streamHeight}");
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

        // 4. Background task for incoming packets (e.g. KEYFRAME_REQ, DISCONNECT)
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

        // 5. Video Streaming Loop
        ushort sequence = 2;
        uint frameNumber = 0;
        bool configSent = false;
        var stopwatch = Stopwatch.StartNew();
        byte[] testPatternBgra = new byte[streamWidth * streamHeight * 4];

        Console.WriteLine("[+] Canlı video akışı başladı (60 FPS)...");

        try
        {
            long targetFrameIntervalUs = 1_000_000 / targetFps;

            while (!sessionCts.Token.IsCancellationRequested)
            {
                long frameStartUs = stopwatch.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;

                // Acquire frame or generate test pattern
                byte[] bgraData;
                ushort captureDurationUs = 0;

                if (captureOk && capture.IsInitialized)
                {
                    var capSw = Stopwatch.StartNew();
                    using var capturedFrame = capture.AcquireNextFrame(timeoutMs: 16);
                    capSw.Stop();
                    captureDurationUs = (ushort)Math.Min(capSw.Elapsed.TotalMicroseconds, ushort.MaxValue);

                    if (capturedFrame?.RgbaData != null)
                    {
                        bgraData = capturedFrame.RgbaData;
                    }
                    else
                    {
                        // No new screen change this tick, wait briefly
                        await Task.Delay(1, sessionCts.Token);
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
                    // Send VIDEO_CONFIG on initial SPS/PPS or whenever it updates
                    if (encoded.SpsPpsConfig != null && (!configSent || encoded.IsKeyframe))
                    {
                        var cfgHeader = FrameHeader.Create(
                            MessageType.VideoConfig,
                            (uint)encoded.SpsPpsConfig.Length,
                            sequence: sequence++,
                            timestampUs: (uint)frameStartUs);
                        await transport.SendPacketAsync(cfgHeader, encoded.SpsPpsConfig, sessionCts.Token);
                        configSent = true;
                    }

                    // Send VIDEO_FRAME
                    var frameMsg = new VideoFrameMessage(
                        FrameNumber: ++frameNumber,
                        CaptureDurationUs: captureDurationUs,
                        EncodeDurationUs: encodeDurationUs,
                        NalData: encoded.Data,
                        IsKeyframe: encoded.IsKeyframe);

                    byte[] framePayload = new byte[frameMsg.PayloadSize];
                    frameMsg.WritePayloadTo(framePayload);

                    var frameHeader = FrameHeader.Create(
                        MessageType.VideoFrame,
                        (uint)framePayload.Length,
                        sequence: sequence++,
                        timestampUs: (uint)frameStartUs,
                        flags: frameMsg.GetFlags());

                    await transport.SendPacketAsync(frameHeader, framePayload, sessionCts.Token);
                }

                // Frame rate pacing (~16.6ms for 60 FPS)
                long elapsedFrameUs = (stopwatch.ElapsedTicks * 1_000_000 / Stopwatch.Frequency) - frameStartUs;
                long sleepUs = targetFrameIntervalUs - elapsedFrameUs;
                if (sleepUs > 1000)
                {
                    await Task.Delay((int)(sleepUs / 1000), sessionCts.Token);
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
            sessionCts.Cancel();
            try { await receiveTask; } catch { }
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
