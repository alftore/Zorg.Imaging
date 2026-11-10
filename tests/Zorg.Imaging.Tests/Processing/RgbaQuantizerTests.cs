using System;
using System.Runtime.Intrinsics;
using Xunit;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Tests.Processing;

public class RgbaQuantizerTests
{
    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(2, 2, false)]
    [InlineData(16, 3, false)]
    [InlineData(255, 4, false)]
    [InlineData(256, 5, false)]
    [InlineData(64, 6, true)]
    [InlineData(256, 7, true)]
    public void The_cached_search_finds_what_a_full_scan_finds(int colours, int seed, bool overPage)
    {
        var random = new Random(seed);
        var palette = new Vector128<float>[colours];
        for (int i = 0; i < colours; i++)
        {
            // Some repeated colours, so ties are tested; translucent ones when compared over a page.
            uint alpha = overPage ? (uint)random.Next(1, 256) : 255u;
            uint colour = (alpha << 24) | ((uint)random.Next(256) << 16) | ((uint)(random.Next(16) * 16) << 8) | (uint)random.Next(256);
            palette[i] = i > 0 && random.Next(8) == 0 ? palette[random.Next(i)] : RgbaQuantizer.Premultiply(colour);
        }

        var nearest = new RgbaQuantizer.NearestColour(palette, overPage);
        for (int round = 0; round < 20_000; round++)
        {
            uint alpha = overPage ? (uint)random.Next(1, 256) : 255u;
            Vector128<float> p = RgbaQuantizer.Premultiply((alpha << 24) | ((uint)random.Next(256) << 16) | ((uint)random.Next(256) << 8) | (uint)random.Next(256));
            int best = 0;
            float least = float.MaxValue;
            for (int i = 0; i < colours; i++)
            {
                float d = Distance(palette[i], p, overPage);
                if (d < least)
                {
                    (least, best) = (d, i);
                }
            }

            Assert.Equal(best, nearest.Find(p));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Without_dithering_every_pixel_takes_its_nearest_colour(bool translucent)
    {
        // Runs of one colour, flat areas and noise, with more colours than the palette holds.
        const int Width = 97, Height = 61;
        var random = new Random(11);
        uint[] argb = new uint[Width * Height];
        for (int i = 0; i < argb.Length; i++)
        {
            uint alpha = translucent ? (uint)random.Next(0, 256) : 255u;
            argb[i] = i % 7 == 0 && i > 0 ? argb[i - 1]
                : i < Width * 20 ? 0xFF3366CCu
                : (alpha << 24) | ((uint)random.Next(256) << 16) | ((uint)random.Next(256) << 8) | (uint)random.Next(256);
        }

        RgbaQuantizer.Result result = RgbaQuantizer.Run(argb, Width, Height, 64, dither: 0);

        var palette = new Vector128<float>[result.Palette.Length];
        for (int k = 0; k < palette.Length; k++)
        {
            palette[k] = RgbaQuantizer.Premultiply(result.Palette[k]);
        }

        bool overPage = Array.Exists(argb, p => p < 0xFF000000u);
        for (int i = 0; i < argb.Length; i++)
        {
            if (argb[i] < 0x01000000u)
            {
                Assert.Equal(0u, result.Palette[result.Indices[i]]);
                continue;
            }

            Vector128<float> p = RgbaQuantizer.Premultiply(argb[i]);
            int best = 0;
            float least = float.MaxValue;
            for (int k = 0; k < palette.Length; k++)
            {
                float d = result.Palette[k] == 0 ? float.MaxValue : Distance(palette[k], p, overPage);
                if (d < least)
                {
                    (least, best) = (d, k);
                }
            }

            Assert.Equal(best, result.Indices[i]);
        }
    }

    [Fact]
    public void A_large_flat_area_keeps_its_exact_colour()
    {
        // A flat blue over most of the picture, counted in long runs, and reddish noise far from it with more colours than
        // the palette holds. Dithering carries no error through the flat area, away from the noise.
        const int Width = 120, Height = 80;
        var random = new Random(5);
        uint[] argb = new uint[Width * Height];
        for (int i = 0; i < argb.Length; i++)
        {
            argb[i] = i % Width < 90 ? 0xFF2A7FD4u : 0xFF000000u | ((uint)random.Next(128, 256) << 16) | ((uint)random.Next(64) << 8) | (uint)random.Next(64);
        }

        RgbaQuantizer.Result result = RgbaQuantizer.Run(argb, Width, Height, 32, dither: 1);

        for (int i = 0; i < argb.Length; i++)
        {
            if (i % Width < 80)
            {
                Assert.Equal(0xFF2A7FD4u, result.Palette[result.Indices[i]]);
            }
        }
    }

    // As the quantiser measures, summed in the same order: plain, or over black plus over white.
    private static float Distance(Vector128<float> a, Vector128<float> b, bool overPage)
    {
        Vector128<float> d = a - b;
        float da = d.GetElement(0), dr = d.GetElement(1), dg = d.GetElement(2), db = d.GetElement(3);
        if (!overPage)
        {
            return (da * da) + (dr * dr) + ((dg * dg) + (db * db));
        }

        float wr = dr - da, wg = dg - da, wb = db - da;
        return (dr * dr) + (dg * dg) + (db * db) + ((wr * wr) + (wg * wg) + (wb * wb));
    }
}
