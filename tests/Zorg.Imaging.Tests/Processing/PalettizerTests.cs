using System;
using Xunit;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Tests.Processing;

public class PalettizerTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(16, 3)]
    [InlineData(255, 4)]
    [InlineData(256, 5)]
    public void The_pruned_search_finds_what_a_full_scan_finds(int colours, int seed)
    {
        var random = new Random(seed);
        uint[] palette = new uint[colours];
        for (int i = 0; i < colours; i++)
        {
            // Some repeated greens and whole repeated colours, so ties are tested.
            palette[i] = 0xFF000000u | ((uint)random.Next(256) << 16) | ((uint)(random.Next(16) * 16) << 8) | (uint)random.Next(256);
            if (i > 0 && random.Next(8) == 0)
            {
                palette[i] = palette[random.Next(i)];
            }
        }

        var nearest = new Palettizer.Nearest(palette);
        for (int round = 0; round < 20_000; round++)
        {
            int r = random.Next(256), g = random.Next(256), b = random.Next(256);
            int best = 0;
            int bestDistance = int.MaxValue;
            for (int i = 0; i < colours; i++)
            {
                int dr = r - (int)((palette[i] >> 16) & 0xFF), dg = g - (int)((palette[i] >> 8) & 0xFF), db = b - (int)(palette[i] & 0xFF);
                int distance = (dr * dr) + (dg * dg) + (db * db);
                if (distance < bestDistance)
                {
                    (bestDistance, best) = (distance, i);
                }
            }

            Assert.Equal(best, nearest.Find(r, g, b));
        }
    }
}
