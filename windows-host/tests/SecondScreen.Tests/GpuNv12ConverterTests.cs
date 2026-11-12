using SecondScreen.Media.Capture;
using SecondScreen.Media.Encoding;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SecondScreen.Tests;

public class GpuNv12ConverterTests
{
    [Fact]
    public void GpuConversion_MatchesCpuColorConverter()
    {
        const int w = 64, h = 64;
        var bgra = new byte[w * h * 4];
        var rnd = new Random(42);
        // 8x8 blocks of random solid colours (avoids chroma-subsampling filter differences at edges)
        for (int by = 0; by < h; by += 8)
        for (int bx = 0; bx < w; bx += 8)
        {
            byte b = (byte)rnd.Next(256), g = (byte)rnd.Next(256), r = (byte)rnd.Next(256);
            for (int y = by; y < by + 8; y++)
            for (int x = bx; x < bx + 8; x++)
            {
                int i = (y * w + x) * 4;
                bgra[i] = b; bgra[i + 1] = g; bgra[i + 2] = r; bgra[i + 3] = 255;
            }
        }

        var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            new[] { FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext context);
        if (hr.Failure || device == null || context == null) return; // no GPU on this machine

        using (device)
        using (context)
        {
            var pin = System.Runtime.InteropServices.GCHandle.Alloc(bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                using var tex = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = w, Height = h, MipLevels = 1, ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
                }, new SubresourceData(pin.AddrOfPinnedObject(), w * 4));

                using var conv = GpuNv12Converter.TryCreate(device, context, tex, w, h, out var err);
                if (conv == null) return; // driver without video processor support -> CPU fallback is used in production

                var gpu = new byte[w * h * 3 / 2];
                var cpu = new byte[w * h * 3 / 2];
                conv.Convert(gpu);
                SecondScreen.Media.Encoding.ColorConverter.BgraToNv12(bgra, cpu, w, h);

                int maxDiff = 0;
                for (int i = 0; i < gpu.Length; i++)
                    maxDiff = Math.Max(maxDiff, Math.Abs(gpu[i] - cpu[i]));

                Assert.True(maxDiff <= 6, $"GPU vs CPU NV12 max diff {maxDiff}");
            }
            finally { pin.Free(); }
        }
    }
}
