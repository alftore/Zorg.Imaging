using System;
using Xunit;
using Zorg.Imaging.Codecs.Webp;

namespace Zorg.Imaging.Tests.Codecs.Webp;

/// <summary>The vector inverse transform and chroma upsampler of the lossy decoder against their scalar references.</summary>
public class Vp8DecoderSimdTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Vector_and_scalar_residuals_give_the_same_pixels(int seed)
    {
        var random = new Random(seed);
        for (int round = 0; round < 2000; round++)
        {
            var scalar = new Vp8Decoder.Plane(8, 8, extra: 0);
            var vector = new Vp8Decoder.Plane(8, 8, extra: 0);
            for (int r = 0; r < 8; r++)
            {
                random.NextBytes(scalar.Row(r)[..8]);
                scalar.Row(r)[..8].CopyTo(vector.Row(r));
            }

            // Small values as in real streams, the whole 16-bit range (where the 32-bit steps wrap), and DC alone.
            short[] coefficients = new short[16];
            int range = (round % 3) switch { 0 => 300, 1 => 32768, _ => 2048 };
            int count = round % 4 == 0 ? 1 : 16;
            for (int i = 0; i < count; i++)
            {
                coefficients[i] = (short)random.Next(-range, range);
            }

            int x = random.Next(2) * 4;
            int y = random.Next(2) * 4;
            Vp8Decoder.AddResidual(scalar, x, y, coefficients, any: true, useVectors: false);
            Vp8Decoder.AddResidual(vector, x, y, coefficients, any: true, useVectors: true);
            Assert.Equal(scalar.Data, vector.Data);
        }
    }

    [Fact]
    public void Vector_and_scalar_upsampling_give_the_same_rows()
    {
        var random = new Random(4);
        for (int width = 1; width < 90; width++)
        {
            int chroma = (width + 1) / 2;
            byte[] top = new byte[chroma];
            byte[] current = new byte[chroma];
            random.NextBytes(top);
            random.NextBytes(current);
            byte[] scalarTop = new byte[width], scalarCurrent = new byte[width];
            byte[] vectorTop = new byte[width], vectorCurrent = new byte[width];
            WebpLossy.Upsample(top, current, scalarTop, scalarCurrent, width, useVectors: false);
            WebpLossy.Upsample(top, current, vectorTop, vectorCurrent, width, useVectors: true);
            Assert.Equal(scalarTop, vectorTop);
            Assert.Equal(scalarCurrent, vectorCurrent);
        }
    }
}
