using System;
using Xunit;
using Zorg.Imaging.Codecs.Jpeg;

namespace Zorg.Imaging.Tests.Codecs.Jpeg;

/// <summary>
/// The integer inverse DCT against libjpeg's <c>jpeg_idct_islow</c> as published (the IJG's jidctint.c): a plain scalar
/// transcription of the algorithm here, in 64-bit arithmetic with its descale and range limit, and against the exact
/// cosine transform it approximates.
/// </summary>
public class JpegIdctTests
{
    [Theory]
    [InlineData(1, 0.0)]
    [InlineData(2, 0.05)]
    [InlineData(3, 0.3)]
    [InlineData(4, 0.9)]
    public void Blocks_transform_as_islow_does(int seed, double zeroShare)
    {
        var random = new Random(seed);
        short[] coefficients = new short[64];
        short[] quantisation = new short[64];
        byte[] actual = new byte[8 * 11];
        byte[] portable = new byte[8 * 11];
        double[] samples = new double[64];
        for (int block = 0; block < 2000; block++)
        {
            // Blocks as a JPEG holds them: 8-bit samples (a gradient, an edge and noise, often hitting 0 or 255), their
            // forward DCT quantised by values from 1 to 100, some coefficients dropped. Ringing reaches the clamps.
            double slope = random.Next(-40, 41);
            int edge = random.Next(0, 9);
            double noise = random.Next(0, 4) * 20;
            for (int i = 0; i < 64; i++)
            {
                double value = 128 + (slope * ((i % 8) - 3.5)) + ((i / 8) < edge ? 90 : -60) + (noise * (random.NextDouble() - 0.5));
                samples[i] = Math.Clamp(value, 0, 255);
            }

            for (int k = 0; k < 64; k++)
            {
                quantisation[k] = (short)random.Next(1, 101);
                double f = ForwardDct(samples, k / 8, k % 8);
                coefficients[k] = k > 0 && random.NextDouble() < zeroShare ? (short)0 : (short)Math.Round(f / quantisation[k]);
            }

            if (block % 7 == 0)
            {
                coefficients.AsSpan(1).Clear();
            }

            JpegIdct.Transform(coefficients, quantisation, actual, 11);
            JpegIdct.TransformPortable(coefficients, quantisation, portable, 11);
            byte[] expected = Islow(coefficients, quantisation);
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    Assert.Equal(expected[(y * 8) + x], actual[(y * 11) + x]);
                    Assert.Equal(expected[(y * 8) + x], portable[(y * 11) + x]);
                }
            }
        }
    }

    [Fact]
    public void The_vector_paths_agree_on_any_input()
    {
        // Far out of range too: every step between the descales is a sum of products, so 32-bit wrap-around gives the
        // same result whatever order the products are added in.
        var random = new Random(9);
        short[] coefficients = new short[64];
        short[] quantisation = new short[64];
        byte[] a = new byte[64];
        byte[] b = new byte[64];
        for (int block = 0; block < 2000; block++)
        {
            for (int k = 0; k < 64; k++)
            {
                coefficients[k] = (short)random.Next(short.MinValue, short.MaxValue + 1);
                quantisation[k] = (short)random.Next(short.MinValue, short.MaxValue + 1);
            }

            JpegIdct.Transform(coefficients, quantisation, a, 8);
            JpegIdct.TransformPortable(coefficients, quantisation, b, 8);
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void Samples_are_within_one_of_the_exact_transform()
    {
        // T.81 A.3.3 in doubles: islow's 13-bit constants keep it within one level of the true inverse DCT.
        var random = new Random(5);
        short[] coefficients = new short[64];
        short[] quantisation = new short[64];
        byte[] actual = new byte[64];
        for (int block = 0; block < 500; block++)
        {
            for (int k = 0; k < 64; k++)
            {
                quantisation[k] = (short)random.Next(1, 30);
                int limit = 120 / quantisation[k] / (1 + (k / 8) + (k % 8));
                coefficients[k] = (short)random.Next(-limit, limit + 1);
            }

            coefficients[0] = (short)random.Next(-300 / quantisation[0], 300 / quantisation[0]);
            JpegIdct.Transform(coefficients, quantisation, actual, 8);
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    double sum = 0;
                    for (int v = 0; v < 8; v++)
                    {
                        for (int u = 0; u < 8; u++)
                        {
                            double cu = u == 0 ? 1 / Math.Sqrt(2) : 1;
                            double cv = v == 0 ? 1 / Math.Sqrt(2) : 1;
                            sum += cu * cv / 4 * coefficients[(v * 8) + u] * quantisation[(v * 8) + u]
                                * Math.Cos(((2 * x) + 1) * u * Math.PI / 16) * Math.Cos(((2 * y) + 1) * v * Math.PI / 16);
                        }
                    }

                    double exact = Math.Clamp(sum + 128, 0, 255);
                    Assert.InRange(actual[(y * 8) + x], exact - 1, exact + 1);
                }
            }
        }
    }

    [Fact]
    public void A_flat_block_is_the_dc_descaled()
    {
        short[] coefficients = new short[64];
        short[] quantisation = new short[64];
        quantisation.AsSpan().Fill(16);
        byte[] output = new byte[64];

        // A DC of 10 · 16 = 160 is a mean 160 / 8 = 20 above 128; -3 · 16 = -48 is 6 below; and the clamps hold.
        foreach ((short dc, byte expected) in new (short, byte)[] { (10, 148), (-3, 122), (0, 128), (100, 255), (-100, 0), (1, 130) })
        {
            coefficients[0] = dc;
            JpegIdct.Transform(coefficients, quantisation, output, 8);
            Assert.All(output, sample => Assert.Equal(expected, sample));
        }
    }

    // jpeg_idct_islow, step for step: CONST_BITS 13, PASS1_BITS 2, the columns into a workspace, then the rows.
    private static byte[] Islow(short[] coefficients, short[] quantisation)
    {
        const int ConstBits = 13;
        const int Pass1Bits = 2;
        long[] workspace = new long[64];
        long[] input = new long[8];
        for (int column = 0; column < 8; column++)
        {
            for (int row = 0; row < 8; row++)
            {
                input[row] = coefficients[(row * 8) + column] * (long)quantisation[(row * 8) + column];
            }

            long[] sums = OneDimension(input);
            for (int row = 0; row < 8; row++)
            {
                workspace[(row * 8) + column] = Descale(sums[row], ConstBits - Pass1Bits);
            }
        }

        byte[] output = new byte[64];
        for (int row = 0; row < 8; row++)
        {
            long[] sums = OneDimension(workspace.AsSpan(row * 8, 8).ToArray());
            for (int x = 0; x < 8; x++)
            {
                // The range limit: samples are centred on 0 until here.
                output[(row * 8) + x] = (byte)Math.Clamp(Descale(sums[x], ConstBits + Pass1Bits + 3) + 128, 0, 255);
            }
        }

        return output;
    }

    // T.81 A.3.3: F(v, u) of samples centred on 0.
    private static double ForwardDct(double[] samples, int v, int u)
    {
        double sum = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                sum += (samples[(y * 8) + x] - 128) * Math.Cos(((2 * x) + 1) * u * Math.PI / 16) * Math.Cos(((2 * y) + 1) * v * Math.PI / 16);
            }
        }

        double cu = u == 0 ? 1 / Math.Sqrt(2) : 1;
        double cv = v == 0 ? 1 / Math.Sqrt(2) : 1;
        return cu * cv / 4 * sum;
    }

    private static long Descale(long value, int bits) => (value + (1L << (bits - 1))) >> bits;

    // FIX(x): the constant times 2^13, rounded.
    private static long Fix(double x) => (long)((x * 8192) + 0.5);

    private static long[] OneDimension(long[] input)
    {
        // Even part: a rotation of inputs 2 and 6, then the butterfly with 0 and 4.
        long z2 = input[2];
        long z3 = input[6];
        long z1 = (z2 + z3) * Fix(0.541196100);
        long tmp2 = z1 + (z3 * -Fix(1.847759065));
        long tmp3 = z1 + (z2 * Fix(0.765366865));
        long tmp0 = (input[0] + input[4]) << 13;
        long tmp1 = (input[0] - input[4]) << 13;
        long tmp10 = tmp0 + tmp3;
        long tmp13 = tmp0 - tmp3;
        long tmp11 = tmp1 + tmp2;
        long tmp12 = tmp1 - tmp2;

        // Odd part.
        tmp0 = input[7];
        tmp1 = input[5];
        tmp2 = input[3];
        tmp3 = input[1];
        z1 = tmp0 + tmp3;
        z2 = tmp1 + tmp2;
        z3 = tmp0 + tmp2;
        long z4 = tmp1 + tmp3;
        long z5 = (z3 + z4) * Fix(1.175875602);
        tmp0 *= Fix(0.298631336);
        tmp1 *= Fix(2.053119869);
        tmp2 *= Fix(3.072711026);
        tmp3 *= Fix(1.501321110);
        z1 *= -Fix(0.899976223);
        z2 *= -Fix(2.562915447);
        z3 *= -Fix(1.961570560);
        z4 *= -Fix(0.390180644);
        z3 += z5;
        z4 += z5;
        tmp0 += z1 + z3;
        tmp1 += z2 + z4;
        tmp2 += z2 + z3;
        tmp3 += z1 + z4;

        return [tmp10 + tmp3, tmp11 + tmp2, tmp12 + tmp1, tmp13 + tmp0, tmp13 - tmp0, tmp12 - tmp1, tmp11 - tmp2, tmp10 - tmp3];
    }
}
