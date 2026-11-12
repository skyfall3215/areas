using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SecondScreen.Media.Capture;

/// <summary>
/// Sunshine-style GPU colour conversion: BGRA desktop texture -> NV12 using the fixed-function
/// D3D11 Video Processor, then reads back only the NV12 planes (1.5 B/px instead of 4 B/px).
/// Removes the CPU BGRA->NV12 pass entirely.
/// </summary>
public sealed class GpuNv12Converter : IDisposable
{
    private readonly ID3D11DeviceContext _context;
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11VideoProcessorInputView? _inputView;
    private ID3D11Texture2D? _nv12Texture;
    private ID3D11VideoProcessorOutputView? _outputView;
    private ID3D11Texture2D? _nv12Staging;
    private readonly int _width;
    private readonly int _height;

    public int Width => _width;
    public int Height => _height;
    public int Nv12Size => _width * _height * 3 / 2;

    private GpuNv12Converter(ID3D11DeviceContext context, int width, int height)
    {
        _context = context;
        _width = width;
        _height = height;
    }

    /// <summary>Returns null if the GPU/driver cannot do BGRA->NV12 video processing.</summary>
    public static GpuNv12Converter? TryCreate(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D source, int width, int height, out string? error)
    {
        error = null;
        var conv = new GpuNv12Converter(context, width, height);
        try
        {
            conv._videoDevice = device.QueryInterface<ID3D11VideoDevice>();
            conv._videoContext = context.QueryInterface<ID3D11VideoContext>();

            var contentDesc = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational(60, 1),
                InputWidth = (uint)width,
                InputHeight = (uint)height,
                OutputFrameRate = new Rational(60, 1),
                OutputWidth = (uint)width,
                OutputHeight = (uint)height,
                Usage = VideoUsage.OptimalSpeed
            };
            conv._videoDevice.CreateVideoProcessorEnumerator(ref contentDesc, out conv._enumerator).CheckError();
            conv._videoDevice.CreateVideoProcessor(conv._enumerator!, 0, out conv._processor).CheckError();

            conv._videoDevice.CreateVideoProcessorInputView(source, conv._enumerator!, new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 }
            }, out conv._inputView).CheckError();

            conv._nv12Texture = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget
            });

            conv._videoDevice.CreateVideoProcessorOutputView(conv._nv12Texture, conv._enumerator!, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 }
            }, out conv._outputView).CheckError();

            conv._nv12Staging = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read
            });

            // Full-range RGB in -> BT.601 limited-range YCbCr out (matches ColorConverter / decoder default)
            var vctx = conv._videoContext;
            vctx.VideoProcessorSetStreamFrameFormat(conv._processor!, 0, VideoFrameFormat.Progressive);
            vctx.VideoProcessorSetStreamAutoProcessingMode(conv._processor!, 0, false);
            vctx.VideoProcessorSetStreamColorSpace(conv._processor!, 0, new VideoProcessorColorSpace
            {
                Usage = 0,
                RGB_Range = 0,
                YCbCr_Matrix = 0,
                Nominal_Range = (uint)VideoProcessorNominalRange.Range_0_255
            });
            vctx.VideoProcessorSetOutputColorSpace(conv._processor!, new VideoProcessorColorSpace
            {
                Usage = 0,
                RGB_Range = 1,
                YCbCr_Matrix = 0,
                Nominal_Range = (uint)VideoProcessorNominalRange.Range_16_235
            });

            return conv;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            conv.Dispose();
            return null;
        }
    }

    /// <summary>Converts the source texture bound at creation into tightly packed NV12 (stride = width).</summary>
    public unsafe void Convert(Span<byte> nv12Destination)
    {
        if (nv12Destination.Length < Nv12Size) throw new ArgumentException("NV12 buffer too small", nameof(nv12Destination));

        var stream = new VideoProcessorStream
        {
            Enable = true,
            InputSurface = _inputView!
        };
        _videoContext!.VideoProcessorBlt(_processor!, _outputView!, 0, 1, new[] { stream }).CheckError();
        _context.CopyResource(_nv12Staging!, _nv12Texture!);

        var mapped = _context.Map(_nv12Staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte* src = (byte*)mapped.DataPointer;
            int pitch = (int)mapped.RowPitch;
            fixed (byte* dst = nv12Destination)
            {
                // Y plane (height rows) followed by interleaved UV plane (height/2 rows) at src + pitch*height
                for (int y = 0; y < _height; y++)
                    Buffer.MemoryCopy(src + (long)y * pitch, dst + (long)y * _width, _width, _width);

                byte* srcUv = src + (long)pitch * _height;
                byte* dstUv = dst + (long)_width * _height;
                for (int y = 0; y < _height / 2; y++)
                    Buffer.MemoryCopy(srcUv + (long)y * pitch, dstUv + (long)y * _width, _width, _width);
            }
        }
        finally
        {
            _context.Unmap(_nv12Staging!, 0);
        }
    }

    public void Dispose()
    {
        _nv12Staging?.Dispose(); _nv12Staging = null;
        _outputView?.Dispose(); _outputView = null;
        _nv12Texture?.Dispose(); _nv12Texture = null;
        _inputView?.Dispose(); _inputView = null;
        _processor?.Dispose(); _processor = null;
        _enumerator?.Dispose(); _enumerator = null;
        _videoContext?.Dispose(); _videoContext = null;
        _videoDevice?.Dispose(); _videoDevice = null;
    }
}
