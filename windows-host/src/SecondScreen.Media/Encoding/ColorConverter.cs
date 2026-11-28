using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace SecondScreen.Media.Encoding;

public static class ColorConverter
{
    /// <summary>
    /// Converts BGRA 32-bit pixel data to NV12 format using AVX2/SSSE3/SSE2 SIMD intrinsics and unsafe parallel pointers.
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

        fixed (byte* pBgra = bgra)
        fixed (byte* pNv12 = nv12)
        {
            byte* src = pBgra;
            byte* dst = pNv12;

            if (height <= 256)
            {
                ConvertRowPairs(src, dst, width, uvOffset, 0, totalPairs);
                return;
            }

            int numThreads = Math.Clamp(Environment.ProcessorCount, 2, 8);
            int chunkSize = (totalPairs + numThreads - 1) / numThreads;

            Parallel.For(0, numThreads, threadIdx =>
            {
                int startPair = threadIdx * chunkSize;
                int endPair = Math.Min(startPair + chunkSize, totalPairs);
                ConvertRowPairs(src, dst, width, uvOffset, startPair, endPair);
            });
        }
    }

    private static unsafe void ConvertRowPairs(byte* src, byte* dst, int width, int uvOffset, int startPair, int endPair)
    {
        if (Avx2.IsSupported)
        {
            ConvertRowPairsAvx2(src, dst, width, uvOffset, startPair, endPair);
        }
        else if (Sse2.IsSupported)
        {
            ConvertRowPairsSse2(src, dst, width, uvOffset, startPair, endPair);
        }
        else
        {
            ConvertRowPairsScalar(src, dst, width, uvOffset, startPair, endPair);
        }
    }

    private static unsafe void ConvertRowPairsAvx2(byte* src, byte* dst, int width, int uvOffset, int startPair, int endPair)
    {
        Vector128<byte> shufB128 = Vector128.Create(
            (byte)0, 0x80, 4, 0x80, 8, 0x80, 12, 0x80,
            0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
        Vector128<byte> shufG128 = Vector128.Create(
            (byte)1, 0x80, 5, 0x80, 9, 0x80, 13, 0x80,
            0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
        Vector128<byte> shufR128 = Vector128.Create(
            (byte)2, 0x80, 6, 0x80, 10, 0x80, 14, 0x80,
            0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);

        Vector256<byte> shufB = Vector256.Create(shufB128, shufB128);
        Vector256<byte> shufG = Vector256.Create(shufG128, shufG128);
        Vector256<byte> shufR = Vector256.Create(shufR128, shufR128);

        // Unsigned ushort constants for Y: max 66*255 + 129*255 + 25*255 + 128 = 56,228 <= 65,535
        Vector256<ushort> cY_R = Vector256.Create((ushort)66);
        Vector256<ushort> cY_G = Vector256.Create((ushort)129);
        Vector256<ushort> cY_B = Vector256.Create((ushort)25);
        Vector256<ushort> c128u = Vector256.Create((ushort)128);
        Vector256<ushort> c16u = Vector256.Create((ushort)16);

        // Reorders PackUnsignedSaturate(y0, y1) dwords into [y0[0..15] | y1[0..15]]
        Vector256<uint> yPermIdx = Vector256.Create(0u, 4u, 1u, 5u, 2u, 6u, 3u, 7u);

        // Signed short constants for U (lower 128-bit lane) and V (upper 128-bit lane): range [-28,432, +28,688]
        Vector256<short> ones16 = Vector256.Create((short)1);
        Vector256<short> cUV_R = Vector256.Create(Vector128.Create((short)-38), Vector128.Create((short)112));
        Vector256<short> cUV_G = Vector256.Create(Vector128.Create((short)-74), Vector128.Create((short)-94));
        Vector256<short> cUV_B = Vector256.Create(Vector128.Create((short)112), Vector128.Create((short)-18));
        Vector256<short> c128s = Vector256.Create((short)128);

        // Un-permutes and interleaves [U0,U1,U4,U5,U2,U3,U6,U7, V0,V1,V4,V5,V2,V3,V6,V7] -> [U0,V0..U7,V7]
        Vector128<byte> uvInterleaveMask = Vector128.Create(
            (byte)0, 8, 1, 9, 4, 12, 5, 13,
            2, 10, 3, 11, 6, 14, 7, 15);

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

            // AVX2: 16 pixels per row (32 pixels total, 8 UV pairs) per iteration
            for (; x <= width - 16; x += 16)
            {
                int byteOffset = x * 4;
                Vector256<byte> r0a = Avx.LoadVector256(row0 + byteOffset);
                Vector256<byte> r0b = Avx.LoadVector256(row0 + byteOffset + 32);
                Vector256<byte> r1a = Avx.LoadVector256(row1 + byteOffset);
                Vector256<byte> r1b = Avx.LoadVector256(row1 + byteOffset + 32);

                // De-interleave B, G, R into 16 ushort lanes: [0..3, 8..11 | 4..7, 12..15]
                Vector256<ushort> b0 = Avx2.UnpackLow(
                    Avx2.Shuffle(r0a, shufB).AsUInt64(),
                    Avx2.Shuffle(r0b, shufB).AsUInt64()).AsUInt16();
                Vector256<ushort> g0 = Avx2.UnpackLow(
                    Avx2.Shuffle(r0a, shufG).AsUInt64(),
                    Avx2.Shuffle(r0b, shufG).AsUInt64()).AsUInt16();
                Vector256<ushort> r0 = Avx2.UnpackLow(
                    Avx2.Shuffle(r0a, shufR).AsUInt64(),
                    Avx2.Shuffle(r0b, shufR).AsUInt64()).AsUInt16();

                Vector256<ushort> b1 = Avx2.UnpackLow(
                    Avx2.Shuffle(r1a, shufB).AsUInt64(),
                    Avx2.Shuffle(r1b, shufB).AsUInt64()).AsUInt16();
                Vector256<ushort> g1 = Avx2.UnpackLow(
                    Avx2.Shuffle(r1a, shufG).AsUInt64(),
                    Avx2.Shuffle(r1b, shufG).AsUInt64()).AsUInt16();
                Vector256<ushort> r1 = Avx2.UnpackLow(
                    Avx2.Shuffle(r1a, shufR).AsUInt64(),
                    Avx2.Shuffle(r1b, shufR).AsUInt64()).AsUInt16();

                // Luma Y = ((66*R + 129*G + 25*B + 128) >> 8) + 16
                Vector256<ushort> yRow0 = Avx2.Add(
                    Avx2.ShiftRightLogical(
                        Avx2.Add(
                            Avx2.Add(Avx2.MultiplyLow(r0, cY_R), Avx2.MultiplyLow(g0, cY_G)),
                            Avx2.Add(Avx2.MultiplyLow(b0, cY_B), c128u)),
                        8),
                    c16u);

                Vector256<ushort> yRow1 = Avx2.Add(
                    Avx2.ShiftRightLogical(
                        Avx2.Add(
                            Avx2.Add(Avx2.MultiplyLow(r1, cY_R), Avx2.MultiplyLow(g1, cY_G)),
                            Avx2.Add(Avx2.MultiplyLow(b1, cY_B), c128u)),
                        8),
                    c16u);

                Vector256<byte> yPacked = Avx2.PackUnsignedSaturate(yRow0.AsInt16(), yRow1.AsInt16());
                Vector256<uint> yOrdered = Avx2.PermuteVar8x32(yPacked.AsUInt32(), yPermIdx);
                Sse2.Store(yDst0 + x, yOrdered.GetLower().AsByte());
                Sse2.Store(yDst1 + x, yOrdered.GetUpper().AsByte());

                // Chroma 2x2 average: (p00 + p10 + p01 + p11) >> 2 for 8 pairs
                Vector256<int> rAvg32 = Avx2.ShiftRightArithmetic(
                    Avx2.MultiplyAddAdjacent(Avx2.Add(r0, r1).AsInt16(), ones16), 2);
                Vector256<int> gAvg32 = Avx2.ShiftRightArithmetic(
                    Avx2.MultiplyAddAdjacent(Avx2.Add(g0, g1).AsInt16(), ones16), 2);
                Vector256<int> bAvg32 = Avx2.ShiftRightArithmetic(
                    Avx2.MultiplyAddAdjacent(Avx2.Add(b0, b1).AsInt16(), ones16), 2);

                // 8 pair averages in order [p0, p1, p4, p5, p2, p3, p6, p7]
                Vector128<short> rAvg128 = Sse2.PackSignedSaturate(rAvg32.GetLower(), rAvg32.GetUpper());
                Vector128<short> gAvg128 = Sse2.PackSignedSaturate(gAvg32.GetLower(), gAvg32.GetUpper());
                Vector128<short> bAvg128 = Sse2.PackSignedSaturate(bAvg32.GetLower(), bAvg32.GetUpper());

                // Duplicate into both 128-bit lanes to compute U (low) and V (high) in parallel
                Vector256<short> rAvg = Vector256.Create(rAvg128, rAvg128);
                Vector256<short> gAvg = Vector256.Create(gAvg128, gAvg128);
                Vector256<short> bAvg = Vector256.Create(bAvg128, bAvg128);

                Vector256<short> uvSum = Avx2.Add(
                    Avx2.Add(Avx2.MultiplyLow(rAvg, cUV_R), Avx2.MultiplyLow(gAvg, cUV_G)),
                    Avx2.Add(Avx2.MultiplyLow(bAvg, cUV_B), c128s));
                Vector256<short> uv16 = Avx2.Add(Avx2.ShiftRightArithmetic(uvSum, 8), c128s);

                Vector128<byte> uvBytes = Sse2.PackUnsignedSaturate(uv16.GetLower(), uv16.GetUpper());
                Sse2.Store(uvDst + x, Ssse3.Shuffle(uvBytes, uvInterleaveMask));
            }

            // Handle 8..15 tail pixels with SSE2/SSSE3
            if (x <= width - 8)
            {
                Convert8PixelsSse2(row0, row1, yDst0, yDst1, uvDst, x);
                x += 8;
            }

            ConvertRowTailScalar(row0, row1, yDst0, yDst1, uvDst, x, width);
        }
    }

    private static unsafe void ConvertRowPairsSse2(byte* src, byte* dst, int width, int uvOffset, int startPair, int endPair)
    {
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

            // SSE2/SSSE3: 8 pixels per row (16 pixels total, 4 UV pairs) per iteration
            for (; x <= width - 8; x += 8)
            {
                Convert8PixelsSse2(row0, row1, yDst0, yDst1, uvDst, x);
            }

            ConvertRowTailScalar(row0, row1, yDst0, yDst1, uvDst, x, width);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Convert8PixelsSse2(
        byte* row0, byte* row1, byte* yDst0, byte* yDst1, byte* uvDst, int x)
    {
        int byteOffset = x * 4;
        Vector128<byte> r0a = Sse2.LoadVector128(row0 + byteOffset);
        Vector128<byte> r0b = Sse2.LoadVector128(row0 + byteOffset + 16);
        Vector128<byte> r1a = Sse2.LoadVector128(row1 + byteOffset);
        Vector128<byte> r1b = Sse2.LoadVector128(row1 + byteOffset + 16);

        Vector128<ushort> b0, g0, r0, b1, g1, r1;

        if (Ssse3.IsSupported)
        {
            Vector128<byte> shufB = Vector128.Create(
                (byte)0, 0x80, 4, 0x80, 8, 0x80, 12, 0x80,
                0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
            Vector128<byte> shufG = Vector128.Create(
                (byte)1, 0x80, 5, 0x80, 9, 0x80, 13, 0x80,
                0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
            Vector128<byte> shufR = Vector128.Create(
                (byte)2, 0x80, 6, 0x80, 10, 0x80, 14, 0x80,
                0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);

            b0 = Sse2.UnpackLow(Ssse3.Shuffle(r0a, shufB).AsUInt64(), Ssse3.Shuffle(r0b, shufB).AsUInt64()).AsUInt16();
            g0 = Sse2.UnpackLow(Ssse3.Shuffle(r0a, shufG).AsUInt64(), Ssse3.Shuffle(r0b, shufG).AsUInt64()).AsUInt16();
            r0 = Sse2.UnpackLow(Ssse3.Shuffle(r0a, shufR).AsUInt64(), Ssse3.Shuffle(r0b, shufR).AsUInt64()).AsUInt16();

            b1 = Sse2.UnpackLow(Ssse3.Shuffle(r1a, shufB).AsUInt64(), Ssse3.Shuffle(r1b, shufB).AsUInt64()).AsUInt16();
            g1 = Sse2.UnpackLow(Ssse3.Shuffle(r1a, shufG).AsUInt64(), Ssse3.Shuffle(r1b, shufG).AsUInt64()).AsUInt16();
            r1 = Sse2.UnpackLow(Ssse3.Shuffle(r1a, shufR).AsUInt64(), Ssse3.Shuffle(r1b, shufR).AsUInt64()).AsUInt16();
        }
        else
        {
            Vector128<uint> maskFF = Vector128.Create(0x000000FFu);

            Vector128<uint> r0a32 = r0a.AsUInt32();
            Vector128<uint> r0b32 = r0b.AsUInt32();
            b0 = Sse2.PackSignedSaturate(
                Sse2.And(r0a32, maskFF).AsInt32(),
                Sse2.And(r0b32, maskFF).AsInt32()).AsUInt16();
            g0 = Sse2.PackSignedSaturate(
                Sse2.And(Sse2.ShiftRightLogical(r0a32, 8), maskFF).AsInt32(),
                Sse2.And(Sse2.ShiftRightLogical(r0b32, 8), maskFF).AsInt32()).AsUInt16();
            r0 = Sse2.PackSignedSaturate(
                Sse2.And(Sse2.ShiftRightLogical(r0a32, 16), maskFF).AsInt32(),
                Sse2.And(Sse2.ShiftRightLogical(r0b32, 16), maskFF).AsInt32()).AsUInt16();

            Vector128<uint> r1a32 = r1a.AsUInt32();
            Vector128<uint> r1b32 = r1b.AsUInt32();
            b1 = Sse2.PackSignedSaturate(
                Sse2.And(r1a32, maskFF).AsInt32(),
                Sse2.And(r1b32, maskFF).AsInt32()).AsUInt16();
            g1 = Sse2.PackSignedSaturate(
                Sse2.And(Sse2.ShiftRightLogical(r1a32, 8), maskFF).AsInt32(),
                Sse2.And(Sse2.ShiftRightLogical(r1b32, 8), maskFF).AsInt32()).AsUInt16();
            r1 = Sse2.PackSignedSaturate(
                Sse2.And(Sse2.ShiftRightLogical(r1a32, 16), maskFF).AsInt32(),
                Sse2.And(Sse2.ShiftRightLogical(r1b32, 16), maskFF).AsInt32()).AsUInt16();
        }

        // Luma Y = ((66*R + 129*G + 25*B + 128) >> 8) + 16 using unsigned ushort (max 56,228 <= 65,535)
        Vector128<ushort> cY_R = Vector128.Create((ushort)66);
        Vector128<ushort> cY_G = Vector128.Create((ushort)129);
        Vector128<ushort> cY_B = Vector128.Create((ushort)25);
        Vector128<ushort> c128u = Vector128.Create((ushort)128);
        Vector128<ushort> c16u = Vector128.Create((ushort)16);

        Vector128<ushort> yRow0 = Sse2.Add(
            Sse2.ShiftRightLogical(
                Sse2.Add(
                    Sse2.Add(Sse2.MultiplyLow(r0, cY_R), Sse2.MultiplyLow(g0, cY_G)),
                    Sse2.Add(Sse2.MultiplyLow(b0, cY_B), c128u)),
                8),
            c16u);

        Vector128<ushort> yRow1 = Sse2.Add(
            Sse2.ShiftRightLogical(
                Sse2.Add(
                    Sse2.Add(Sse2.MultiplyLow(r1, cY_R), Sse2.MultiplyLow(g1, cY_G)),
                    Sse2.Add(Sse2.MultiplyLow(b1, cY_B), c128u)),
                8),
            c16u);

        Vector128<byte> yPacked = Sse2.PackUnsignedSaturate(yRow0.AsInt16(), yRow1.AsInt16());
        Sse2.StoreScalar((ulong*)(yDst0 + x), yPacked.AsUInt64());
        Sse2.StoreScalar((ulong*)(yDst1 + x), Sse2.UnpackHigh(yPacked.AsUInt64(), yPacked.AsUInt64()));

        // Chroma 2x2 average: (p00 + p10 + p01 + p11) >> 2 for 4 pairs
        Vector128<short> ones16 = Vector128.Create((short)1);
        Vector128<int> rAvg32 = Sse2.ShiftRightArithmetic(Sse2.MultiplyAddAdjacent(Sse2.Add(r0, r1).AsInt16(), ones16), 2);
        Vector128<int> gAvg32 = Sse2.ShiftRightArithmetic(Sse2.MultiplyAddAdjacent(Sse2.Add(g0, g1).AsInt16(), ones16), 2);
        Vector128<int> bAvg32 = Sse2.ShiftRightArithmetic(Sse2.MultiplyAddAdjacent(Sse2.Add(b0, b1).AsInt16(), ones16), 2);

        // Lower 4 shorts compute U, upper 4 shorts compute V in parallel
        Vector128<short> rAvg = Sse2.PackSignedSaturate(rAvg32, rAvg32);
        Vector128<short> gAvg = Sse2.PackSignedSaturate(gAvg32, gAvg32);
        Vector128<short> bAvg = Sse2.PackSignedSaturate(bAvg32, bAvg32);

        // Signed short math: range [-28,432, +28,688] — zero overflow
        Vector128<short> cUV_R = Vector128.Create((short)-38, -38, -38, -38, 112, 112, 112, 112);
        Vector128<short> cUV_G = Vector128.Create((short)-74, -74, -74, -74, -94, -94, -94, -94);
        Vector128<short> cUV_B = Vector128.Create((short)112, 112, 112, 112, -18, -18, -18, -18);
        Vector128<short> c128s = Vector128.Create((short)128);

        Vector128<short> uvSum = Sse2.Add(
            Sse2.Add(Sse2.MultiplyLow(rAvg, cUV_R), Sse2.MultiplyLow(gAvg, cUV_G)),
            Sse2.Add(Sse2.MultiplyLow(bAvg, cUV_B), c128s));
        Vector128<short> uv16 = Sse2.Add(Sse2.ShiftRightArithmetic(uvSum, 8), c128s);

        Vector128<byte> uvPacked = Sse2.PackUnsignedSaturate(uv16, uv16);
        Vector128<byte> uvInterleaved = Sse2.UnpackLow(uvPacked, Sse2.ShiftRightLogical128BitLane(uvPacked, 4));
        Sse2.StoreScalar((ulong*)(uvDst + x), uvInterleaved.AsUInt64());
    }

    private static unsafe void ConvertRowPairsScalar(byte* src, byte* dst, int width, int uvOffset, int startPair, int endPair)
    {
        for (int yHalf = startPair; yHalf < endPair; yHalf++)
        {
            int y0 = yHalf * 2;
            int y1 = y0 + 1;

            byte* row0 = src + (y0 * width * 4);
            byte* row1 = src + (y1 * width * 4);

            byte* yDst0 = dst + (y0 * width);
            byte* yDst1 = dst + (y1 * width);
            byte* uvDst = dst + uvOffset + (yHalf * width);

            ConvertRowTailScalar(row0, row1, yDst0, yDst1, uvDst, 0, width);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void ConvertRowTailScalar(
        byte* row0, byte* row1, byte* yDst0, byte* yDst1, byte* uvDst, int x, int width)
    {
        // Fast 4-pixel (2x2 block x 2) unrolled loop for scalar fallback and tail columns
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
}
