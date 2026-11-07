using System;
using Xunit;
using Zorg.Imaging.Codecs.Png;

namespace Zorg.Imaging.Tests.Codecs.Png;

/// <summary>The vector PNG filters and score against the scalar reference on random rows (D13).</summary>
public class PngFilterSimdTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void Vector_filters_match_the_scalar_filters(int bytesPerPixel)
    {
        var random = new Random(bytesPerPixel);
        foreach (int length in new[] { bytesPerPixel, bytesPerPixel + 7, 15, 16, 17, 33, 100, 1001 })
        {
            if (length < bytesPerPixel)
            {
                continue;
            }

            byte[] raw = new byte[length];
            byte[] previous = new byte[length];
            random.NextBytes(raw);
            random.NextBytes(previous);
            for (int filter = 0; filter <= 4; filter++)
            {
                byte[] expected = new byte[length];
                byte[] actual = new byte[length];
                PngFilters.Filter(filter, raw, previous, bytesPerPixel, expected);
                long score = PngFilters.FilterAndScore(filter, raw, previous, bytesPerPixel, actual);
                Assert.Equal(expected, actual);

                long expectedScore = 0;
                foreach (byte b in expected)
                {
                    expectedScore += Math.Abs((int)(sbyte)b);
                }

                Assert.Equal(expectedScore, score);
            }
        }
    }
}
