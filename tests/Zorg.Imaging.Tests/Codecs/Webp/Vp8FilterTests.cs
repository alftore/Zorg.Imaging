using System;
using Xunit;
using Zorg.Imaging.Codecs.Webp;

namespace Zorg.Imaging.Tests.Codecs.Webp;

/// <summary>The SIMD loop filter against the scalar reference on random pixels, for every filter and both directions (D13).</summary>
public class Vp8FilterTests
{
    public static TheoryData<int, bool, int> Cases()
    {
        var data = new TheoryData<int, bool, int>();
        foreach (Vp8Filter.Kind kind in new[] { Vp8Filter.Kind.Simple, Vp8Filter.Kind.Subblock, Vp8Filter.Kind.Macroblock })
        {
            foreach (bool vertical in new[] { true, false })
            {
                foreach (int seed in new[] { 1, 2, 3 })
                {
                    data.Add((int)kind, vertical, seed);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Simd_and_scalar_filters_give_the_same_pixels(int kindValue, bool vertical, int seed)
    {
        var kind = (Vp8Filter.Kind)kindValue;
        const int Stride = 40;
        var random = new Random(seed);
        for (int round = 0; round < 200; round++)
        {
            // Smooth pixels with small steps, so most points pass the thresholds and every branch runs.
            byte[] pixels = new byte[Stride * 40];
            int baseValue = random.Next(256);
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = (byte)Math.Clamp(baseValue + random.Next(-6 * (round % 5), (6 * (round % 5)) + 1) + ((i % Stride) >= 20 ? random.Next(-40, 41) : 0), 0, 255);
            }

            byte[] scalar = (byte[])pixels.Clone();
            byte[] simd = (byte[])pixels.Clone();
            int at = (8 * Stride) + 20;
            int across = vertical ? 1 : Stride;
            int along = vertical ? Stride : 1;
            int count = round % 2 == 0 ? 16 : 8;
            int level = random.Next(1, 64);
            int interior = random.Next(1, 10);
            int hev = random.Next(0, 3);

            if (kind == Vp8Filter.Kind.Simple)
            {
                int limit = ((level + 2) * 2) + interior;
                if (count == 8)
                {
                    count = 16;
                }

                ScalarSimple(scalar, at, across, along, limit);
                Vp8Filter.Edge(simd, at, across, along, 16, kind, limit, 0, 0);
            }
            else
            {
                int edge = kind == Vp8Filter.Kind.Macroblock ? level + 2 : level;
                Vp8Decoder.ScalarNormalEdge(scalar, at, across, along, count, edge, interior, hev, kind == Vp8Filter.Kind.Macroblock);
                Vp8Filter.Edge(simd, at, across, along, count, kind, edge, interior, hev);
            }

            Assert.Equal(scalar, simd);
        }
    }

    private static void ScalarSimple(byte[] data, int at, int across, int along, int limit) =>
        Vp8Decoder.ScalarSimpleEdge(data, at, across, along, limit);
}
