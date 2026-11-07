using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Codecs.Webp;

/// <summary>A lossy WebP image as ARGB: the VP8 frame converted from YUV, and the alpha from an ALPH chunk.</summary>
/// <remarks>
/// <para>
/// VP8 holds BT.601 YUV at studio range with chroma at half size. Chroma is brought to full size with a 9-3-3-1 triangle
/// filter, each output pixel weighted towards its nearest chroma sample, and converted with libwebp's fixed-point
/// coefficients, so pixels come out as libwebp's default decode gives them.
/// </para>
/// <para>
/// The ALPH chunk (RFC 9649, section 2.7.1.2) holds alpha raw or as a VP8L stream's green channel, optionally filtered
/// horizontally, vertically or by gradient.
/// </para>
/// </remarks>
internal static class WebpLossy
{
    public static uint[] Decode(byte[] vp8, byte[]? alpha, int width, int height)
    {
        Vp8Decoder frame = Vp8Decoder.Decode(vp8);
        if (frame.Width != width || frame.Height != height)
        {
            throw ImageErrors.Corrupt("The WebP lossy image's size doesn't match the canvas or frame.");
        }

        uint[] argb = new uint[(long)width * height];
        ToRgb(frame, argb);
        if (alpha is not null)
        {
            byte[] values = DecodeAlpha(alpha, width, height);
            for (int i = 0; i < argb.Length; i++)
            {
                argb[i] = (argb[i] & 0x00FFFFFFu) | ((uint)values[i] << 24);
            }
        }

        return argb;
    }

    private static void ToRgb(Vp8Decoder frame, uint[] argb)
    {
        int width = frame.Width;
        int height = frame.Height;

        // Row 0 sits on chroma row 0 alone; then each pair of rows (2k - 1, 2k) between chroma rows k - 1 and k; an
        // even height's last row on the last chroma row alone (libwebp's EmitFancyRGB).
        Pair(frame, 0, -1, 0, 0, argb, width);
        for (int k = 1; 2 * k <= height - 1; k++)
        {
            Pair(frame, (2 * k) - 1, 2 * k, k - 1, k, argb, width);
        }

        if ((height & 1) == 0)
        {
            int last = (height / 2) - 1;
            Pair(frame, height - 1, -1, last, last, argb, width);
        }
    }

