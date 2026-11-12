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
    private IMFMediaEventGenerator? _eventGen;
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
    private OutputStreamInfo _outputStreamInfo;
    private byte[]? _cachedSpsPps;
    private bool _requestKeyframe = true;
    private bool _isDisposed;
    private bool _isAsync;
    private int _pendingInputRequests;

    public int Width => _width;
    public int Height => _height;
    public byte[]? CachedSpsPps => _cachedSpsPps;
    public bool IsHardwareAccelerated { get; private set; }
    public string EncoderName { get; private set; } = "None";
    public string? FallbackReason { get; private set; }
    public string StatusLabel => IsHardwareAccelerated
        ? $"GPU ({EncoderName})"
        : (FallbackReason != null ? $"CPU Fallback ({EncoderName})" : $"CPU ({EncoderName})");

    public void Initialize(int width, int height, int fps = 60, int bitrateBps = 15_000_000)
    {
        InitializeInternal(width, height, fps, bitrateBps, forceCpuOnly: false, fallbackReason: null);
    }

    private void InitializeInternal(int width, int height, int fps, int bitrateBps, bool forceCpuOnly, string? fallbackReason)
    {
        Dispose();
        _isDisposed = false;

        _width = width;
        _height = height;
        _fps = fps;
        _bitrateBps = bitrateBps;
        _nv12Size = width * height * 3 / 2;
        _pendingInputRequests = 0;
        IsHardwareAccelerated = false;
        EncoderName = "None";
        FallbackReason = fallbackReason;

        MediaFactory.MFStartup().CheckError();

        var outputReg = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264
        };

        bool initialized = false;
        string? hwFailure = fallbackReason;

        // 1. Try Hardware GPU Encoders first (NVIDIA NVENC, Intel QuickSync, AMD AMF)
        if (!forceCpuOnly)
        {
            // MFT_ENUM_FLAG_HARDWARE (0x04) | MFT_ENUM_FLAG_SORTANDFILTER (0x40)
            var hwEncoders = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, 0x00000004 | 0x00000040, null, outputReg);
            int hwCount = 0;
            foreach (var act in hwEncoders)
            {
                hwCount++;
                if (TryActivateAndConfigure(act, isHardware: true, out string? err))
                {
                    initialized = true;
                    break;
                }
                hwFailure = err ?? "Hardware MFT initialization failed";
            }
            if (hwCount == 0)
            {
                hwFailure ??= "No hardware H.264 MFT found";
            }
        }

        // 2. Fallback to Synchronous CPU Encoder (Microsoft H.264 Encoder MFT)
        if (!initialized)
        {
            FallbackReason = hwFailure;
            var syncEncoders = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, 0x00000001, null, outputReg);
            foreach (var act in syncEncoders)
            {
                if (TryActivateAndConfigure(act, isHardware: false, out _))
                {
                    initialized = true;
                    break;
                }
            }
        }

        if (!initialized)
        {
            var allEncoders = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, 0x0000003F, null, outputReg);
            foreach (var act in allEncoders)
            {
                if (TryActivateAndConfigure(act, isHardware: false, out _))
                {
                    initialized = true;
                    break;
                }
            }
        }

        if (!initialized || _encoder == null)
        {
            throw new InvalidOperationException("No suitable H.264 video encoder found on this system.");
        }

        // Pre-allocate reusable MF buffers and samples to avoid native allocation overhead on every frame
        _reusableInBuffer = MediaFactory.MFCreateMemoryBuffer(_nv12Size);
        _reusableInSample = MediaFactory.MFCreateSample();
        _reusableInSample.AddBuffer(_reusableInBuffer);

        int outBufferSize = _outputStreamInfo.Size > 0 ? _outputStreamInfo.Size : 1024 * 1024;
        _reusableOutBuffer = MediaFactory.MFCreateMemoryBuffer(outBufferSize);
        _reusableOutSample = MediaFactory.MFCreateSample();
        _reusableOutSample.AddBuffer(_reusableOutBuffer);
    }

    private bool TryActivateAndConfigure(IMFActivate act, bool isHardware, out string? error)
    {
        error = null;
        string friendlyName = isHardware ? "Hardware H.264" : "Software H.264";
        try
        {
            friendlyName = act.GetString(TransformAttributeKeys.MftFriendlyNameAttribute) ?? friendlyName;
        }
        catch { }

        IMFTransform? candidate = null;
        IMFMediaEventGenerator? eventGen = null;
        IMFMediaType? outType = null;
        IMFMediaType? inType = null;

        try
        {
            candidate = act.ActivateObject<IMFTransform>();
            if (candidate == null)
            {
                error = $"{friendlyName}: ActivateObject returned null";
                return false;
            }

            // Unlock Async MFTs (required by async NVENC / QuickSync / AMF).
            // MF_E_ATTRIBUTENOTFOUND on TransformAsync simply means the MFT is synchronous.
            bool isAsync = false;
            bool hasAsyncAttr = false;
            try
            {
                if (candidate.Attributes != null)
                {
                    hasAsyncAttr = candidate.Attributes.GetUInt32(TransformAttributeKeys.TransformAsync) != 0;
                }
            }
            catch
            {
                hasAsyncAttr = false;
            }

            if (hasAsyncAttr)
            {
                try
                {
                    isAsync = true;
                    candidate.Attributes!.Set(TransformAttributeKeys.TransformAsyncUnlock, 1);
                    eventGen = candidate.QueryInterface<IMFMediaEventGenerator>();
                }
                catch (Exception ex)
                {
                    error = $"{friendlyName}: Async unlock failed ({ex.Message})";
                    throw;
                }
            }

            try { candidate.Attributes?.Set(LowLatencyGuid, 1); } catch { }

            ICodecAPI? codecApi = null;
            try
            {
                var pUnk = candidate.NativePointer;
                if (pUnk != IntPtr.Zero)
                {
                    codecApi = Marshal.GetObjectForIUnknown(pUnk) as ICodecAPI;
                    if (codecApi != null)
                    {
                        CodecApiGuids.TrySet(codecApi, CodecApiGuids.LowLatencyMode, true);
                        CodecApiGuids.TrySet(codecApi, CodecApiGuids.DefaultBPictureCount, (uint)0);
                        CodecApiGuids.TrySet(codecApi, CodecApiGuids.QualityVsSpeed, (uint)0);
                        CodecApiGuids.TrySet(codecApi, CodecApiGuids.NumWorkerThreads, (uint)Math.Clamp(Environment.ProcessorCount, 4, 16));
                        CodecApiGuids.TrySet(codecApi, CodecApiGuids.RateControlMode, (uint)0);
                        CodecApiGuids.TrySet(codecApi, CodecApiGuids.MeanBitRate, (uint)_bitrateBps);
                        CodecApiGuids.TrySet(codecApi, CodecApiGuids.MaxBitRate, (uint)(_bitrateBps * 12 / 10));
                        uint bufSize = (uint)(_bitrateBps / 8 / _fps * 2);
                        CodecApiGuids.TrySet(codecApi, CodecApiGuids.BufferSize, bufSize);
                        CodecApiGuids.TrySet(codecApi, CodecApiGuids.AdaptiveMode, (uint)0);
                        CodecApiGuids.TrySet(codecApi, CodecApiGuids.GopSize, (uint)120);
                    }
                }
            }
            catch { }

            outType = MediaFactory.MFCreateMediaType();
            outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            MediaFactory.MFSetAttributeRatio(outType, MediaTypeAttributeKeys.FrameRate, (uint)_fps, 1);
            MediaFactory.MFSetAttributeSize(outType, MediaTypeAttributeKeys.FrameSize, (uint)_width, (uint)_height);
            outType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)_bitrateBps);
            outType.Set(LowLatencyGuid, 1);
            candidate.SetOutputType(0, outType, 0);

            inType = MediaFactory.MFCreateMediaType();
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            MediaFactory.MFSetAttributeRatio(inType, MediaTypeAttributeKeys.FrameRate, (uint)_fps, 1);
            MediaFactory.MFSetAttributeSize(inType, MediaTypeAttributeKeys.FrameSize, (uint)_width, (uint)_height);
            inType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            candidate.SetInputType(0, inType, 0);

            candidate.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            candidate.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

            _outputStreamInfo = candidate.GetOutputStreamInfo(0);
            _encoder = candidate;
            _eventGen = eventGen;
            _isAsync = isAsync && eventGen != null;
            _codecApi = codecApi;
            _outType = outType;
            _inType = inType;
            _requestKeyframe = true;
            IsHardwareAccelerated = isHardware;
            EncoderName = friendlyName;
            return true;
        }
        catch (Exception ex)
        {
            error ??= $"{friendlyName}: {ex.Message}";
            eventGen?.Dispose();
            inType?.Dispose();
            outType?.Dispose();
            candidate?.Dispose();
            return false;
        }
    }

    public void RequestKeyframe()
    {
        _requestKeyframe = true;
    }

    public unsafe EncodedPacket? EncodeBgraFrame(ReadOnlySpan<byte> bgra, long timestampUs)
    {
        if (_encoder == null || _reusableInBuffer == null || _reusableInSample == null || _reusableOutBuffer == null || _reusableOutSample == null)
            return null;

        // Convert BGRA directly into the locked MF input buffer (zero intermediate copy)
        WriteBgraToReusableInputBuffer(bgra, timestampUs);

        try
        {
            return EncodePreparedSample();
        }
        catch (Exception ex) when (IsHardwareAccelerated)
        {
            // Hardware encoder failed mid-stream: immediately fall back to CPU so stream never goes black
            InitializeInternal(_width, _height, _fps, _bitrateBps, forceCpuOnly: true, fallbackReason: ex.Message);
            WriteBgraToReusableInputBuffer(bgra, timestampUs);
            return EncodePreparedSample();
        }
    }

    private unsafe void WriteBgraToReusableInputBuffer(ReadOnlySpan<byte> bgra, long timestampUs)
    {
        if (_reusableInBuffer == null || _reusableInSample == null) return;

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
    }

    private EncodedPacket? EncodePreparedSample()
    {
        if (_encoder == null || _reusableInSample == null || _reusableOutBuffer == null || _reusableOutSample == null)
            return null;

        if (_requestKeyframe)
        {
            if (_codecApi != null)
            {
                CodecApiGuids.TrySet(_codecApi, CodecApiGuids.ForceKeyframe, (uint)1);
            }
            _encoder.Attributes?.Set(ForceKeyframeGuid, 1);
            _requestKeyframe = false;
        }

        if (_isAsync && _eventGen != null)
        {
            // Wait until the Async MFT signals it is ready for input
            while (_pendingInputRequests <= 0)
            {
                using var ev = _eventGen.GetEvent(0);
                if (ev.EventType == MediaEventTypes.TransformNeedInput)
                {
                    _pendingInputRequests++;
                }
                else if (ev.EventType == MediaEventTypes.TransformHaveOutput)
                {
                    // Drain stale output if any arrived before NeedInput
                    TryPullOutputSample();
                }
            }

            _encoder.ProcessInput(0, _reusableInSample, 0);
            _pendingInputRequests--;

            // Wait for encoded output (or NeedInput if MFT has a 1-frame pipeline delay)
            while (true)
            {
                using var ev = _eventGen.GetEvent(0);
                if (ev.EventType == MediaEventTypes.TransformHaveOutput)
                {
                    return TryPullOutputSample();
                }
                if (ev.EventType == MediaEventTypes.TransformNeedInput)
                {
                    _pendingInputRequests++;
                    return null;
                }
            }
        }

        _encoder.ProcessInput(0, _reusableInSample, 0);
        return TryPullOutputSample();
    }

    private EncodedPacket? TryPullOutputSample()
    {
        if (_encoder == null || _reusableOutBuffer == null || _reusableOutSample == null)
            return null;

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
            _eventGen?.Dispose();
            _eventGen = null;
            _isAsync = false;
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

