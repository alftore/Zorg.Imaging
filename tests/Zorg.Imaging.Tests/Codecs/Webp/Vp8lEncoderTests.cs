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

    // Runs of one colour of every length from 1 to 40, some at the very start and end, with and without other colours
    // among them: the hash chains skip through runs, and every copy that skipping finds must still be exact.
    [Theory]
    [InlineData(5, false)]
    [InlineData(23, true)]
    public void Runs_of_one_colour_round_trip_at_every_effort(int seed, bool noisy)
    {
        var random = new Random(seed);
        int width = 50 + random.Next(80);
        int height = 20 + random.Next(30);
        Rgba32[] colours = [new(0, 0, 0, 0), new(255, 255, 255, 255), new(200, 40, 40, 255), new(200, 40, 40, 128)];
        using var image = new Image(width, height, PixelFormat.Rgba32);
        int left = 0;
        Rgba32 colour = colours[0];
        for (int y = 0; y < height; y++)
        {
            Span<Rgba32> row = image.GetRow<Rgba32>(y);
            for (int x = 0; x < width; x++)
            {
                if (left == 0)
                {
                    left = 1 + random.Next(40);
                    colour = noisy && random.Next(3) == 0
                        ? new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255)
                        : colours[random.Next(colours.Length)];
                }

                row[x] = colour;
                left--;
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

    // Fifteen runs of ten black pixels, each ended by a different pixel; the first is followed by three pixels that
    // follow the last run too. Walked a pixel at a time, 64 steps reach only nine runs back; with each run a few steps,
    // the walk reaches the first and its copy of the whole run and the three after.
    [Fact]
    public void The_match_finder_reaches_past_runs_of_one_colour()
    {
        const uint Black = 0xFF000000u;
        var pixels = new System.Collections.Generic.List<uint>();
        void Run(params uint[] after)
        {
            for (int k = 0; k < 10; k++)
            {
                pixels.Add(Black);
            }

            pixels.AddRange(after);
        }

        uint[] tail = [0xFF123456u, 0xFF654321u, 0xFFABCDEFu];
        Run(tail);
        for (uint k = 1; k <= 14; k++)
        {
            Run(0xFF000100u + k);
        }

        int query = pixels.Count;
        Run(tail);
        pixels.AddRange([0xFF777777u, 0xFF888888u, 0xFF999999u, 0xFFAAAAAAu]);

        var finder = new Vp8lEncoder.MatchFinder([.. pixels], pixels.Count, effort: 4);
        (int length, int distance) = finder.Find(query);
        Assert.Equal((13, query), (length, distance));
    }
}
