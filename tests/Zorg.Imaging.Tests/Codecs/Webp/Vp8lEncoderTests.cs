using System;
using Xunit;
using Zorg.Imaging.Codecs.Webp;

namespace Zorg.Imaging.Tests.Codecs.Webp;

public class Vp8lEncoderTests
{
    // Half noisy gradient, half flat blocks: tiles whose best codes differ, with a colour cache. Moving each tile to its
    // cheapest group used to leave a group empty but still counted, so one set of codes too many was written and the
    // stream fell out of step (these seeds failed at effort 3 and 4).
    [Theory]
    [InlineData(3)]
    [InlineData(14)]
    [InlineData(49)]
    public void Mixed_content_round_trips_at_every_effort(int seed)
    {
        var random = new Random(seed);
        int width = 64 + random.Next(100);
        int height = 48 + random.Next(60);
        using var image = new Image(width, height, PixelFormat.Rgb24);
        int split = (width / 3) + random.Next(width / 3);
        for (int y = 0; y < height; y++)
        {
            Span<Rgb24> row = image.GetRow<Rgb24>(y);
            for (int x = 0; x < width; x++)
            {
                if (x < split)
                {
                    int v = ((x * 3) + (y * 2) + random.Next(24)) & 255;
                    row[x] = new Rgb24((byte)v, (byte)((v / 2) + random.Next(8)), (byte)(255 - v));
                }
                else
                {
                    row[x] = (((x / 9) + (y / 7)) % 3) switch
                    {
                        0 => new Rgb24(240, 240, 240),
                        1 => new Rgb24(30, 90, 200),
                        _ => new Rgb24((byte)random.Next(256), 20, 20),
                    };
                }
            }
        }

        for (int effort = 0; effort <= 6; effort++)
        {
            using var stream = new System.IO.MemoryStream();
            image.SaveAsWebp(stream, new WebpEncoderOptions { Lossless = true, Effort = effort });
            using Image decoded = Image.Decode(stream.ToArray());
            Assert.True(image.GetPixelBytes().SequenceEqual(decoded.GetPixelBytes()), $"effort {effort}");
        }
    }

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
