using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace SecondScreen.Media.Encoding;

public sealed class EncodedPacket
{
    public required byte[] Data { get; init; }
    public bool IsKeyframe { get; init; }
    public byte[]? SpsPpsConfig { get; init; }
}

public sealed class MfH264Encoder : IDisposable
{
    private static readonly Guid LowLatencyGuid = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid ForceKeyframeGuid = new("398c1b98-8353-475a-9e28-13306d773018");

    private IMFTransform? _encoder;
    private ICodecAPI? _codecApi;
    private IMFMediaType? _inType;
    private IMFMediaType? _outType;
    private IMFMediaBuffer? _reusableInBuffer;
    private IMFSample? _reusableInSample;
    private IMFMediaBuffer? _reusableOutBuffer;
    private IMFSample? _reusableOutSample;
    private int _width;
    private int _height;
    private int _fps;
    private int _bitrateBps;
    private int _nv12Size;
    private byte[]? _nv12Buffer;
    private OutputStreamInfo _outputStreamInfo;
    private byte[]? _cachedSpsPps;
    private bool _requestKeyframe = true;
    private bool _isDisposed;

    public int Width => _width;
    public int Height => _height;
    public byte[]? CachedSpsPps => _cachedSpsPps;

    public void Initialize(int width, int height, int fps = 60, int bitrateBps = 15_000_000)
    {
        Dispose();
        _isDisposed = false;

        _width = width;
        _height = height;
        _fps = fps;
        _bitrateBps = bitrateBps;
        _nv12Size = width * height * 3 / 2;
        _nv12Buffer = new byte[_nv12Size];

        MediaFactory.MFStartup().CheckError();

        var outputReg = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264
        };

