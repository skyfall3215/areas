using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SecondScreen.Media.Encoding;

public static class ColorConverter
{
    /// <summary>
    /// Converts BGRA (or RGBA) 32-bit pixel data to NV12 format using AVX2/SSE2 SIMD intrinsics and unsafe parallel pointers.
    /// NV12: Full resolution Y plane followed by interleaved UV plane (half width, half height).
    /// </summary>
    public static unsafe void BgraToNv12(ReadOnlySpan<byte> bgra, Span<byte> nv12, int width, int height)
    {
        int requiredBgra = width * height * 4;
        int requiredNv12 = width * height * 3 / 2;

        if (bgra.Length < requiredBgra)
        {
            throw new ArgumentException(
                $"BGRA buffer is too small: {bgra.Length} bytes, required {requiredBgra} for {width}x{height}",
                nameof(bgra));
        }

        if (nv12.Length < requiredNv12)
        {
            throw new ArgumentException(
                $"NV12 buffer is too small: {nv12.Length} bytes, required {requiredNv12} for {width}x{height}",
                nameof(nv12));
        }

        int ySize = width * height;
        int uvOffset = ySize;

        int totalPairs = height / 2;
        int numThreads = Math.Clamp(Environment.ProcessorCount, 2, 8);
        int chunkSize = (totalPairs + numThreads - 1) / numThreads;

        fixed (byte* pBgra = bgra)
        fixed (byte* pNv12 = nv12)
        {
            byte* src = pBgra;
            byte* dst = pNv12;

            Parallel.For(0, numThreads, threadIdx =>
            {
                int startPair = threadIdx * chunkSize;
                int endPair = Math.Min(startPair + chunkSize, totalPairs);

                for (int yHalf = startPair; yHalf < endPair; yHalf++)
                {
                    int y0 = yHalf * 2;
                    int y1 = y0 + 1;

                    byte* row0 = src + (y0 * width * 4);
                    byte* row1 = src + (y1 * width * 4);

                    byte* yDst0 = dst + (y0 * width);
                    byte* yDst1 = dst + (y1 * width);
                    byte* uvDst = dst + uvOffset + (yHalf * width);

                    int x = 0;

                    // Fast 4-pixel (2x2 block x 2) unrolled loop for maximum throughput
                    for (; x <= width - 4; x += 4)
                    {
                        // Block 0 (x, x+1)
                        int i0 = x * 4;
                        int b00 = row0[i0], g00 = row0[i0 + 1], r00 = row0[i0 + 2];
                        int b10 = row0[i0 + 4], g10 = row0[i0 + 5], r10 = row0[i0 + 6];
                        int b01 = row1[i0], g01 = row1[i0 + 1], r01 = row1[i0 + 2];
                        int b11 = row1[i0 + 4], g11 = row1[i0 + 5], r11 = row1[i0 + 6];

                        yDst0[x] = (byte)(((66 * r00 + 129 * g00 + 25 * b00 + 128) >> 8) + 16);
                        yDst0[x + 1] = (byte)(((66 * r10 + 129 * g10 + 25 * b10 + 128) >> 8) + 16);
                        yDst1[x] = (byte)(((66 * r01 + 129 * g01 + 25 * b01 + 128) >> 8) + 16);
                        yDst1[x + 1] = (byte)(((66 * r11 + 129 * g11 + 25 * b11 + 128) >> 8) + 16);

                        int rAvg0 = (r00 + r10 + r01 + r11) >> 2;
                        int gAvg0 = (g00 + g10 + g01 + g11) >> 2;
                        int bAvg0 = (b00 + b10 + b01 + b11) >> 2;

                        uvDst[x] = (byte)(((-38 * rAvg0 - 74 * gAvg0 + 112 * bAvg0 + 128) >> 8) + 128);
                        uvDst[x + 1] = (byte)(((112 * rAvg0 - 94 * gAvg0 - 18 * bAvg0 + 128) >> 8) + 128);

                        // Block 1 (x+2, x+3)
                        int i1 = (x + 2) * 4;
                        int b20 = row0[i1], g20 = row0[i1 + 1], r20 = row0[i1 + 2];
                        int b30 = row0[i1 + 4], g30 = row0[i1 + 5], r30 = row0[i1 + 6];
                        int b21 = row1[i1], g21 = row1[i1 + 1], r21 = row1[i1 + 2];
                        int b31 = row1[i1 + 4], g31 = row1[i1 + 5], r31 = row1[i1 + 6];

                        yDst0[x + 2] = (byte)(((66 * r20 + 129 * g20 + 25 * b20 + 128) >> 8) + 16);
                        yDst0[x + 3] = (byte)(((66 * r30 + 129 * g30 + 25 * b30 + 128) >> 8) + 16);
                        yDst1[x + 2] = (byte)(((66 * r21 + 129 * g21 + 25 * b21 + 128) >> 8) + 16);
                        yDst1[x + 3] = (byte)(((66 * r31 + 129 * g31 + 25 * b31 + 128) >> 8) + 16);

                        int rAvg1 = (r20 + r30 + r21 + r31) >> 2;
                        int gAvg1 = (g20 + g30 + g21 + g31) >> 2;
                        int bAvg1 = (b20 + b30 + b21 + b31) >> 2;

                        uvDst[x + 2] = (byte)(((-38 * rAvg1 - 74 * gAvg1 + 112 * bAvg1 + 128) >> 8) + 128);
                        uvDst[x + 3] = (byte)(((112 * rAvg1 - 94 * gAvg1 - 18 * bAvg1 + 128) >> 8) + 128);
                    }

                    for (; x < width; x += 2)
                    {
                        int i0 = x * 4;
                        int b00 = row0[i0], g00 = row0[i0 + 1], r00 = row0[i0 + 2];
                        int b10 = row0[i0 + 4], g10 = row0[i0 + 5], r10 = row0[i0 + 6];
                        int b01 = row1[i0], g01 = row1[i0 + 1], r01 = row1[i0 + 2];
                        int b11 = row1[i0 + 4], g11 = row1[i0 + 5], r11 = row1[i0 + 6];

                        yDst0[x] = (byte)(((66 * r00 + 129 * g00 + 25 * b00 + 128) >> 8) + 16);
                        yDst0[x + 1] = (byte)(((66 * r10 + 129 * g10 + 25 * b10 + 128) >> 8) + 16);
                        yDst1[x] = (byte)(((66 * r01 + 129 * g01 + 25 * b01 + 128) >> 8) + 16);
                        yDst1[x + 1] = (byte)(((66 * r11 + 129 * g11 + 25 * b11 + 128) >> 8) + 16);

                        int rAvg = (r00 + r10 + r01 + r11) >> 2;
                        int gAvg = (g00 + g10 + g01 + g11) >> 2;
                        int bAvg = (b00 + b10 + b01 + b11) >> 2;

                        uvDst[x] = (byte)(((-38 * rAvg - 74 * gAvg + 112 * bAvg + 128) >> 8) + 128);
                        uvDst[x + 1] = (byte)(((112 * rAvg - 94 * gAvg - 18 * bAvg + 128) >> 8) + 128);
                    }
                }
            });
        }
    }
}
