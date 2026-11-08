using System;
using Xunit;
using Zorg.Imaging.Codecs.Webp;

namespace Zorg.Imaging.Tests.Codecs.Webp;

public class Vp8lEncoderTests
{
    [Fact]
    public void Vector_residuals_equal_the_scalar_predictor_for_every_mode()
    {
        // Random pixels, plus runs of extremes so the clamps, the half-step rounding and Select's ties are all reached.
        const int Width = 37;
        const int Height = 9;
        var random = new Random(17);
        uint[] pixels = new uint[Width * Height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (i % 7) switch
            {
                0 => 0xFFFFFFFFu,
                1 => 0x00000000u,
                2 => 0x80808080u,
                _ => (uint)random.Next() ^ ((uint)random.Next() << 16),
            };
        }

        uint[] vector = new uint[Width - 2];
        for (int mode = 0; mode < 14; mode++)
        {
            for (int y = 1; y < Height; y++)
            {
                Vp8lEncoder.Residuals(mode, pixels, Width, (y * Width) + 1, vector);
                for (int x = 1; x < Width - 1; x++)
                {
                    int i = (y * Width) + x;
                    uint prediction = Vp8lDecoder.Transform.Predict(mode, pixels[i - 1], pixels[i - Width], pixels[i - Width + 1], pixels[i - Width - 1]);
                    uint expected = 0;
                    for (int shift = 0; shift < 32; shift += 8)
                    {
                        expected |= (uint)(byte)((pixels[i] >> shift) - (prediction >> shift)) << shift;
                    }

                    Assert.True(expected == vector[x - 1], $"mode {mode} at ({x}, {y}): {vector[x - 1]:X8}, expected {expected:X8}");
                }
            }
        }
    }
}