    // Two output rows (the second may be absent), the first nearer chroma row `top`, the second nearer `current`: the
    // chroma of each row is upsampled into a full-width row first, then each row converts in one pass.
    private static void Pair(Vp8Decoder frame, int topRow, int bottomRow, int top, int current, uint[] argb, int width)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(4 * width);
        try
        {
            Span<byte> topUp = rented.AsSpan(0, width);
            Span<byte> topVp = rented.AsSpan(width, width);
            Span<byte> bottomUp = rented.AsSpan(2 * width, width);
            Span<byte> bottomVp = rented.AsSpan(3 * width, width);
            Upsample(frame.URow(top), frame.URow(current), topUp, bottomUp, width);
            Upsample(frame.VRow(top), frame.VRow(current), topVp, bottomVp, width);
            ToArgb(frame.LumaRow(topRow), topUp, topVp, argb.AsSpan(topRow * width, width));
            if (bottomRow >= 0)
            {
                ToArgb(frame.LumaRow(bottomRow), bottomUp, bottomVp, argb.AsSpan(bottomRow * width, width));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    // libwebp's fancy upsampler for one chroma plane: the row nearer `top`, and the row nearer `current`.
    private static void Upsample(ReadOnlySpan<byte> topRow, ReadOnlySpan<byte> currentRow, Span<byte> nearTop, Span<byte> nearCurrent, int width)
    {
        int tl = topRow[0];
        int l = currentRow[0];
        nearTop[0] = (byte)(((3 * tl) + l + 2) >> 2);
        nearCurrent[0] = (byte)(((3 * l) + tl + 2) >> 2);
        int pairs = (width - 1) >> 1;
        for (int x = 1; x <= pairs; x++)
        {
            int t = topRow[x];
            int c = currentRow[x];
            int average = tl + t + l + c + 8;
            int diag12 = (average + (2 * (t + l))) >> 3;
            int diag03 = (average + (2 * (tl + c))) >> 3;
            nearTop[(2 * x) - 1] = (byte)((diag12 + tl) >> 1);
            nearTop[2 * x] = (byte)((diag03 + t) >> 1);
            nearCurrent[(2 * x) - 1] = (byte)((diag03 + l) >> 1);
            nearCurrent[2 * x] = (byte)((diag12 + c) >> 1);
            tl = t;
            l = c;
        }

        if ((width & 1) == 0)
        {
            nearTop[width - 1] = (byte)(((3 * tl) + l + 2) >> 2);
            nearCurrent[width - 1] = (byte)(((3 * l) + tl + 2) >> 2);
        }
    }

    // One row of full-resolution Y, U and V to ARGB: eight pixels at a time in 32-bit lanes, then the rest.
    private static void ToArgb(ReadOnlySpan<byte> y, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, Span<uint> output)
    {
        int x = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            ref byte y0 = ref MemoryMarshal.GetReference(y);
            ref byte u0 = ref MemoryMarshal.GetReference(u);
            ref byte v0 = ref MemoryMarshal.GetReference(v);
            ref uint o0 = ref MemoryMarshal.GetReference(output);
            Vector256<int> top = Vector256.Create(255);
            Vector256<int> alpha = Vector256.Create(unchecked((int)0xFF000000u));
            for (; x + 8 <= output.Length; x += 8)
            {
                Vector256<int> luma = Vector256.ShiftRightArithmetic(Widen8(ref y0, x) * 19077, 8);
                Vector256<int> cu = Widen8(ref u0, x);
                Vector256<int> cv = Widen8(ref v0, x);
                Vector256<int> r = Clip(luma + Vector256.ShiftRightArithmetic(cv * 26149, 8) - Vector256.Create(14234), top);
                Vector256<int> g = Clip(luma - Vector256.ShiftRightArithmetic(cu * 6419, 8) - Vector256.ShiftRightArithmetic(cv * 13320, 8) + Vector256.Create(8708), top);
                Vector256<int> b = Clip(luma + Vector256.ShiftRightArithmetic(cu * 33050, 8) - Vector256.Create(17685), top);
                Vector256<int> argb = alpha | Vector256.ShiftLeft(r, 16) | Vector256.ShiftLeft(g, 8) | b;
                argb.AsUInt32().StoreUnsafe(ref o0, (nuint)x);
            }
        }

        for (; x < output.Length; x++)
        {
            output[x] = Pixel(y[x], u[x], v[x]);
        }
    }

    private static Vector256<int> Widen8(ref byte source, int x)
    {
        Vector128<byte> bytes = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, x))).AsByte();
        Vector128<ushort> shorts = Vector128.WidenLower(bytes);
        return Vector256.Create(Vector128.WidenLower(shorts), Vector128.WidenUpper(shorts)).AsInt32();
    }

    // (v & ~16383) == 0 ? v >> 6 : v < 0 ? 0 : 255 is the same as clamping v >> 6 to 0..255.
    private static Vector256<int> Clip(Vector256<int> value, Vector256<int> top) =>
        Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(value, 6), Vector256<int>.Zero), top);

    // BT.601 studio range to RGB in 14-bit fixed point: R = 1.164 (Y - 16) + 1.596 (V - 128), and so on.
    private static uint Pixel(int y, int u, int v)
    {
        int luma = (y * 19077) >> 8;
        int r = Clip(luma + ((v * 26149) >> 8) - 14234);
        int g = Clip(luma - ((u * 6419) >> 8) - ((v * 13320) >> 8) + 8708);
        int b = Clip(luma + ((u * 33050) >> 8) - 17685);
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private static int Clip(int value) => (value & ~16383) == 0 ? value >> 6 : value < 0 ? 0 : 255;

    private static byte[] DecodeAlpha(byte[] chunk, int width, int height)
    {
        if (chunk.Length < 1)
        {
            throw ImageErrors.Corrupt("The WebP alpha chunk is empty.");
        }

        int compression = chunk[0] & 3;
        int filter = (chunk[0] >> 2) & 3;
        byte[] alpha = new byte[(long)width * height];
        switch (compression)
        {
            case 0:
                if (chunk.Length - 1 < alpha.Length)
                {
                    throw ImageErrors.Truncated("The WebP alpha chunk is shorter than its image.");
                }

                chunk.AsSpan(1, alpha.Length).CopyTo(alpha);
                break;

            case 1:
                uint[] pixels = Vp8lDecoder.DecodeStream(new Vp8lBitReader(chunk.AsMemory(1)), width, height);
                for (int i = 0; i < alpha.Length; i++)
                {
                    alpha[i] = (byte)(pixels[i] >> 8);
                }

                break;

            default:
                throw ImageErrors.Corrupt("The WebP alpha chunk uses an unknown compression.");
        }

        if (filter != 0)
        {
            Unfilter(alpha, width, height, filter);
        }

        return alpha;
    }

    // The residuals plus predictions: left (1), above (2) or left + above - above-left clamped (3). The first pixel
    // predicts from 0, the rest of the first row from the left, the rest of the first column from above.
    private static void Unfilter(byte[] alpha, int width, int height, int filter)
    {
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int prediction;
                if (y == 0)
                {
                    prediction = x == 0 ? 0 : alpha[row + x - 1];
                }
                else if (x == 0)
                {
                    prediction = alpha[row - width];
                }
                else
                {
                    int left = alpha[row + x - 1];
                    int above = alpha[row - width + x];
                    prediction = filter switch
                    {
                        1 => left,
                        2 => above,
                        _ => Math.Clamp(left + above - alpha[row - width + x - 1], 0, 255),
                    };
                }

                alpha[row + x] = (byte)(alpha[row + x] + prediction);
            }
        }
    }
}
