using System;
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

    // Two output rows (the second may be absent), the first nearer chroma row `top`, the second nearer `current`.
    private static void Pair(Vp8Decoder frame, int topRow, int bottomRow, int top, int current, uint[] argb, int width)
    {
        ReadOnlySpan<byte> topU = frame.URow(top);
        ReadOnlySpan<byte> topV = frame.VRow(top);
        ReadOnlySpan<byte> curU = frame.URow(current);
        ReadOnlySpan<byte> curV = frame.VRow(current);
        ReadOnlySpan<byte> topY = frame.LumaRow(topRow);
        ReadOnlySpan<byte> bottomY = bottomRow >= 0 ? frame.LumaRow(bottomRow) : default;
        Span<uint> topOut = argb.AsSpan(topRow * width, width);
        Span<uint> bottomOut = bottomRow >= 0 ? argb.AsSpan(bottomRow * width, width) : default;

        int tlU = topU[0];
        int tlV = topV[0];
        int lU = curU[0];
        int lV = curV[0];
        topOut[0] = Pixel(topY[0], ((3 * tlU) + lU + 2) >> 2, ((3 * tlV) + lV + 2) >> 2);
        if (bottomRow >= 0)
        {
            bottomOut[0] = Pixel(bottomY[0], ((3 * lU) + tlU + 2) >> 2, ((3 * lV) + tlV + 2) >> 2);
        }

        int pairs = (width - 1) >> 1;
        for (int x = 1; x <= pairs; x++)
        {
            int tU = topU[x];
            int tV = topV[x];
            int u = curU[x];
            int v = curV[x];
            int avgU = tlU + tU + lU + u + 8;
            int avgV = tlV + tV + lV + v + 8;
            int diag12U = (avgU + (2 * (tU + lU))) >> 3;
            int diag12V = (avgV + (2 * (tV + lV))) >> 3;
            int diag03U = (avgU + (2 * (tlU + u))) >> 3;
            int diag03V = (avgV + (2 * (tlV + v))) >> 3;
            topOut[(2 * x) - 1] = Pixel(topY[(2 * x) - 1], (diag12U + tlU) >> 1, (diag12V + tlV) >> 1);
            topOut[2 * x] = Pixel(topY[2 * x], (diag03U + tU) >> 1, (diag03V + tV) >> 1);
            if (bottomRow >= 0)
            {
                bottomOut[(2 * x) - 1] = Pixel(bottomY[(2 * x) - 1], (diag03U + lU) >> 1, (diag03V + lV) >> 1);
                bottomOut[2 * x] = Pixel(bottomY[2 * x], (diag12U + u) >> 1, (diag12V + v) >> 1);
            }

            tlU = tU;
            tlV = tV;
            lU = u;
            lV = v;
        }

        if ((width & 1) == 0)
        {
            topOut[width - 1] = Pixel(topY[width - 1], ((3 * tlU) + lU + 2) >> 2, ((3 * tlV) + lV + 2) >> 2);
            if (bottomRow >= 0)
            {
                bottomOut[width - 1] = Pixel(bottomY[width - 1], ((3 * lU) + tlU + 2) >> 2, ((3 * lV) + tlV + 2) >> 2);
            }
        }
    }

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
