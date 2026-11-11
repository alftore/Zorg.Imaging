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
}
