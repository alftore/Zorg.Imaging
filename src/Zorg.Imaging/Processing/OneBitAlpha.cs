using System;

namespace Zorg.Imaging.Processing;

/// <summary>
/// Makes every pixel fully transparent or fully opaque, as GIF holds alpha: by a threshold, or with the alpha dithered so
/// a soft edge or shadow keeps its look as a pattern of opaque pixels. Kept pixels can be blended over a matte colour.
/// </summary>
/// <remarks>
/// The dithering is Floyd–Steinberg error diffusion on the alpha alone, alternate rows in opposite directions. Only
/// partly transparent pixels take part: fully transparent and fully opaque ones stay as they are and drop any error
/// that reaches them, so no stray pixels appear in a clear background or holes in a solid shape.
/// </remarks>
internal static class OneBitAlpha
{
    /// <summary>Resolves the alpha of <paramref name="argb"/> in place: 0 (with colour 0) or 255.</summary>
    public static void Apply(uint[] argb, int width, int height, GifEncoderOptions options)
    {
        if (!AnyPartial(argb))
        {
            return;
        }

        int threshold = options.AlphaThreshold;
        int scale = (int)Math.Round(options.AlphaDithering * 256);
        Rgba32? matte = options.Matte;

        // Each row's error in 1/4096ths of an alpha step, with a pixel of room at each end so the edges need no tests.
        int[] currentRow = new int[width + 2];
        int[] nextRow = new int[width + 2];
        for (int y = 0; y < height; y++)
        {
            bool forward = (y & 1) == 0;
            int step = forward ? 1 : -1;
            for (int n = 0; n < width; n++)
            {
                int x = forward ? n : width - 1 - n;
                int i = (y * width) + x;
                uint p = argb[i];
                int alpha = (int)(p >> 24);
                if (alpha == 255)
                {
                    continue;
                }

                if (alpha == 0)
                {
                    argb[i] = 0;
                    continue;
                }

                int at = x + 1;
                int wanted = alpha + ((currentRow[at] + 2048) >> 12);
                bool keep = wanted >= threshold;
                argb[i] = keep ? 0xFF000000u | Blend(p, alpha, matte) : 0u;
                if (scale == 0)
                {
                    continue;
                }

                // 7/16 ahead; on the next row 3/16 behind, 5/16 below and 1/16 ahead.
                int error = (wanted - (keep ? 255 : 0)) * scale;
                currentRow[at + step] += error * 7;
                nextRow[at - step] += error * 3;
                nextRow[at] += error * 5;
                nextRow[at + step] += error;
            }

            (currentRow, nextRow) = (nextRow, currentRow);
            Array.Clear(nextRow);
        }
    }

    private static bool AnyPartial(uint[] argb)
    {
        foreach (uint p in argb)
        {
            uint alpha = p >> 24;
            if (alpha != 0 && alpha != 255)
            {
                return true;
            }
        }

        return false;
    }

    // The pixel's colour, or with a matte its colour over the matte by its alpha, rounded.
    private static uint Blend(uint p, int alpha, Rgba32? matte)
    {
        if (matte is not { } m)
        {
            return p & 0xFFFFFFu;
        }

        int rest = 255 - alpha;
        uint r = (uint)(((int)((p >> 16) & 0xFF) * alpha) + (m.R * rest) + 127) / 255;
        uint g = (uint)(((int)((p >> 8) & 0xFF) * alpha) + (m.G * rest) + 127) / 255;
        uint b = (uint)(((int)(p & 0xFF) * alpha) + (m.B * rest) + 127) / 255;
        return (r << 16) | (g << 8) | b;
    }
}
