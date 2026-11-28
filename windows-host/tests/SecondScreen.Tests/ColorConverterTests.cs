using ColorConverter = SecondScreen.Media.Encoding.ColorConverter;

namespace SecondScreen.Tests;

public class ColorConverterTests
{
    private static void ScalarReferenceBgraToNv12(ReadOnlySpan<byte> bgra, Span<byte> nv12, int width, int height)
    {
        int uvOffset = width * height;
        int totalPairs = height / 2;

        for (int yHalf = 0; yHalf < totalPairs; yHalf++)
        {
            int y0 = yHalf * 2;
            int y1 = y0 + 1;
            int row0Base = y0 * width * 4;
            int row1Base = y1 * width * 4;
            int yDst0Base = y0 * width;
            int yDst1Base = y1 * width;
            int uvDstBase = uvOffset + yHalf * width;

            for (int x = 0; x < width; x += 2)
            {
                int i0 = x * 4;
                int b00 = bgra[row0Base + i0], g00 = bgra[row0Base + i0 + 1], r00 = bgra[row0Base + i0 + 2];
                int b10 = bgra[row0Base + i0 + 4], g10 = bgra[row0Base + i0 + 5], r10 = bgra[row0Base + i0 + 6];
                int b01 = bgra[row1Base + i0], g01 = bgra[row1Base + i0 + 1], r01 = bgra[row1Base + i0 + 2];
                int b11 = bgra[row1Base + i0 + 4], g11 = bgra[row1Base + i0 + 5], r11 = bgra[row1Base + i0 + 6];

                nv12[yDst0Base + x] = (byte)(((66 * r00 + 129 * g00 + 25 * b00 + 128) >> 8) + 16);
                nv12[yDst0Base + x + 1] = (byte)(((66 * r10 + 129 * g10 + 25 * b10 + 128) >> 8) + 16);
                nv12[yDst1Base + x] = (byte)(((66 * r01 + 129 * g01 + 25 * b01 + 128) >> 8) + 16);
                nv12[yDst1Base + x + 1] = (byte)(((66 * r11 + 129 * g11 + 25 * b11 + 128) >> 8) + 16);

                int rAvg = (r00 + r10 + r01 + r11) >> 2;
                int gAvg = (g00 + g10 + g01 + g11) >> 2;
                int bAvg = (b00 + b10 + b01 + b11) >> 2;

                nv12[uvDstBase + x] = (byte)(((-38 * rAvg - 74 * gAvg + 112 * bAvg + 128) >> 8) + 128);
                nv12[uvDstBase + x + 1] = (byte)(((112 * rAvg - 94 * gAvg - 18 * bAvg + 128) >> 8) + 128);
            }
        }
    }

    private static void AssertMatchesScalarReference(byte[] bgra, int width, int height, int maxAllowedError = 0)
    {
        int nv12Size = width * height * 3 / 2;
        byte[] actual = new byte[nv12Size];
        byte[] expected = new byte[nv12Size];

        ColorConverter.BgraToNv12(bgra, actual, width, height);
        ScalarReferenceBgraToNv12(bgra, expected, width, height);

        int maxError = 0;
        int firstBadIndex = -1;
        for (int i = 0; i < nv12Size; i++)
        {
            int diff = Math.Abs(actual[i] - expected[i]);
            if (diff > maxError)
            {
                maxError = diff;
                if (firstBadIndex < 0 && diff > maxAllowedError)
                {
                    firstBadIndex = i;
                }
            }
        }

        Assert.True(
            maxError <= maxAllowedError,
            $"NV12 mismatch for {width}x{height}: maxError={maxError} (allowed <= {maxAllowedError}), " +
            $"first mismatch at byte {firstBadIndex} (actual={(firstBadIndex >= 0 ? actual[firstBadIndex] : 0)}, " +
            $"expected={(firstBadIndex >= 0 ? expected[firstBadIndex] : 0)})");
    }

