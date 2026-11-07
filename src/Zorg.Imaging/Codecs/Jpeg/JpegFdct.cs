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
    /// <param name="samples">64 samples, row by row, already shifted to -128..127.</param>
    /// <param name="reciprocals">1 / quantisation value for each natural position.</param>
    /// <param name="output">The 64 quantised coefficients, in zigzag order.</param>
    public static void Transform(ReadOnlySpan<float> samples, ReadOnlySpan<float> reciprocals, Span<short> output)
    {
        ReadOnlySpan<Vector256<float>> columns = MemoryMarshal.Cast<float, Vector256<float>>(Transposed);
        Span<Vector256<float>> rows = stackalloc Vector256<float>[8];

        // Across: row y becomes Σx f(y, x) · (contribution of x to each u).
        for (int y = 0; y < 8; y++)
        {
            ReadOnlySpan<float> f = samples.Slice(y * 8, 8);
            Vector256<float> sum = Vector256<float>.Zero;
            for (int x = 0; x < 8; x++)
            {
                sum += columns[x] * f[x];
            }

            rows[y] = sum;
        }

        // Down: frequency row v is Σy basis(v, y) · row y; then quantise, rounding to nearest.
        Span<float> coefficients = stackalloc float[64];
        for (int v = 0; v < 8; v++)
        {
            Vector256<float> sum = Vector256<float>.Zero;
            for (int y = 0; y < 8; y++)
            {
                sum += rows[y] * Basis[(v * 8) + y];
            }

            (sum * Vector256.Create(reciprocals.Slice(v * 8, 8))).CopyTo(coefficients.Slice(v * 8, 8));
        }

        ReadOnlySpan<byte> zigZag = JpegIdct.ZigZag;
        for (int k = 0; k < 64; k++)
        {
            output[k] = (short)MathF.Round(coefficients[zigZag[k]]);
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
