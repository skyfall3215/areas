using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using DxgiResultCode = Vortice.DXGI.ResultCode;

namespace SecondScreen.Media.Capture;

public sealed class CapturedFrame
{
    public byte[]? RgbaData { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long TimestampUs { get; init; }
    public bool IsNew { get; init; }
}

public sealed class DxgiScreenCapture : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;
    private string? _targetDeviceName;
    private int _outputIndex;
    private int _width;
    private int _height;
    private int _originX;
    private int _originY;
    private byte[]? _reusableRgbaData;
    private bool _hasAnySurface;
    private bool _hasVisibleDesktop;
    private long _lastPresentTimeUs;
    private bool _isDisposed;

    public int Width => _width;
    public int Height => _height;
    public bool IsInitialized => _duplication != null;

    public bool Initialize(string? targetDeviceName = null, int outputIndex = 0)
    {
        bool hadAnySurface = _hasAnySurface;
        Dispose();
        _isDisposed = false;
        _hasAnySurface = hadAnySurface && _reusableRgbaData != null;
        _hasVisibleDesktop = false;
        _targetDeviceName = targetDeviceName;
        _outputIndex = outputIndex;

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

                        try
                        {
                            var featureLevels = new[]
                            {
                                FeatureLevel.Level_11_1,
                                FeatureLevel.Level_11_0
                            };

                            var createFlags = DeviceCreationFlags.BgraSupport;
                            var hr = D3D11.D3D11CreateDevice(
                                adapter,
                                DriverType.Unknown,
                                createFlags,
                                featureLevels,
                                out ID3D11Device device,
                                out ID3D11DeviceContext context);

                            if (hr.Failure)
                            {
                                Console.WriteLine($"[DxgiScreenCapture] D3D11CreateDevice failed for {devName} on {desc.Description}: {hr}");
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
                                Console.WriteLine($"[DxgiScreenCapture] DuplicateOutput failed for {devName} on {desc.Description}");
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

                            // Pre-allocate zero-GC reusable pixel buffer (preserve if same size during AccessLost recovery)
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
                            return true;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[DxgiScreenCapture] Yakalama başlatma istisnası: {ex.Message}");
                            Dispose();
                        }
                    }
                }
            }
        }

        return false;
    }

    private byte[]? _cleanDesktopRgba;

    public CapturedFrame? AcquireNextFrame(int timeoutMs = 0)
    {
        if (_duplication == null || _context == null || _stagingTexture == null || _reusableRgbaData == null)
            return null;

        _cleanDesktopRgba ??= new byte[_width * _height * 4];

        var result = _duplication.AcquireNextFrame(
            (uint)timeoutMs,
            out var frameInfo,
            out var desktopResource);

        if (result == DxgiResultCode.WaitTimeout)
        {
            if (_hasAnySurface)
            {
                RestoreLastCursorPatch();
                GdiScreenCapture.DrawCursorOntoBgraBuffer(_reusableRgbaData, _width, _height, _originX, _originY, out _lastCursorRect);
                return new CapturedFrame
                {
                    RgbaData = _reusableRgbaData,
                    Width = _width,
                    Height = _height,
                    TimestampUs = _lastPresentTimeUs,
                    IsNew = false
                };
            }
            return null;
        }

        if (result.Failure || desktopResource == null)
        {
            if (result == DxgiResultCode.AccessLost ||
                result == DxgiResultCode.AccessDenied ||
                result == DxgiResultCode.InvalidCall)
            {
                if (Initialize(_targetDeviceName, _outputIndex))
                {
                    // Immediately attempt one quick read on the re-created duplication session
                    return AcquireNextFrame(16);
                }
            }
            if (_hasAnySurface && _reusableRgbaData != null)
            {
                RestoreLastCursorPatch();
                GdiScreenCapture.DrawCursorOntoBgraBuffer(_reusableRgbaData, _width, _height, _originX, _originY, out _lastCursorRect);
                return new CapturedFrame { RgbaData = _reusableRgbaData, Width = _width, Height = _height, TimestampUs = _lastPresentTimeUs, IsNew = false };
            }
            return null;
        }

        using (desktopResource)
        using (var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>())
        {
            if (desktopTexture == null)
            {
                _duplication.ReleaseFrame();
                return null;
            }

            _context.CopyResource(_stagingTexture, desktopTexture);
            _duplication.ReleaseFrame();

            var mapped = _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int rowPitch = (int)mapped.RowPitch;
                int rowBytes = _width * 4;
                long totalBytes = (long)_width * _height * 4;
                bool hasVisibleDesktopPixels = _hasVisibleDesktop;

                unsafe
                {
                    byte* srcPtr = (byte*)mapped.DataPointer.ToPointer();

                    // Until the first non-blank DWM desktop frame arrives (e.g. during the ~1.5s ExtendDesktop transition),
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
                    IsNew = hasVisibleDesktopPixels
                };
            }
            finally
            {
                _context.Unmap(_stagingTexture, 0);
            }
        }
    }

    private System.Drawing.Rectangle _lastCursorRect;

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
            _stagingTexture?.Dispose();
            _stagingTexture = null;
            _duplication?.Dispose();
            _duplication = null;
            _context?.Dispose();
            _context = null;
            _device?.Dispose();
            _device = null;
            _hasAnySurface = false;
            _hasVisibleDesktop = false;
        }
    }
}
