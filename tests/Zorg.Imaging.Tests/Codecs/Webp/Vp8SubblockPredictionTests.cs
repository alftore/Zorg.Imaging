using System;
using Xunit;
using Zorg.Imaging.Codecs.Webp;

namespace Zorg.Imaging.Tests.Codecs.Webp;

public class Vp8SubblockPredictionTests
{
    [Fact]
    public void The_ten_predictions_at_once_equal_the_decoders_one_at_a_time()
    {
        var random = new Random(7);
        int[] edge = new int[13];
        byte[] all = new byte[160];
        byte[] one = new byte[16];
        for (int trial = 0; trial < 20_000; trial++)
        {
            // Random edges, edges at the extremes and flat edges.
            int kind = trial % 4;
            for (int i = 0; i < 13; i++)
            {
                edge[i] = kind switch
                {
                    0 => random.Next(256),
                    1 => random.Next(2) * 255,
                    2 => 128,
                    _ => random.Next(250, 256),
                };
            }

            Vp8Encoder.PredictSubblocks(edge, all);
            for (int mode = 0; mode < 10; mode++)
            {
                Vp8Decoder.PredictSubblock(edge, mode, one);
                Assert.Equal(one, all.AsSpan(mode * 16, 16).ToArray());
            }
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(8)]
    public void Tm_block_prediction_is_above_plus_left_less_corner_clamped(int size)
    {
        var random = new Random(size);
        var plane = new Vp8Decoder.Plane(48, 48, extra: 4);
        for (int trial = 0; trial < 2_000; trial++)
        {
            for (int y = -1; y < 40; y++)
            {
                Span<byte> row = plane.Row(y);
                for (int x = 0; x < 40; x++)
                {
                    row[x] = (byte)(trial % 3 == 0 ? random.Next(2) * 255 : random.Next(256));
                }

                plane.Data[plane.Index(-1, y)] = (byte)random.Next(256);
            }

            int x0 = 16;
            int y0 = 16;
            byte[] expected = new byte[size * size];
            int corner = plane.At(x0 - 1, y0 - 1);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    expected[(y * size) + x] = (byte)Math.Clamp(plane.At(x0 + x, y0 - 1) + plane.At(x0 - 1, y0 + y) - corner, 0, 255);
                }
            }

            Vp8Decoder.PredictBlock(plane, x0, y0, size, Vp8Decoder.TmPred, leftEdge: false, topEdge: false);
            for (int y = 0; y < size; y++)
            {
                Assert.Equal(expected.AsSpan(y * size, size).ToArray(), plane.Row(y0 + y).Slice(x0, size).ToArray());
            }
        }
    }
}
