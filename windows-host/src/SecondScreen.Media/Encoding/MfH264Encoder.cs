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
                    CodecApiGuids.TrySet(_codecApi, CodecApiGuids.RateControlMode, (uint)0); // CBR
                    CodecApiGuids.TrySet(_codecApi, CodecApiGuids.MeanBitRate, (uint)_bitrateBps);
                    CodecApiGuids.TrySet(_codecApi, CodecApiGuids.MaxBitRate, (uint)(_bitrateBps * 12 / 10));
                    uint bufSize = (uint)(_bitrateBps / 8 / _fps * 2);
                    CodecApiGuids.TrySet(_codecApi, CodecApiGuids.BufferSize, bufSize);
                    CodecApiGuids.TrySet(_codecApi, CodecApiGuids.AdaptiveMode, (uint)0);
                    CodecApiGuids.TrySet(_codecApi, CodecApiGuids.GopSize, (uint)60); // 1 keyframe per second
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
    }

    public void RequestKeyframe()
    {
        _requestKeyframe = true;
    }

    public EncodedPacket? EncodeBgraFrame(ReadOnlySpan<byte> bgra, long timestampUs)
    {
        if (_encoder == null || _nv12Buffer == null)
            return null;

        // Convert BGRA to NV12
        ColorConverter.BgraToNv12(bgra, _nv12Buffer, _width, _height);

        // Create MF input buffer
        using var inBuffer = MediaFactory.MFCreateMemoryBuffer(_nv12Size);
        inBuffer.Lock(out IntPtr pData, out _, out _);
        Marshal.Copy(_nv12Buffer, 0, pData, _nv12Size);
        inBuffer.Unlock();
        inBuffer.CurrentLength = _nv12Size;

        using var inSample = MediaFactory.MFCreateSample();
        inSample.AddBuffer(inBuffer);
        inSample.SampleTime = timestampUs * 10; // convert us to 100ns units
        inSample.SampleDuration = (10_000_000 / _fps);

        if (_requestKeyframe)
        {
            if (_codecApi != null)
            {
                CodecApiGuids.TrySet(_codecApi, CodecApiGuids.ForceKeyframe, (uint)1);
            }
            _encoder.Attributes?.Set(ForceKeyframeGuid, 1);
            _requestKeyframe = false;
        }

        _encoder.ProcessInput(0, inSample, 0);

        // Retrieve output sample
        int outBufferSize = _outputStreamInfo.Size > 0 ? _outputStreamInfo.Size : 1024 * 1024;
        using var outBuffer = MediaFactory.MFCreateMemoryBuffer(outBufferSize);
        using var outSample = MediaFactory.MFCreateSample();
        outSample.AddBuffer(outBuffer);

        var outputData = new OutputDataBuffer
        {
            StreamID = 0,
            Sample = outSample
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

        byte[] frameData = frameStream.Length > 0 ? frameStream.ToArray() : raw;

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
            _inType?.Dispose();
            _inType = null;
            _outType?.Dispose();
            _outType = null;
            try { MediaFactory.MFShutdown(); } catch { }
        }
    }
}

