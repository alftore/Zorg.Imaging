using System;
using Xunit;
using Zorg.Imaging.Codecs.Jpeg;

namespace Zorg.Imaging.Tests.Codecs.Jpeg;

/// <summary>
/// Each SIMD routine against its scalar definition on random input (D13): identical output, whatever the hardware.
/// The gate also runs every test with AVX-512, AVX2 and all SIMD switched off.
/// </summary>
public class JpegSimdTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(333)]
    public void Ycc_to_rgb_matches_the_scalar_formula(int width)
    {
        var random = new Random(width);
        byte[] y = new byte[width];
        byte[] cb = new byte[width];
        byte[] cr = new byte[width];
        random.NextBytes(y);
        random.NextBytes(cb);
        random.NextBytes(cr);

        // Extremes too: every corner of the colour cube clamps somewhere.
        if (width >= 16)
        {
            y[0] = 0;
            cb[0] = 255;
            cr[0] = 255;
            y[1] = 255;
            cb[1] = 0;
            cr[1] = 0;
        }

        byte[] output = new byte[3 * width];
        int done = JpegColour.YccToRgb24(y, cb, cr, output, width);
        Assert.InRange(done, 0, width);
        for (int x = 0; x < done; x++)
        {
            int c1 = cb[x] - 128;
            int c2 = cr[x] - 128;
            Assert.Equal(Clamp(y[x] + (((91881 * c2) + 32768) >> 16)), output[3 * x]);
            Assert.Equal(Clamp(y[x] + (((-22554 * c1) - (46802 * c2) + 32768) >> 16)), output[(3 * x) + 1]);
            Assert.Equal(Clamp(y[x] + (((116130 * c1) + 32768) >> 16)), output[(3 * x) + 2]);
        }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(9, true)]
    [InlineData(10, true)]
    [InlineData(19, false)]
    [InlineData(255, true)]
    [InlineData(256, true)]
    public void Upsampling_matches_the_triangle_filter(int outputWidth, bool vertical)
    {
        int columns = (outputWidth + 1) / 2;
        var random = new Random(outputWidth);
        byte[] near = new byte[columns];
        byte[] far = new byte[columns];
        random.NextBytes(near);
        random.NextBytes(far);

        short[] sums = new short[columns];
        JpegColour.ColumnSums(near, far, vertical, sums);
        byte[] output = new byte[outputWidth];
        (int evenBias, int oddBias) = vertical ? (8, 7) : (4, 8);
        JpegColour.UpsampleTwice(sums, output, evenBias, oddBias);

        for (int i = 0; i < columns; i++)
        {
            Assert.Equal(vertical ? (3 * near[i]) + far[i] : 4 * near[i], sums[i]);
        }

        for (int x = 0; x < outputWidth; x++)
        {
            int i = x / 2;
            int neighbour = Math.Clamp(i + ((x & 1) == 0 ? -1 : 1), 0, columns - 1);
            Assert.Equal((byte)(((3 * sums[i]) + sums[neighbour] + ((x & 1) == 0 ? evenBias : oddBias)) >> 4), output[x]);
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(47)]
    [InlineData(512)]
    public void Rgb_to_centred_ycc_matches_the_scalar_formula(int width)
    {
        var random = new Random(width);
        byte[] rgb = new byte[3 * width];
        random.NextBytes(rgb);
        float[] y = new float[width];
        float[] cb = new float[width];
        float[] cr = new float[width];
        int done = JpegColour.Rgb24ToYcc(rgb, y, cb, cr, width);
        Assert.InRange(done, 0, width);
        for (int x = 0; x < done; x++)
        {
            float r = rgb[3 * x];
            float g = rgb[(3 * x) + 1];
            float b = rgb[(3 * x) + 2];
            Assert.Equal((0.299f * r) + (0.587f * g) + (0.114f * b) - 128, y[x], 0.001f);
            Assert.Equal((-0.168736f * r) - (0.331264f * g) + (0.5f * b), cb[x], 0.001f);
            Assert.Equal((0.5f * r) - (0.418688f * g) - (0.081312f * b), cr[x], 0.001f);
        }
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
