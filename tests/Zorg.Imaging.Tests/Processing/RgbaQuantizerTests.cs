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

    // As the quantiser measures: plain, or over black plus over white.
    private static float Distance(Vector128<float> a, Vector128<float> b, bool overPage)
    {
        Vector128<float> d = a - b;
        if (!overPage)
        {
            return Vector128.Sum(d * d);
        }

        Vector128<float> colour = d.WithElement(0, 0f);
        Vector128<float> onWhite = colour - Vector128.Create(0f, d.GetElement(0), d.GetElement(0), d.GetElement(0));
        return Vector128.Sum(colour * colour) + Vector128.Sum(onWhite * onWhite);
    }
}
