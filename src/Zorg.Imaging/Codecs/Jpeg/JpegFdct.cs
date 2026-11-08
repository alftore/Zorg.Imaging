using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Zorg.Imaging.Codecs.Jpeg;

/// <summary>The forward DCT and quantisation of one 8 x 8 block (ITU-T T.81, A.3.3 and A.3.4).</summary>
/// <remarks>
/// F(v, u) = C(u)/2 C(v)/2 Σx Σy f(y, x) cos((2x+1)uπ/16) cos((2y+1)vπ/16): the same orthonormal basis the decoder
/// inverts. Down the columns, eight at once in 256-bit rows, it is split into the sums and differences of mirrored rows
/// (the even and odd frequencies), about 50 operations instead of 128; across, 64 multiply-adds against the basis.
/// </remarks>
internal static class JpegFdct
{
    // Transposed[x] holds, for each u, C(u)/2 · cos((2x+1)uπ/16): sample x's contribution to every frequency.
    private static readonly float[] Transposed = MakeTransposed();

    // cos(kπ/16) / 2, and for k = 4 also C(0) / 2 = 1 / (2√2), which is the same number.
    private static readonly Vector256<float> Half1 = Vector256.Create((float)(Math.Cos(1 * Math.PI / 16) / 2));
    private static readonly Vector256<float> Half2 = Vector256.Create((float)(Math.Cos(2 * Math.PI / 16) / 2));
    private static readonly Vector256<float> Half3 = Vector256.Create((float)(Math.Cos(3 * Math.PI / 16) / 2));
    private static readonly Vector256<float> Half4 = Vector256.Create((float)(Math.Cos(4 * Math.PI / 16) / 2));
    private static readonly Vector256<float> Half5 = Vector256.Create((float)(Math.Cos(5 * Math.PI / 16) / 2));
    private static readonly Vector256<float> Half6 = Vector256.Create((float)(Math.Cos(6 * Math.PI / 16) / 2));
    private static readonly Vector256<float> Half7 = Vector256.Create((float)(Math.Cos(7 * Math.PI / 16) / 2));

    /// <summary>Transforms and quantises a block.</summary>
    /// <param name="samples">The block's samples, already centred on 0, row y at <paramref name="stride"/> * y.</param>
    /// <param name="stride">The distance between rows in <paramref name="samples"/>.</param>
    /// <param name="reciprocals">1 / quantisation value for each natural position.</param>
    /// <param name="output">The 64 quantised coefficients, in zigzag order.</param>
    public static void Transform(ReadOnlySpan<float> samples, int stride, ReadOnlySpan<float> reciprocals, Span<short> output)
    {
        // Down: X(v) = C(v)/2 Σy x(y) cos((2y+1)vπ/16) for every column x at once. With s(k) = x(k) + x(7-k) and
        // d(k) = x(k) - x(7-k), the even frequencies are a 4-point transform of s and the odd ones a 4 x 4 product
        // with d.
        Vector256<float> r0 = Vector256.Create(samples.Slice(0, 8));
        Vector256<float> r1 = Vector256.Create(samples.Slice(stride, 8));
        Vector256<float> r2 = Vector256.Create(samples.Slice(2 * stride, 8));
        Vector256<float> r3 = Vector256.Create(samples.Slice(3 * stride, 8));
        Vector256<float> r4 = Vector256.Create(samples.Slice(4 * stride, 8));
        Vector256<float> r5 = Vector256.Create(samples.Slice(5 * stride, 8));
        Vector256<float> r6 = Vector256.Create(samples.Slice(6 * stride, 8));
        Vector256<float> r7 = Vector256.Create(samples.Slice(7 * stride, 8));
        Vector256<float> s0 = r0 + r7, s1 = r1 + r6, s2 = r2 + r5, s3 = r3 + r4;
        Vector256<float> d0 = r0 - r7, d1 = r1 - r6, d2 = r2 - r5, d3 = r3 - r4;
        Vector256<float> e0 = s0 + s3, e1 = s1 + s2, e2 = s0 - s3, e3 = s1 - s2;

        Span<Vector256<float>> down = stackalloc Vector256<float>[8];
        down[0] = (e0 + e1) * Half4;
        down[4] = (e0 - e1) * Half4;
        down[2] = (e2 * Half2) + (e3 * Half6);
        down[6] = (e2 * Half6) - (e3 * Half2);
        down[1] = (d0 * Half1) + (d1 * Half3) + (d2 * Half5) + (d3 * Half7);
        down[3] = (d0 * Half3) - (d1 * Half7) - (d2 * Half1) - (d3 * Half5);
        down[5] = (d0 * Half5) - (d1 * Half1) + (d2 * Half7) + (d3 * Half3);
        down[7] = (d0 * Half7) - (d1 * Half5) + (d2 * Half3) - (d3 * Half1);

        // Across: frequency row v is the sum over x of its value at x times the contribution of x to each u; then
        // quantise, rounding to nearest even (as MathF.Round does), and narrow eight coefficients at once.
        ReadOnlySpan<Vector256<float>> columns = MemoryMarshal.Cast<float, Vector256<float>>(Transposed);
        ReadOnlySpan<float> rows = MemoryMarshal.Cast<Vector256<float>, float>(down);
        Span<short> natural = stackalloc short[64];
        ReadOnlySpan<Vector256<float>> q = MemoryMarshal.Cast<float, Vector256<float>>(reciprocals[..64]);
        for (int v = 0; v < 8; v++)
        {
            ReadOnlySpan<float> f = rows.Slice(v * 8, 8);
            Vector256<float> sum = columns[0] * f[0];
            sum += columns[1] * f[1];
            sum += columns[2] * f[2];
            sum += columns[3] * f[3];
            sum += columns[4] * f[4];
            sum += columns[5] * f[5];
            sum += columns[6] * f[6];
            sum += columns[7] * f[7];
            Vector256<int> ints = Vector256.ConvertToInt32(Vector256.Round(sum * q[v]));
            Vector128.Narrow(ints.GetLower(), ints.GetUpper()).CopyTo(natural.Slice(v * 8, 8));
        }

        ReadOnlySpan<byte> zigZag = JpegIdct.ZigZag;
        for (int k = 0; k < 64; k++)
        {
            output[k] = natural[zigZag[k]];
        }
    }

    private static float[] MakeBasis()
    {
        float[] basis = new float[64];
        for (int v = 0; v < 8; v++)
        {
            double c = v == 0 ? 1 / Math.Sqrt(2) : 1;
            for (int y = 0; y < 8; y++)
            {
                basis[(v * 8) + y] = (float)(c / 2 * Math.Cos(((2 * y) + 1) * v * Math.PI / 16));
            }
        }

        return basis;
    }

    private static float[] MakeTransposed()
    {
        float[] basis = MakeBasis();
        float[] transposed = new float[64];
        for (int u = 0; u < 8; u++)
        {
            for (int x = 0; x < 8; x++)
            {
                transposed[(x * 8) + u] = basis[(u * 8) + x];
            }
        }

        return transposed;
    }
}