        // Prefer synchronous MFT for immediate 1-in-1-out low latency, or fallback to first available
        var syncEncoders = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, 0x00000001, null, outputReg);
        IMFTransform? chosen = null;
        foreach (var act in syncEncoders)
        {
            var t = act.ActivateObject<IMFTransform>();
            if (t != null)
            {
                chosen = t;
                break;
            }
        }

        if (chosen == null)
        {
            var allEncoders = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, 0x0000003F, null, outputReg);
            foreach (var act in allEncoders)
            {
                var t = act.ActivateObject<IMFTransform>();
                if (t != null)
                {
                    chosen = t;
                    break;
                }
            }
        }

        _encoder = chosen ?? throw new InvalidOperationException("No suitable H.264 video encoder found on this system.");

        // Set low-latency mode attribute on encoder
        _encoder.Attributes?.Set(LowLatencyGuid, 1);

        // Configure ICodecAPI (Hardware Low Latency, 0 B-frames, CBR Rate Control)
        try
        {
            var pUnk = _encoder.NativePointer;
            if (pUnk != IntPtr.Zero)
            {
                var codecApi = Marshal.GetObjectForIUnknown(pUnk) as ICodecAPI;
                if (codecApi != null)
                {
                    _codecApi = codecApi;
                    CodecApiGuids.TrySet(_codecApi, CodecApiGuids.LowLatencyMode, true);
                    CodecApiGuids.TrySet(_codecApi, CodecApiGuids.DefaultBPictureCount, (uint)0); // NO B-FRAMES
                    CodecApiGuids.TrySet(_codecApi, CodecApiGuids.QualityVsSpeed, (uint)0); // 0 = Fastest encoding speed (ultra-low latency)
                    CodecApiGuids.TrySet(_codecApi, CodecApiGuids.NumWorkerThreads, (uint)Math.Clamp(Environment.ProcessorCount, 4, 16));
                    CodecApiGuids.TrySet(_codecApi, CodecApiGuids.RateControlMode, (uint)0); // CBR
                    CodecApiGuids.TrySet(_codecApi, CodecApiGuids.MeanBitRate, (uint)_bitrateBps);
                    CodecApiGuids.TrySet(_codecApi, CodecApiGuids.MaxBitRate, (uint)(_bitrateBps * 12 / 10));
                    uint bufSize = (uint)(_bitrateBps / 8 / _fps * 2);
                    CodecApiGuids.TrySet(_codecApi, CodecApiGuids.BufferSize, bufSize);
                    CodecApiGuids.TrySet(_codecApi, CodecApiGuids.AdaptiveMode, (uint)0);
                    CodecApiGuids.TrySet(_codecApi, CodecApiGuids.GopSize, (uint)120); // 1 keyframe every 2 seconds (prevents 1Hz keyframe spikes)
                }
            }
        }
        catch { }

        // Configure output MediaType
        _outType = MediaFactory.MFCreateMediaType();
        _outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        _outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        MediaFactory.MFSetAttributeRatio(_outType, MediaTypeAttributeKeys.FrameRate, (uint)_fps, 1);
        MediaFactory.MFSetAttributeSize(_outType, MediaTypeAttributeKeys.FrameSize, (uint)_width, (uint)_height);
        _outType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        _outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)_bitrateBps);
        _outType.Set(LowLatencyGuid, 1);
        _encoder.SetOutputType(0, _outType, 0);

        // Configure input MediaType
        _inType = MediaFactory.MFCreateMediaType();
        _inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        _inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        MediaFactory.MFSetAttributeRatio(_inType, MediaTypeAttributeKeys.FrameRate, (uint)_fps, 1);
        MediaFactory.MFSetAttributeSize(_inType, MediaTypeAttributeKeys.FrameSize, (uint)_width, (uint)_height);
        _inType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        _encoder.SetInputType(0, _inType, 0);

        _encoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _encoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        _outputStreamInfo = _encoder.GetOutputStreamInfo(0);
        _requestKeyframe = true;

        // Pre-allocate reusable MF buffers and samples to avoid native allocation overhead on every frame
        _reusableInBuffer = MediaFactory.MFCreateMemoryBuffer(_nv12Size);
        _reusableInSample = MediaFactory.MFCreateSample();
        _reusableInSample.AddBuffer(_reusableInBuffer);

        int outBufferSize = _outputStreamInfo.Size > 0 ? _outputStreamInfo.Size : 1024 * 1024;
        _reusableOutBuffer = MediaFactory.MFCreateMemoryBuffer(outBufferSize);
        _reusableOutSample = MediaFactory.MFCreateSample();
        _reusableOutSample.AddBuffer(_reusableOutBuffer);
    }

    public void RequestKeyframe()
    {
        _requestKeyframe = true;
    }

    public unsafe EncodedPacket? EncodeBgraFrame(ReadOnlySpan<byte> bgra, long timestampUs)
    {
        if (_encoder == null || _nv12Buffer == null || _reusableInBuffer == null || _reusableInSample == null || _reusableOutBuffer == null || _reusableOutSample == null)
            return null;

        // Convert BGRA directly into the locked MF input buffer (zero intermediate copy)
        _reusableInBuffer.Lock(out IntPtr pData, out _, out _);
        try
        {
            var dstSpan = new Span<byte>(pData.ToPointer(), _nv12Size);
            ColorConverter.BgraToNv12(bgra, dstSpan, _width, _height);
        }
        finally
        {
            _reusableInBuffer.Unlock();
        }
        _reusableInBuffer.CurrentLength = _nv12Size;

        _reusableInSample.SampleTime = timestampUs * 10; // convert us to 100ns units
        _reusableInSample.SampleDuration = (10_000_000 / _fps);

        if (_requestKeyframe)
        {
            if (_codecApi != null)
            {
                CodecApiGuids.TrySet(_codecApi, CodecApiGuids.ForceKeyframe, (uint)1);
            }
            _encoder.Attributes?.Set(ForceKeyframeGuid, 1);
            _requestKeyframe = false;
        }

        _encoder.ProcessInput(0, _reusableInSample, 0);

        // Retrieve output sample
        _reusableOutBuffer.CurrentLength = 0;
        var outputData = new OutputDataBuffer
        {
            StreamID = 0,
            Sample = _reusableOutSample
        };

        var result = _encoder.ProcessOutput(ProcessOutputFlags.None, 1, ref outputData, out _);
        if (!result.Success || outputData.Sample == null)
        {
            return null;
        }

        using var mediaBuffer = outputData.Sample.ConvertToContiguousBuffer();
        mediaBuffer.Lock(out IntPtr pOut, out _, out int outLength);
        if (outLength <= 0)
        {
            mediaBuffer.Unlock();
            return null;
        }

        byte[] rawBytes = new byte[outLength];
        Marshal.Copy(pOut, rawBytes, 0, outLength);
        mediaBuffer.Unlock();

        // Parse NAL units
        return ParseH264Bitstream(rawBytes);
    }

    private EncodedPacket ParseH264Bitstream(byte[] raw)
    {
        bool isKeyframe = false;
        byte[]? spsPps = null;
        List<(int offset, int length, byte nalType)> nals = new();

        // Scan for Annex-B start codes: 00 00 00 01 or 00 00 01
        for (int i = 0; i < raw.Length - 4; i++)
        {
            int startCodeLen = 0;
            if (raw[i] == 0 && raw[i + 1] == 0 && raw[i + 2] == 1)
            {
                startCodeLen = 3;
            }
            else if (raw[i] == 0 && raw[i + 1] == 0 && raw[i + 2] == 0 && raw[i + 3] == 1)
            {
                startCodeLen = 4;
            }

            if (startCodeLen > 0)
            {
                int nalStart = i;
                byte nalHeader = raw[i + startCodeLen];
                byte nalType = (byte)(nalHeader & 0x1F);

                if (nals.Count > 0)
                {
                    var prev = nals[^1];
                    nals[^1] = (prev.offset, nalStart - prev.offset, prev.nalType);
                }

                nals.Add((nalStart, raw.Length - nalStart, nalType));
                i += startCodeLen;
            }
        }

        using var spsPpsStream = new MemoryStream();
        using var frameStream = new MemoryStream();

        foreach (var (offset, length, nalType) in nals)
        {
            if (nalType == 7 || nalType == 8) // SPS or PPS
            {
                spsPpsStream.Write(raw, offset, length);
            }
            else
            {
                if (nalType == 5) // IDR
                {
                    isKeyframe = true;
                }
                frameStream.Write(raw, offset, length);
            }
        }

        if (spsPpsStream.Length > 0)
        {
            spsPps = spsPpsStream.ToArray();
            _cachedSpsPps = spsPps;
            isKeyframe = true;
        }
        else if (isKeyframe && _cachedSpsPps != null)
        {
            spsPps = _cachedSpsPps;
        }

        // On keyframes, include full bitstream (SPS + PPS + IDR) so receiver decodes self-contained
        byte[] frameData = isKeyframe ? raw : (frameStream.Length > 0 ? frameStream.ToArray() : raw);

        return new EncodedPacket
        {
            Data = frameData,
            IsKeyframe = isKeyframe,
            SpsPpsConfig = spsPps
        };
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            if (_encoder != null)
            {
                try
                {
                    _encoder.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
                    _encoder.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
                }
                catch { }
                _encoder.Dispose();
                _encoder = null;
            }
            _codecApi = null;
            _reusableInSample?.Dispose();
            _reusableInSample = null;
            _reusableInBuffer?.Dispose();
            _reusableInBuffer = null;
            _reusableOutSample?.Dispose();
            _reusableOutSample = null;
            _reusableOutBuffer?.Dispose();
            _reusableOutBuffer = null;
            _inType?.Dispose();
            _inType = null;
            _outType?.Dispose();
            _outType = null;
            try { MediaFactory.MFShutdown(); } catch { }
        }
    }
}

