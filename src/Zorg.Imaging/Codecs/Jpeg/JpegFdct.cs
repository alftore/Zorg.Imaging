using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Zorg.Imaging.Codecs.Jpeg;

/// <summary>The forward DCT and quantisation of one 8 x 8 block (ITU-T T.81, A.3.3 and A.3.4).</summary>
/// <remarks>
/// F(v, u) = C(u)/2 C(v)/2 Σx Σy f(y, x) cos((2x+1)uπ/16) cos((2y+1)vπ/16): the same orthonormal basis the decoder
/// inverts, applied as two passes of 64 multiply-adds on 256-bit rows.
/// </remarks>
internal static class JpegFdct
{
    // Transposed[x] holds, for each u, C(u)/2 · cos((2x+1)uπ/16): sample x's contribution to every frequency.
    private static readonly float[] Transposed = MakeTransposed();

    // Basis[v * 8 + y] = C(v)/2 · cos((2y+1)vπ/16).
    private static readonly float[] Basis = MakeBasis();

    /// <summary>Transforms and quantises a block.</summary>
    /// <param name="samples">The block's samples, already centred on 0, row y at <paramref name="stride"/> * y.</param>
    /// <param name="stride">The distance between rows in <paramref name="samples"/>.</param>
    /// <param name="reciprocals">1 / quantisation value for each natural position.</param>
    /// <param name="output">The 64 quantised coefficients, in zigzag order.</param>
    public static void Transform(ReadOnlySpan<float> samples, int stride, ReadOnlySpan<float> reciprocals, Span<short> output)
    {
        ReadOnlySpan<Vector256<float>> columns = MemoryMarshal.Cast<float, Vector256<float>>(Transposed);
        Span<Vector256<float>> rows = stackalloc Vector256<float>[8];

        // Across: row y becomes the sum over x of f(y, x) times the contribution of x to each u.
        for (int y = 0; y < 8; y++)
        {
            ReadOnlySpan<float> f = samples.Slice(y * stride, 8);
            Vector256<float> sum = columns[0] * f[0];
            sum += columns[1] * f[1];
            sum += columns[2] * f[2];
            sum += columns[3] * f[3];
            sum += columns[4] * f[4];
            sum += columns[5] * f[5];
            sum += columns[6] * f[6];
            sum += columns[7] * f[7];
            rows[y] = sum;
        }

        // Down: frequency row v is the sum over y of basis(v, y) times row y; then quantise, rounding to nearest even
        // (as MathF.Round does), and narrow eight coefficients at once.
        Span<short> natural = stackalloc short[64];
        ReadOnlySpan<Vector256<float>> q = MemoryMarshal.Cast<float, Vector256<float>>(reciprocals[..64]);
        for (int v = 0; v < 8; v++)
        {
            ReadOnlySpan<float> b = Basis.AsSpan(v * 8, 8);
            Vector256<float> sum = rows[0] * b[0];
            sum += rows[1] * b[1];
            sum += rows[2] * b[2];
            sum += rows[3] * b[3];
            sum += rows[4] * b[4];
            sum += rows[5] * b[5];
            sum += rows[6] * b[6];
            sum += rows[7] * b[7];
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
