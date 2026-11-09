namespace SecondScreen.Media.Encoding;

public static class ColorConverter
{
    /// <summary>
    /// Converts BGRA (or RGBA) 32-bit pixel data to NV12 format using unsafe parallel pointers.
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

            // Process chunks across CPU worker threads (zero task allocation overhead)
            Parallel.For(0, numThreads, threadIdx =>
            {
                int startPair = threadIdx * chunkSize;
                int endPair = Math.Min(startPair + chunkSize, totalPairs);

                for (int yHalf = startPair; yHalf < endPair; yHalf++)
                {
                    int y0 = yHalf * 2;
                    int y1 = y0 + 1;

                    int row0Offset = y0 * width * 4;
                    int row1Offset = y1 * width * 4;

                    int yDst0 = y0 * width;
                    int yDst1 = y1 * width;
                    int uvDst = uvOffset + (yHalf * width);

                    for (int x = 0; x < width; x += 2)
                    {
                        int x0 = x;
                        int x1 = x + 1;

                        // Pixel (x0, y0)
                        int i00 = row0Offset + (x0 * 4);
                        byte b00 = src[i00];
                        byte g00 = src[i00 + 1];
                        byte r00 = src[i00 + 2];

                        // Pixel (x1, y0)
                        int i10 = row0Offset + (x1 * 4);
                        byte b10 = src[i10];
                        byte g10 = src[i10 + 1];
                        byte r10 = src[i10 + 2];

                        // Pixel (x0, y1)
                        int i01 = row1Offset + (x0 * 4);
                        byte b01 = src[i01];
                        byte g01 = src[i01 + 1];
                        byte r01 = src[i01 + 2];

                        // Pixel (x1, y1)
                        int i11 = row1Offset + (x1 * 4);
                        byte b11 = src[i11];
                        byte g11 = src[i11 + 1];
                        byte r11 = src[i11 + 2];

                        // Rec. 601 coefficients
                        // Y = 0.299R + 0.587G + 0.114B
                        dst[yDst0 + x0] = (byte)(((66 * r00 + 129 * g00 + 25 * b00 + 128) >> 8) + 16);
                        dst[yDst0 + x1] = (byte)(((66 * r10 + 129 * g10 + 25 * b10 + 128) >> 8) + 16);
                        dst[yDst1 + x0] = (byte)(((66 * r01 + 129 * g01 + 25 * b01 + 128) >> 8) + 16);
                        dst[yDst1 + x1] = (byte)(((66 * r11 + 129 * g11 + 25 * b11 + 128) >> 8) + 16);

                        // Average 2x2 block for U and V
                        int rAvg = (r00 + r10 + r01 + r11) >> 2;
                        int gAvg = (g00 + g10 + g01 + g11) >> 2;
                        int bAvg = (b00 + b10 + b01 + b11) >> 2;

                        // U = -0.169R - 0.331G + 0.500B + 128
                        // V = 0.500R - 0.419G - 0.081B + 128
                        byte u = (byte)(((-38 * rAvg - 74 * gAvg + 112 * bAvg + 128) >> 8) + 128);
                        byte v = (byte)(((112 * rAvg - 94 * gAvg - 18 * bAvg + 128) >> 8) + 128);

                        dst[uvDst + x0] = u;
                        dst[uvDst + x1] = v;
                    }
                }
            });
        }
    }
}
