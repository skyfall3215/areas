using System.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using DxgiResultCode = Vortice.DXGI.ResultCode;

namespace SecondScreen.Media.Capture;

public sealed class CapturedFrame
{
    public byte[]? RgbaData { get; init; }
    /// <summary>Tightly packed NV12 (GPU-converted). When set, RgbaData is null.</summary>
    public byte[]? Nv12Data { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long TimestampUs { get; init; }
    public bool IsNew { get; init; }
    public bool ForceKeyframe { get; init; }
}

public sealed class DxgiScreenCapture : IDisposable
{
    private const int CursorPatchSize = 192; // covers the 96x96 GDI cursor canvas for any hotspot

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point lpPoint);

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;
    // GPU NV12 path (Sunshine-style): persistent desktop copy + video processor
    private ID3D11Texture2D? _desktopCopy;
    private ID3D11Texture2D? _cursorStaging;
    private GpuNv12Converter? _gpu;
    private byte[]? _nv12Data;
    private byte[]? _cursorPatchBgra;
    private byte[]? _cursorPatchNv12;
    private bool _hasGpuFrame;
    private bool _gpuDisabled;
    private string? _targetDeviceName;
    private int _outputIndex;
    private int _width;
    private int _height;
    private int _originX;
    private int _originY;
    private byte[]? _reusableRgbaData;
    private byte[]? _cleanDesktopRgba;
    private bool _hasAnySurface;
    private bool _hasVisibleDesktop;
    private long _lastPresentTimeUs;
    private long _lastInitAttemptTicks;
    private bool _isDisposed;

    public int Width => _width;
    public int Height => _height;
    public bool IsInitialized => !_isDisposed && (_duplication != null || _hasAnySurface);

    /// <summary>
    /// Releases only the D3D11/DXGI COM duplication pointers while preserving the cached
    /// desktop pixel buffers (_reusableRgbaData / _cleanDesktopRgba / _hasAnySurface) so
    /// UAC / Winlogon secure-desktop transitions never lose the last desktop frame or crash.
    /// </summary>
    public void ReleaseDuplicationObjects()
    {
        try { _gpu?.Dispose(); } catch { }
        _gpu = null;
        try { _desktopCopy?.Dispose(); } catch { }
        _desktopCopy = null;
        try { _cursorStaging?.Dispose(); } catch { }
        _cursorStaging = null;
        _hasGpuFrame = false;
        try { _stagingTexture?.Dispose(); } catch { }
        _stagingTexture = null;
        try { _duplication?.Dispose(); } catch { }
        _duplication = null;
        try { _context?.Dispose(); } catch { }
        _context = null;
        try { _device?.Dispose(); } catch { }
        _device = null;
        _hasVisibleDesktop = false;
    }

