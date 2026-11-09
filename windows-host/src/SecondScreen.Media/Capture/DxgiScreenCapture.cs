using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using DxgiResultCode = Vortice.DXGI.ResultCode;

namespace SecondScreen.Media.Capture;

public sealed class CapturedFrame : IDisposable
{
    public byte[]? RgbaData { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long TimestampUs { get; init; }
    public ID3D11Texture2D? GpuTexture { get; init; }

    public void Dispose()
    {
        GpuTexture?.Dispose();
    }
}

public sealed class DxgiScreenCapture : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;
    private int _width;
    private int _height;
    private bool _isDisposed;

    public int Width => _width;
    public int Height => _height;
    public bool IsInitialized => _duplication != null;

    public bool Initialize(int outputIndex = 0)
    {
        Dispose();
        _isDisposed = false;

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        if (factory == null) return false;

        // Try adapters (supports hybrid graphics: iGPU vs dGPU)
        for (uint adapterIdx = 0; factory.EnumAdapters1(adapterIdx, out var adapter).Success; adapterIdx++)
        {
            using (adapter)
            {
                var desc = adapter.Description1;
                // Skip basic render driver
                if ((desc.Flags & AdapterFlags.Software) != 0) continue;

                for (uint outIdx = 0; adapter.EnumOutputs(outIdx, out var output).Success; outIdx++)
                {
                    using (output)
                    {
                        if (outIdx != (uint)outputIndex && outputIndex >= 0) continue;

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
                                continue;
                            }

                            _device = device;
                            _context = context;

                            using var output1 = output.QueryInterface<IDXGIOutput1>();
                            if (output1 == null) continue;

                            var duplication = output1.DuplicateOutput(_device);
                            if (duplication == null)
                            {
                                _device?.Dispose();
                                _context?.Dispose();
                                _device = null;
                                _context = null;
                                continue;
                            }

                            _duplication = duplication;
                            var outDesc = output.Description;
                            _width = outDesc.DesktopCoordinates.Right - outDesc.DesktopCoordinates.Left;
                            _height = outDesc.DesktopCoordinates.Bottom - outDesc.DesktopCoordinates.Top;

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
                        catch
                        {
                            // Try next adapter/output
                            Dispose();
                        }
                    }
                }
            }
        }

        return false;
    }

    public CapturedFrame? AcquireNextFrame(int timeoutMs = 100)
    {
        if (_duplication == null || _context == null || _stagingTexture == null)
            return null;

        var result = _duplication.AcquireNextFrame(
            (uint)timeoutMs,
            out var frameInfo,
            out var desktopResource);

        if (result == DxgiResultCode.WaitTimeout)
        {
            return null;
        }

        if (result.Failure || desktopResource == null)
        {
            // If access was lost (e.g. desktop switch or UAC prompt), reinitialize
            if (result == DxgiResultCode.AccessLost || result == DxgiResultCode.AccessDenied)
            {
                Initialize();
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

            // Copy to staging texture
            _context.CopyResource(_stagingTexture, desktopTexture);
            _duplication.ReleaseFrame();

            // Map and copy bytes
            var mapped = _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int rowPitch = (int)mapped.RowPitch;
                byte[] rgbaData = new byte[_width * _height * 4];

                unsafe
                {
                    byte* srcPtr = (byte*)mapped.DataPointer.ToPointer();
                    fixed (byte* dstPtr = rgbaData)
                    {
                        for (int y = 0; y < _height; y++)
                        {
                            Buffer.MemoryCopy(
                                srcPtr + (y * rowPitch),
                                dstPtr + (y * _width * 4),
                                _width * 4,
                                _width * 4);
                        }
                    }
                }

                return new CapturedFrame
                {
                    RgbaData = rgbaData,
                    Width = _width,
                    Height = _height,
                    TimestampUs = (long)(frameInfo.LastPresentTime / 10) // 100ns to microseconds
                };
            }
            finally
            {
                _context.Unmap(_stagingTexture, 0);
            }
        }
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
        }
    }
}