    [Theory]
    [InlineData(0, 0, 0, 16, 128, 128)]       // Black
    [InlineData(255, 255, 255, 235, 128, 128)] // White (tests ushort Y sum = 56,228 > short.MaxValue)
    [InlineData(255, 0, 0, 82, 90, 240)]      // Red
    [InlineData(0, 255, 0, 144, 54, 34)]      // Green (tests ushort G*129 = 32,895 > short.MaxValue)
    [InlineData(0, 0, 255, 41, 240, 110)]     // Blue
    [InlineData(255, 255, 0, 210, 16, 146)]   // Yellow (min U = -28,432)
    [InlineData(0, 255, 255, 169, 166, 16)]   // Cyan (min V = -28,432)
    [InlineData(255, 0, 255, 107, 202, 222)]  // Magenta
    public void BgraToNv12_SolidColors_MatchScalarReferenceExactly(
        byte r, byte g, byte b, byte expectedY, byte expectedU, byte expectedV)
    {
        const int width = 64;
        const int height = 32;
        byte[] bgra = new byte[width * height * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = b;
            bgra[i + 1] = g;
            bgra[i + 2] = r;
            bgra[i + 3] = 255;
        }

        AssertMatchesScalarReference(bgra, width, height, maxAllowedError: 0);

        byte[] nv12 = new byte[width * height * 3 / 2];
        ColorConverter.BgraToNv12(bgra, nv12, width, height);

        int ySize = width * height;
        for (int i = 0; i < ySize; i++)
        {
            Assert.Equal(expectedY, nv12[i]);
        }

        for (int i = ySize; i < nv12.Length; i += 2)
        {
            Assert.Equal(expectedU, nv12[i]);
            Assert.Equal(expectedV, nv12[i + 1]);
        }
    }

    [Theory]
    [InlineData(256, 128)]
    [InlineData(270, 260)] // Exercises multi-threaded path (height > 256) + non-multiple-of-16 width
    public void BgraToNv12_Gradients_MatchScalarReferenceExactly(int width, int height)
    {
        byte[] bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bgra[i] = (byte)((x * 255) / Math.Max(1, width - 1));
                bgra[i + 1] = (byte)((y * 255) / Math.Max(1, height - 1));
                bgra[i + 2] = (byte)(((x + y) * 255) / Math.Max(1, width + height - 2));
                bgra[i + 3] = (byte)((x ^ y) & 0xFF); // Arbitrary alpha must be ignored
            }
        }

        AssertMatchesScalarReference(bgra, width, height, maxAllowedError: 0);
    }

    [Theory]
    [InlineData(6, 4)]       // Scalar-only tail (4 + 2)
    [InlineData(8, 4)]       // Exact SSE2/SSSE3 8-pixel width
    [InlineData(14, 6)]      // SSE2 8 + scalar 4 + scalar 2
    [InlineData(24, 8)]      // AVX2 16 + SSE2 8
    [InlineData(34, 18)]     // Non-multiple-of-16 (AVX2 16x2 + scalar 2)
    [InlineData(192, 192)]   // Small cursor patch (height <= 256 single-thread bypass)
    [InlineData(1366, 768)]  // Non-multiple-of-16 laptop display (multi-threaded)
    [InlineData(1920, 1080)] // Standard Full HD (multi-threaded)
    public void BgraToNv12_PseudoRandomResolutions_MatchScalarReferenceExactly(int width, int height)
    {
        byte[] bgra = new byte[width * height * 4];
        var rng = new Random(width * 397 ^ height);
        rng.NextBytes(bgra);

        AssertMatchesScalarReference(bgra, width, height, maxAllowedError: 0);
    }

    [Fact]
    public void BgraToNv12_BufferTooSmall_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            ColorConverter.BgraToNv12(new byte[16 * 16 * 4 - 1], new byte[16 * 16 * 3 / 2], 16, 16));

        Assert.Throws<ArgumentException>(() =>
            ColorConverter.BgraToNv12(new byte[16 * 16 * 4], new byte[16 * 16 * 3 / 2 - 1], 16, 16));
    }
}
