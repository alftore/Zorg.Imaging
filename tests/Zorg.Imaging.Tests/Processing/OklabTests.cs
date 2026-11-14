using System.Runtime.Intrinsics;
using Xunit;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Tests.Processing;

/// <summary>The Oklab lanes the palette quantiser uses for opaque photos.</summary>
public class OklabTests
{
    [Theory]
    [InlineData(0xFFFFFFFFu, 1.0, 0.0, 0.0)]
    [InlineData(0xFF000000u, 0.0, 0.0, 0.0)]
    [InlineData(0xFFFF0000u, 0.62796, 0.22486, 0.12585)]
    [InlineData(0xFF00FF00u, 0.86644, -0.23389, 0.17950)]
    [InlineData(0xFF0000FFu, 0.45201, -0.03246, -0.31153)]
    public void Matches_published_values(uint argb, double l, double a, double b)
    {
        // Reference values from Björn Ottosson's description of Oklab, for sRGB primaries, white and black.
        Vector128<float> lanes = Oklab.FromArgb(argb);
        Assert.Equal(l * 255, lanes.GetElement(0), 0.05);
        Assert.Equal((a * 255) + 128, lanes.GetElement(1), 0.05);
        Assert.Equal((b * 255) + 128, lanes.GetElement(2), 0.05);
        Assert.Equal(0, lanes.GetElement(3));
    }

    [Fact]
    public void Every_colour_comes_back_exactly()
    {
        // Every 3rd level of each channel, about 610,000 colours: lanes to sRGB and back are lossless at 8 bits.
        for (uint r = 0; r < 256; r += 3)
        {
            for (uint g = 0; g < 256; g += 3)
            {
                for (uint b = 0; b < 256; b += 3)
                {
                    uint argb = 0xFF000000u | (r << 16) | (g << 8) | b;
                    Vector128<float> lanes = Oklab.FromArgb(argb);
                    Assert.Equal(argb, Oklab.ToArgb(lanes.GetElement(0), lanes.GetElement(1), lanes.GetElement(2)));
                }
            }
        }
    }

    [Fact]
    public void Lanes_fit_the_quantisers_range()
    {
        for (uint r = 0; r < 256; r += 15)
        {
            for (uint g = 0; g < 256; g += 15)
            {
                for (uint b = 0; b < 256; b += 15)
                {
                    Vector128<float> lanes = Oklab.FromArgb(0xFF000000u | (r << 16) | (g << 8) | b);
                    for (int lane = 0; lane < 3; lane++)
                    {
                        Assert.InRange(lanes.GetElement(lane), 0f, 255f);
                    }
                }
            }
        }
    }

    [Fact]
    public void Carried_values_between_levels_lie_between_their_neighbours()
    {
        // Error diffusion hands over fractional levels; their lightness falls between the levels either side.
        Vector128<float> low = Oklab.FromArgb(0xFF404040u);
        Vector128<float> high = Oklab.FromArgb(0xFF414141u);
        Vector128<float> mid = Oklab.FromRgb(Vector128.Create(255f, 64.5f, 64.5f, 64.5f));
        Assert.InRange(mid.GetElement(0), low.GetElement(0), high.GetElement(0));
    }
}
