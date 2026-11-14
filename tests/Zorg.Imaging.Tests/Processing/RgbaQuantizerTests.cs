using System;
using System.Runtime.Intrinsics;
using Xunit;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Tests.Processing;

public class RgbaQuantizerTests
{
    [Theory]
    [InlineData(1, 1, false, 5)]
    [InlineData(2, 2, false, 5)]
    [InlineData(16, 3, false, 6)]
    [InlineData(255, 4, false, 5)]
    [InlineData(256, 5, false, 6)]
    [InlineData(64, 6, true, 5)]
    [InlineData(256, 7, true, 5)]
    public void The_cached_search_finds_what_a_full_scan_finds(int colours, int seed, bool overPage, int bits)
    {
        var random = new Random(seed);
        var space = new RgbaQuantizer.Space(overPage);
        var palette = new Vector256<float>[colours];
        for (int i = 0; i < colours; i++)
        {
            // Some repeated colours, so ties are tested; translucent ones when compared over a page.
            uint alpha = overPage ? (uint)random.Next(1, 256) : 255u;
            uint colour = (alpha << 24) | ((uint)random.Next(256) << 16) | ((uint)(random.Next(16) * 16) << 8) | (uint)random.Next(256);
            palette[i] = i > 0 && random.Next(8) == 0 ? palette[random.Next(i)] : space.FromArgb(colour);
        }

        var nearest = new RgbaQuantizer.NearestColour(palette, space.Lanes, bits);
        for (int round = 0; round < 20_000; round++)
        {
            // Whole colours, and fractional ones as error diffusion makes them.
            uint alpha = overPage ? (uint)random.Next(1, 256) : 255u;
            uint colour = (alpha << 24) | ((uint)random.Next(256) << 16) | ((uint)random.Next(256) << 8) | (uint)random.Next(256);
            Vector128<float> carried = RgbaQuantizer.Premultiply(colour) + Vector128.Create(0, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f);
            carried = Vector128.Min(Vector128.Max(carried, Vector128<float>.Zero), Vector128.Create(255f, alpha, alpha, alpha));
            Vector256<float> p = round % 2 == 0 ? space.FromArgb(colour) : space.FromPremultiplied(carried);
            int best = 0;
            float least = float.MaxValue;
            for (int i = 0; i < colours; i++)
            {
                float d = Distance(palette[i], p, space.Lanes);
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

        var space = new RgbaQuantizer.Space(translucent);
        var palette = new Vector256<float>[result.Palette.Length];
        for (int k = 0; k < palette.Length; k++)
        {
            palette[k] = space.FromArgb(result.Palette[k]);
        }

        for (int i = 0; i < argb.Length; i++)
        {
            if (argb[i] < 0x01000000u)
            {
                Assert.Equal(0u, result.Palette[result.Indices[i]]);
                continue;
            }

            Vector256<float> p = space.FromArgb(argb[i]);
            int best = 0;
            float least = float.MaxValue;
            for (int k = 0; k < palette.Length; k++)
            {
                float d = result.Palette[k] == 0 ? float.MaxValue : Distance(palette[k], p, space.Lanes);
                if (d < least)
                {
                    (least, best) = (d, k);
                }
            }

            Assert.Equal(best, result.Indices[i]);
        }
    }

    [Fact]
    public void Translucent_colours_come_back_from_their_lanes()
    {
        // Alpha from the difference over black and over white, colour from both; premultiplied within two levels.
        var space = new RgbaQuantizer.Space(overPage: true);
        var random = new Random(3);
        for (int round = 0; round < 5_000; round++)
        {
            uint colour = ((uint)random.Next(1, 256) << 24) | ((uint)random.Next(256) << 16) | ((uint)random.Next(256) << 8) | (uint)random.Next(256);
            uint back = space.ToArgb(space.FromArgb(colour), opaque: false);
            Vector128<float> difference = Vector128.Abs(RgbaQuantizer.Premultiply(back) - RgbaQuantizer.Premultiply(colour));
            Assert.True(Vector128.LessThanOrEqualAll(difference, Vector128.Create(2.01f)), $"{colour:X8} came back as {back:X8}");
        }
    }

    [Fact]
    public void A_picture_opaque_but_for_a_transparent_background_keeps_opaque_colours()
    {
        // A sprite: noise inside, fully transparent around it.
        const int Width = 64, Height = 64;
        var random = new Random(9);
        uint[] argb = new uint[Width * Height];
        for (int i = 0; i < argb.Length; i++)
        {
            int x = i % Width, y = i / Width;
            argb[i] = x is > 8 and < 56 && y is > 8 and < 56 ? 0xFF000000u | (uint)random.Next(1 << 24) : 0u;
        }

        RgbaQuantizer.Result result = RgbaQuantizer.Run(argb, Width, Height, 32, dither: 1);

        Assert.Equal(32, result.Palette.Length);
        for (int i = 0; i < argb.Length; i++)
        {
            uint colour = result.Palette[result.Indices[i]];
            Assert.Equal(argb[i] == 0 ? 0u : 0xFFu, colour >> 24);
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

    // As the quantiser measures, summed lane by lane in the same order.
    private static float Distance(Vector256<float> a, Vector256<float> b, int lanes)
    {
        float sum = 0;
        for (int lane = 0; lane < lanes; lane++)
        {
            float d = a.GetElement(lane) - b.GetElement(lane);
            sum += d * d;
        }

        return sum;
    }
}
