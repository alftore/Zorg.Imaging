using System;
using System.Numerics;
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

    // Columns[n - 1][x']: the basis for cell x' as a vector over u, for the reduced transform's dot products.
    private static readonly Vector256<float>[][] CellColumns = [Columns(1), Columns(2), [], Columns(4), [], [], [], Columns(8)];

    /// <summary>Turns one block of quantised coefficients into samples.</summary>
    /// <param name="coefficients">The 64 coefficients in natural (row-major) order, as decoded.</param>
    /// <param name="quantisation">The 64 quantisation values in natural order.</param>
    /// <param name="wide">The samples across: 8, 4, 2 or 1.</param>
    /// <param name="high">The samples down: 8, 4, 2 or 1.</param>
    /// <param name="output">Where row 0 of the samples goes.</param>
    /// <param name="stride">The distance between rows in <paramref name="output"/>.</param>
    public static void Transform(ReadOnlySpan<short> coefficients, ReadOnlySpan<float> quantisation, int wide, int high, Span<byte> output, int stride)
    {
        // Which rows of coefficients have anything in them, from one compare per row; the DC is masked out of row 0.
        ReadOnlySpan<Vector128<short>> rows = MemoryMarshal.Cast<short, Vector128<short>>(coefficients[..64]);
        int used = 0;
        for (int v = 0; v < 8; v++)
        {
            Vector128<short> row = v == 0 ? rows[0] & Vector128.Create(0, -1, -1, -1, -1, -1, -1, -1) : rows[v];
            if (row != Vector128<short>.Zero)
            {
                used |= 1 << v;
            }
        }

        // Only DC: the block is flat, at any size.
        if (used == 0)
        {
            byte flat = Clamp((coefficients[0] * quantisation[0] / 8f) + 128);
            for (int y = 0; y < high; y++)
            {
                output.Slice(y * stride, wide).Fill(flat);
            }

            return;
        }

        // Dequantise row by row as vectors: 8 shorts widened to floats, times the row's quantisation.
        used |= 1;
        Span<Vector256<float>> block = stackalloc Vector256<float>[8];
        ReadOnlySpan<Vector256<float>> q = MemoryMarshal.Cast<float, Vector256<float>>(quantisation[..64]);
        for (int v = 0; v < 8; v++)
        {
            if ((used & (1 << v)) == 0)
            {
                block[v] = Vector256<float>.Zero;
                continue;
            }

            Vector128<short> row = rows[v];
            Vector256<int> wideRow = Vector256.Create(Vector128.WidenLower(row), Vector128.WidenUpper(row));
            block[v] = Vector256.ConvertToSingle(wideRow) * q[v];
        }

        if (wide == 8 && high == 8)
        {
            Transform8(block, used, output, stride);
        }
        else
        {
            TransformReduced(block, used, wide, high, output, stride);
        }
    }

    private static void Transform8(ReadOnlySpan<Vector256<float>> block, int used, Span<byte> output, int stride)
    {
        ReadOnlySpan<Vector256<float>> basis = MemoryMarshal.Cast<float, Vector256<float>>(Basis[7]);
        Span<Vector256<float>> rows = stackalloc Vector256<float>[8];

        // Across: row v becomes the sum over u of F(v, u) times basis row u.
        for (int v = 0; v < 8; v++)
        {
            if ((used & (1 << v)) == 0)
            {
                continue;
            }

            // Each coefficient broadcast straight from memory, which is cheaper than taking it out of a register.
            ReadOnlySpan<float> f = MemoryMarshal.Cast<Vector256<float>, float>(block.Slice(v, 1));
            Vector256<float> sum = basis[0] * f[0];
            sum += basis[1] * f[1];
            sum += basis[2] * f[2];
            sum += basis[3] * f[3];
            sum += basis[4] * f[4];
            sum += basis[5] * f[5];
            sum += basis[6] * f[6];
            sum += basis[7] * f[7];
            rows[v] = sum;
        }

        // Down: output row y is the sum over v of basis(v, y) times row v; then round, clamp to 0..255 and narrow
        // eight samples at once.
        float[] b = Basis[7];
        Vector256<float> half = Vector256.Create(128.5f);
        Vector256<int> top = Vector256.Create(255);
        for (int y = 0; y < 8; y++)
        {
            Vector256<float> sum = half;
            for (int remaining = used; remaining != 0; remaining &= remaining - 1)
            {
                int v = BitOperations.TrailingZeroCount(remaining);
                sum += rows[v] * b[(v * 8) + y];
            }

            // Floor of (value + 0.5) is round-half-up; values here are never far below zero.
            Vector256<int> ints = Vector256.Min(Vector256.Max(Vector256.ConvertToInt32(Vector256.Floor(sum)), Vector256<int>.Zero), top);
            Vector128<short> shorts = Vector128.Narrow(ints.GetLower(), ints.GetUpper());
            Vector128<byte> bytes = Vector128.Narrow(shorts.AsUInt16(), shorts.AsUInt16());
            MemoryMarshal.Write(output.Slice(y * stride, 8), bytes.AsUInt64().ToScalar());
        }
    }

    // Down first, as 8-wide vectors over u: row y' of the result is the sum over v of basis(v, y') times coefficient row
    // v, skipping empty rows. Then each sample is one dot product of that row with its cell's basis column.
    private static void TransformReduced(ReadOnlySpan<Vector256<float>> block, int used, int wide, int high, Span<byte> output, int stride)
    {
        float[] down = Basis[high - 1];
        ReadOnlySpan<Vector256<float>> across = CellColumns[wide - 1];
        for (int y = 0; y < high; y++)
        {
            Vector256<float> row = Vector256<float>.Zero;
            for (int remaining = used; remaining != 0; remaining &= remaining - 1)
            {
                int v = BitOperations.TrailingZeroCount(remaining);
                row += block[v] * down[(v * high) + y];
            }

            Span<byte> line = output.Slice(y * stride, wide);
            for (int x = 0; x < wide; x++)
            {
                line[x] = Clamp(Vector256.Dot(row, across[x]) + 128);
            }
        }
    }

    private static Vector256<float>[] Columns(int n)
    {
        float[] basis = Make(n);
        var columns = new Vector256<float>[n];
        Span<float> column = stackalloc float[8];
        for (int x = 0; x < n; x++)
        {
            for (int u = 0; u < 8; u++)
            {
                column[u] = basis[(u * n) + x];
            }

            columns[x] = Vector256.Create<float>(column);
        }

        return columns;
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
