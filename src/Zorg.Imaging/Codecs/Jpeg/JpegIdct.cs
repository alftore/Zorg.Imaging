using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Zorg.Imaging.Codecs.Jpeg;

/// <summary>
/// The inverse DCT (ITU-T T.81, A.3.3), at full size or reduced to the average of each 2 x 2, 4 x 4 or 8 x 8 cell.
/// </summary>
/// <remarks>
/// <para>
/// The 8 x 8 block is f(x, y) = Σu Σv C(u)/2 C(v)/2 F(v, u) cos((2x+1)uπ/16) cos((2y+1)vπ/16), with C(0) = 1/√2.
/// Reduced to n samples along a side, each sample is the mean of the 8/n full-size samples in its cell. The mean is
/// linear, so it moves inside the sum: the basis is replaced by its mean over each cell,
/// B(u, x') = (n/8) Σ over the cell of C(u)/2 cos((2x+1)uπ/16), and all 64 coefficients still count. The result is
/// exactly the area average of the full decode (before rounding), at a fraction of the cost: a box pre-shrink fused
/// into the transform, which is what lets a large JPEG decode at 1/2, 1/4 or 1/8 of its size.
/// </para>
/// <para>
/// Sizes may differ across and down, so chroma stored at half width only can be brought to luma's grid by the
/// transform itself. At full size, rows are 256-bit vectors, so each pass is 64 multiply-adds; zero rows are skipped.
/// </para>
/// </remarks>
internal static class JpegIdct
{
    // Natural (row-major) position of each zigzag index (T.81, Figure A.6).
    public static ReadOnlySpan<byte> ZigZag =>
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    // Basis[n - 1][u * n + x'] for n = 1, 2, 4, 8: the mean of C(u)/2 · cos((2x+1)uπ/16) over cell x'.
    private static readonly float[][] Basis = [Make(1), Make(2), [], Make(4), [], [], [], Make(8)];

    /// <summary>Turns one block of quantised coefficients into samples.</summary>
    /// <param name="coefficients">The 64 coefficients in zigzag order, as decoded.</param>
    /// <param name="quantisation">The 64 quantisation values in zigzag order.</param>
    /// <param name="wide">The samples across: 8, 4, 2 or 1.</param>
    /// <param name="high">The samples down: 8, 4, 2 or 1.</param>
    /// <param name="output">Where row 0 of the samples goes.</param>
    /// <param name="stride">The distance between rows in <paramref name="output"/>.</param>
    public static void Transform(ReadOnlySpan<short> coefficients, ReadOnlySpan<ushort> quantisation, int wide, int high, Span<byte> output, int stride)
    {
        // Only DC: the block is flat, at any size.
        if (!HasAc(coefficients))
        {
            byte flat = Clamp((coefficients[0] * quantisation[0] / 8f) + 128);
            for (int y = 0; y < high; y++)
            {
                output.Slice(y * stride, wide).Fill(flat);
            }

            return;
        }

        Span<float> block = stackalloc float[64];
        block.Clear();
        for (int k = 0; k < 64; k++)
        {
            if (coefficients[k] != 0)
            {
                block[ZigZag[k]] = coefficients[k] * quantisation[k];
            }
        }

        if (wide == 8 && high == 8)
        {
            Transform8(block, output, stride);
        }
        else
        {
            TransformReduced(block, wide, high, output, stride);
        }
    }

    private static bool HasAc(ReadOnlySpan<short> coefficients)
    {
        // Eight 128-bit loads cover the 64 coefficients; the DC is masked out of the first.
        ReadOnlySpan<Vector128<short>> vectors = MemoryMarshal.Cast<short, Vector128<short>>(coefficients[..64]);
        Vector128<short> any = vectors[0] & Vector128.Create(0, -1, -1, -1, -1, -1, -1, -1);
        for (int i = 1; i < 8; i++)
        {
            any |= vectors[i];
        }

        return any != Vector128<short>.Zero;
    }

    private static void Transform8(Span<float> block, Span<byte> output, int stride)
    {
        ReadOnlySpan<Vector256<float>> basis = MemoryMarshal.Cast<float, Vector256<float>>(Basis[7]);
        Span<Vector256<float>> rows = stackalloc Vector256<float>[8];
        int used = 0;

        // Across: row v becomes Σu F(v, u) · basis row u.
        for (int v = 0; v < 8; v++)
        {
            ReadOnlySpan<float> f = block.Slice(v * 8, 8);
            Vector256<float> sum = Vector256<float>.Zero;
            bool any = false;
            for (int u = 0; u < 8; u++)
            {
                if (f[u] != 0)
                {
                    sum += basis[u] * f[u];
                    any = true;
                }
            }

            rows[v] = sum;
            if (any)
            {
                used |= 1 << v;
            }
        }

        // Down: output row y is Σv basis(v, y) · row v.
        float[] b = Basis[7];
        Span<float> samples = stackalloc float[8];
        for (int y = 0; y < 8; y++)
        {
            Vector256<float> sum = Vector256.Create(128f);
            for (int v = 0; v < 8; v++)
            {
                if ((used & (1 << v)) != 0)
                {
                    sum += rows[v] * b[(v * 8) + y];
                }
            }

            sum.CopyTo(samples);
            Span<byte> row = output.Slice(y * stride, 8);
            for (int x = 0; x < 8; x++)
            {
                row[x] = Clamp(samples[x]);
            }
        }
    }

    private static void TransformReduced(Span<float> block, int wide, int high, Span<byte> output, int stride)
    {
        float[] across = Basis[wide - 1];
        float[] down = Basis[high - 1];
        Span<float> rows = stackalloc float[64];
        for (int v = 0; v < 8; v++)
        {
            ReadOnlySpan<float> f = block.Slice(v * 8, 8);
            for (int x = 0; x < wide; x++)
            {
                float sum = 0;
                for (int u = 0; u < 8; u++)
                {
                    sum += f[u] * across[(u * wide) + x];
                }

                rows[(v * 8) + x] = sum;
            }
        }

        for (int y = 0; y < high; y++)
        {
            for (int x = 0; x < wide; x++)
            {
                float sum = 128;
                for (int v = 0; v < 8; v++)
                {
                    sum += down[(v * high) + y] * rows[(v * 8) + x];
                }

                output[(y * stride) + x] = Clamp(sum);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clamp(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    // The mean of each basis function over each of n equal cells of the 8 samples.
    private static float[] Make(int n)
    {
        int cell = 8 / n;
        float[] matrix = new float[8 * n];
        for (int u = 0; u < 8; u++)
        {
            double c = u == 0 ? 1 / Math.Sqrt(2) : 1;
            for (int cellIndex = 0; cellIndex < n; cellIndex++)
            {
                double sum = 0;
                for (int x = cellIndex * cell; x < (cellIndex + 1) * cell; x++)
                {
                    sum += c / 2 * Math.Cos(((2 * x) + 1) * u * Math.PI / 16);
                }

                matrix[(u * n) + cellIndex] = (float)(sum / cell);
            }
        }

        return matrix;
    }
}