    public bool Initialize(string? targetDeviceName = null, int outputIndex = 0)
    {
        bool hadAnySurface = _hasAnySurface;
        ReleaseDuplicationObjects();
        _isDisposed = false;
        _hasAnySurface = hadAnySurface && _reusableRgbaData != null;
        _hasVisibleDesktop = false;
        _targetDeviceName = targetDeviceName;
        _outputIndex = outputIndex;
        _lastInitAttemptTicks = Stopwatch.GetTimestamp();

        try
        {
            GdiScreenCapture.EnsureThreadDesktopAttached();

            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            if (factory == null) return false;

            int globalOutCount = 0;

            // Try adapters (supports hybrid graphics: iGPU vs dGPU + Virtual Display Driver)
            for (uint adapterIdx = 0; factory.EnumAdapters1(adapterIdx, out var adapter).Success; adapterIdx++)
            {
                using (adapter)
                {
                    var desc = adapter.Description1;
                    if ((desc.Flags & AdapterFlags.Software) != 0) continue;

                    for (uint outIdx = 0; adapter.EnumOutputs(outIdx, out var output).Success; outIdx++)
                    {
                        using (output)
                        {
                            var outDesc = output.Description;
                            string devName = outDesc.DeviceName ?? string.Empty;
                            bool match = false;

                            if (!string.IsNullOrEmpty(targetDeviceName))
                            {
                                match = devName.Equals(targetDeviceName, StringComparison.OrdinalIgnoreCase);
                            }
                            else if (outputIndex >= 0)
                            {
                                match = (globalOutCount == outputIndex) || (outIdx == (uint)outputIndex);
                            }
                            else
                            {
                                match = true;
                            }

                            globalOutCount++;
                            if (!match) continue;

                            ID3D11Device? device = null;
                            ID3D11DeviceContext? context = null;
                            try
                            {
                                var featureLevels = new[]
                                {
                                    FeatureLevel.Level_11_1,
                                    FeatureLevel.Level_11_0
                                };

                                var createFlags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
                                var hr = D3D11.D3D11CreateDevice(
                                    adapter,
                                    DriverType.Unknown,
                                    createFlags,
                                    featureLevels,
                                    out device,
                                    out context);
                                if (hr.Failure)
                                {
                                    hr = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, featureLevels, out device, out context);
                                }

                                if (hr.Failure || device == null || context == null)
                                {
                                    try { device?.Dispose(); } catch { }
                                    try { context?.Dispose(); } catch { }
                                    continue;
                                }

                                using var output1 = output.QueryInterface<IDXGIOutput1>();
                                if (output1 == null)
                                {
                                    device.Dispose();
                                    context.Dispose();
                                    continue;
                                }

                                var duplication = output1.DuplicateOutput(device);
                                if (duplication == null)
                                {
                                    device.Dispose();
                                    context.Dispose();
                                    continue;
                                }

                                _device = device;
                                _context = context;
                                _duplication = duplication;
                                _originX = outDesc.DesktopCoordinates.Left;
                                _originY = outDesc.DesktopCoordinates.Top;
                                _width = (outDesc.DesktopCoordinates.Right - outDesc.DesktopCoordinates.Left) & ~1;
                                _height = (outDesc.DesktopCoordinates.Bottom - outDesc.DesktopCoordinates.Top) & ~1;

                                Console.WriteLine($"[DxgiScreenCapture] Başarıyla yakalandı: {devName} ({_width}x{_height} @ {_originX},{_originY}) [{desc.Description}]");

                                // Pre-allocate zero-GC reusable pixel buffer (preserve if same size during AccessLost/UAC recovery)
                                int requiredBytes = _width * _height * 4;
                                if (_reusableRgbaData == null || _reusableRgbaData.Length != requiredBytes)
                                {
                                    _reusableRgbaData = new byte[requiredBytes];
                                    _cleanDesktopRgba = new byte[requiredBytes];
                                    _hasAnySurface = false;
                                }

                                // Create staging texture for CPU readback
                                var textureDesc = new Texture2DDescription
                                {
                                    Width = (uint)_width,
                                    Height = (uint)_height,
                                    MipLevels = 1,
                                    ArraySize = 1,
                                    Format = Format.B8G8R8A8_UNorm,
                                    SampleDescription = new SampleDescription(1, 0),
                                    Usage = ResourceUsage.Staging,
                                    BindFlags = BindFlags.None,
                                    CPUAccessFlags = CpuAccessFlags.Read,
                                    MiscFlags = ResourceOptionFlags.None
                                };

                                _stagingTexture = _device.CreateTexture2D(textureDesc);
                                TryInitGpuPath();
                                return true;
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[DxgiScreenCapture] Yakalama başlatma istisnası: {ex.Message}");
                                try { device?.Dispose(); } catch { }
                                try { context?.Dispose(); } catch { }
                                ReleaseDuplicationObjects();
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DxgiScreenCapture] DXGI fabrika/enum istisnası: {ex.Message}");
            ReleaseDuplicationObjects();
        }

        return false;
    }

    private CapturedFrame? GetCachedFrameWithCursor()
    {
        if (!_hasAnySurface || _reusableRgbaData == null || _width <= 0 || _height <= 0)
            return null;

        try
        {
            RestoreLastCursorPatch();
            GdiScreenCapture.DrawCursorOntoBgraBuffer(_reusableRgbaData, _width, _height, _originX, _originY, out _lastCursorRect);
        }
        catch { }

        return new CapturedFrame
        {
            RgbaData = _reusableRgbaData,
            Width = _width,
            Height = _height,
            TimestampUs = _lastPresentTimeUs,
            IsNew = false,
            ForceKeyframe = false
        };
    }

    public CapturedFrame? AcquireNextFrame(int timeoutMs = 0)
    {
        try
        {
            if (_duplication == null || _context == null || _stagingTexture == null)
            {
                // Throttle re-initialization attempts (e.g. while UAC / Winlogon secure desktop is active) to once every 200 ms
                long nowTicks = Stopwatch.GetTimestamp();
                if (nowTicks - _lastInitAttemptTicks < Stopwatch.Frequency / 5)
                {
                    return CachedFrame();
                }

                if (!Initialize(_targetDeviceName, _outputIndex))
                {
                    return CachedFrame();
                }

                // Give the newly re-attached duplication session up to 8ms to latch the desktop surface
                if (timeoutMs == 0) timeoutMs = 8;
            }

            if (_reusableRgbaData == null)
                return null;

            _cleanDesktopRgba ??= new byte[_width * _height * 4];

            var result = _duplication!.AcquireNextFrame(
                (uint)timeoutMs,
                out var frameInfo,
                out var desktopResource);

            if (result == DxgiResultCode.WaitTimeout)
            {
                return CachedFrame();
            }

            if (result.Failure || desktopResource == null)
            {
                if (result == DxgiResultCode.AccessLost ||
                    result == DxgiResultCode.AccessDenied ||
                    result == DxgiResultCode.InvalidCall)
                {
                    // Release COM duplication pointers without wiping _reusableRgbaData or recursing;
                    // the top of AcquireNextFrame will cleanly re-initialize after 200ms once UAC/mode-switch finishes.
                    ReleaseDuplicationObjects();
                    _lastInitAttemptTicks = Stopwatch.GetTimestamp();
                }
                return CachedFrame();
            }

            using (desktopResource)
            using (var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>())
            {
                if (desktopTexture == null)
                {
                    try { _duplication.ReleaseFrame(); } catch { }
                    return CachedFrame();
                }

                if (_gpu != null && _desktopCopy != null)
                {
                    // GPU path: keep a persistent copy of the desktop on the GPU, convert to NV12 there
                    _context!.CopyResource(_desktopCopy, desktopTexture);
                    try { _duplication.ReleaseFrame(); } catch { }
                    _hasGpuFrame = true;
                    _lastPresentTimeUs = (long)(frameInfo.LastPresentTime / 10);
                    return BuildGpuFrame();
                }

                _context!.CopyResource(_stagingTexture!, desktopTexture);
                try { _duplication.ReleaseFrame(); } catch { }

                var mapped = _context.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    int rowPitch = (int)mapped.RowPitch;
                    int rowBytes = _width * 4;
                    long totalBytes = (long)_width * _height * 4;
                    bool wasVisibleBefore = _hasVisibleDesktop;
                    bool hasVisibleDesktopPixels = _hasVisibleDesktop;

                    unsafe
                    {
                        byte* srcPtr = (byte*)mapped.DataPointer.ToPointer();

                        // Until the first non-blank DWM desktop frame arrives (e.g. during ExtendDesktop or after UAC recovery),
                        // check if the frame has visible desktop pixels so we know the exact moment DWM finishes painting.
                        if (!hasVisibleDesktopPixels)
                        {
                            int stride = Math.Max(64, (int)(totalBytes / 4096) & ~3);
                            for (long idx = 0; idx < totalBytes - 4; idx += stride)
                            {
                                if (srcPtr[idx] != 0 || srcPtr[idx + 1] != 0 || srcPtr[idx + 2] != 0)
                                {
                                    hasVisibleDesktopPixels = true;
                                    break;
                                }
                            }
                        }

                        fixed (byte* dstClean = _cleanDesktopRgba)
                        fixed (byte* dstWork = _reusableRgbaData)
                        {
                            if (rowPitch == rowBytes)
                            {
                                Buffer.MemoryCopy(srcPtr, dstClean, totalBytes, totalBytes);
                                Buffer.MemoryCopy(srcPtr, dstWork, totalBytes, totalBytes);
                            }
                            else
                            {
                                for (int y = 0; y < _height; y++)
                                {
                                    byte* sRow = srcPtr + (y * rowPitch);
                                    int dOff = y * rowBytes;
                                    Buffer.MemoryCopy(sRow, dstClean + dOff, rowBytes, rowBytes);
                                    Buffer.MemoryCopy(sRow, dstWork + dOff, rowBytes, rowBytes);
                                }
                            }
                        }
                    }

                    GdiScreenCapture.DrawCursorOntoBgraBuffer(_reusableRgbaData, _width, _height, _originX, _originY, out _lastCursorRect);

                    _hasAnySurface = true;
                    if (hasVisibleDesktopPixels)
                    {
                        _hasVisibleDesktop = true;
                    }
                    _lastPresentTimeUs = (long)(frameInfo.LastPresentTime / 10);

                    return new CapturedFrame
                    {
                        RgbaData = _reusableRgbaData,
                        Width = _width,
                        Height = _height,
                        TimestampUs = _lastPresentTimeUs,
                        IsNew = hasVisibleDesktopPixels,
                        ForceKeyframe = !wasVisibleBefore && hasVisibleDesktopPixels
                    };
                }
                finally
                {
                    try { _context.Unmap(_stagingTexture!, 0); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DxgiScreenCapture] Kare yakalama istisnası (otomatik kurtarılıyor): {ex.Message}");
            ReleaseDuplicationObjects();
            _lastInitAttemptTicks = Stopwatch.GetTimestamp();
            return CachedFrame();
        }
    }

    private System.Drawing.Rectangle _lastCursorRect;

    private void TryInitGpuPath()
    {
        if (_gpuDisabled || _device == null || _context == null) return;
        try
        {
            _desktopCopy = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
            });

            int patch = Math.Min(CursorPatchSize, Math.Min(_width, _height)) & ~1;
            _cursorStaging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)patch,
                Height = (uint)patch,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read
            });

            _gpu = GpuNv12Converter.TryCreate(_device, _context, _desktopCopy, _width, _height, out var err);
            if (_gpu == null)
            {
                Console.WriteLine($"[DxgiScreenCapture] GPU NV12 dönüşümü kullanılamıyor, CPU yolu: {err}");
                _gpuDisabled = true; // don't retry on every UAC re-init
                _desktopCopy.Dispose(); _desktopCopy = null;
                _cursorStaging.Dispose(); _cursorStaging = null;
                return;
            }

            int nv12Size = _width * _height * 3 / 2;
            if (_nv12Data == null || _nv12Data.Length != nv12Size) _nv12Data = new byte[nv12Size];
            _cursorPatchBgra = new byte[patch * patch * 4];
            _cursorPatchNv12 = new byte[patch * patch * 3 / 2];
            Console.WriteLine("[DxgiScreenCapture] GPU BGRA->NV12 (D3D11 Video Processor) devrede");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DxgiScreenCapture] GPU yolu başlatılamadı, CPU yolu: {ex.Message}");
            _gpuDisabled = true;
            try { _gpu?.Dispose(); } catch { }
            _gpu = null;
            try { _desktopCopy?.Dispose(); } catch { }
            _desktopCopy = null;
            try { _cursorStaging?.Dispose(); } catch { }
            _cursorStaging = null;
        }
    }

    private CapturedFrame? CachedFrame()
    {
        if (_gpu != null && _hasGpuFrame)
            return BuildGpuFrame();

        // GPU objects lost (UAC / mode switch): repeat last NV12 frame as-is
        if (_gpu == null && !_gpuDisabled && _lastNv12Valid && _nv12Data != null)
        {
            return new CapturedFrame { Nv12Data = _nv12Data, Width = _width, Height = _height, TimestampUs = _lastPresentTimeUs };
        }

        return GetCachedFrameWithCursor();
    }

    private bool _lastNv12Valid;

    private unsafe CapturedFrame BuildGpuFrame()
    {
        _gpu!.Convert(_nv12Data);

        bool wasVisibleBefore = _hasVisibleDesktop;
        if (!_hasVisibleDesktop)
        {
            // Black in limited-range Y is 16; any brighter sample means DWM has painted the desktop
            int ySize = _width * _height;
            int stride = Math.Max(64, ySize / 4096);
            for (int i = 0; i < ySize; i += stride)
            {
                if (_nv12Data![i] > 20) { _hasVisibleDesktop = true; break; }
            }
        }

        try { OverlayCursorNv12(); } catch { }
        _lastNv12Valid = true;

        return new CapturedFrame
        {
            Nv12Data = _nv12Data,
            Width = _width,
            Height = _height,
            TimestampUs = _lastPresentTimeUs,
            IsNew = _hasVisibleDesktop,
            ForceKeyframe = !wasVisibleBefore && _hasVisibleDesktop
        };
    }

    /// <summary>
    /// Reads back only a small BGRA patch around the cursor, draws the real Windows cursor on it with GDI,
    /// converts that patch to NV12 and splices it into the frame (~0.1 ms).
    /// </summary>
    private unsafe void OverlayCursorNv12()
    {
        if (_cursorStaging == null || _desktopCopy == null || _cursorPatchBgra == null || _cursorPatchNv12 == null || _nv12Data == null)
            return;
        if (!GetCursorPos(out var pt)) return;

        int patch = (int)_cursorStaging.Description.Width;
        int relX = pt.X - _originX;
        int relY = pt.Y - _originY;
        if (relX < -patch / 2 || relX >= _width + patch / 2 || relY < -patch / 2 || relY >= _height + patch / 2) return;

        int px = Math.Clamp(relX - patch / 2, 0, _width - patch) & ~1;
        int py = Math.Clamp(relY - patch / 2, 0, _height - patch) & ~1;

        _context!.CopySubresourceRegion(_cursorStaging, 0, 0, 0, 0, _desktopCopy, 0,
            new Vortice.Mathematics.Box(px, py, 0, px + patch, py + patch, 1));

        var mapped = _context.Map(_cursorStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte* src = (byte*)mapped.DataPointer;
            int pitch = (int)mapped.RowPitch;
            fixed (byte* dst = _cursorPatchBgra)
            {
                for (int y = 0; y < patch; y++)
                    Buffer.MemoryCopy(src + (long)y * pitch, dst + y * patch * 4, patch * 4, patch * 4);
            }
        }
        finally
        {
            _context.Unmap(_cursorStaging, 0);
        }

        GdiScreenCapture.DrawCursorOntoBgraBuffer(_cursorPatchBgra, patch, patch, _originX + px, _originY + py, out var drawn);
        if (drawn.Width <= 0 || drawn.Height <= 0) return;

        Encoding.ColorConverter.BgraToNv12(_cursorPatchBgra, _cursorPatchNv12, patch, patch);

        fixed (byte* pFrame = _nv12Data)
        fixed (byte* pPatch = _cursorPatchNv12)
        {
            for (int y = 0; y < patch; y++)
                Buffer.MemoryCopy(pPatch + y * patch, pFrame + (long)(py + y) * _width + px, patch, patch);

            byte* frameUv = pFrame + (long)_width * _height;
            byte* patchUv = pPatch + patch * patch;
            for (int y = 0; y < patch / 2; y++)
                Buffer.MemoryCopy(patchUv + y * patch, frameUv + (long)(py / 2 + y) * _width + px, patch, patch);
        }
    }

    private unsafe void RestoreLastCursorPatch()
    {
        if (_cleanDesktopRgba == null || _reusableRgbaData == null) return;
        var r = _lastCursorRect;
        if (r.Width <= 0 || r.Height <= 0) return;

        fixed (byte* pClean = _cleanDesktopRgba)
        fixed (byte* pWork = _reusableRgbaData)
        {
            for (int row = 0; row < r.Height; row++)
            {
                int offset = ((r.Y + row) * _width + r.X) * 4;
                Buffer.MemoryCopy(pClean + offset, pWork + offset, r.Width * 4, r.Width * 4);
            }
        }
        _lastCursorRect = default;
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            ReleaseDuplicationObjects();
            _hasAnySurface = false;
            _hasVisibleDesktop = false;
        }
    }
}
