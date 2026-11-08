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

    /// <summary>
    /// The reduced transform's weights for one quantisation table: for each coefficient (natural order), the amount it
    /// adds to each of the <paramref name="wide"/> x <paramref name="high"/> samples, quantisation included, padded to
    /// a multiple of four samples so each coefficient's row loads as whole vectors.
    /// </summary>
    public static float[] ReducedWeights(ReadOnlySpan<float> quantisation, int wide, int high)
    {
        float[] across = Basis[wide - 1];
        float[] down = Basis[high - 1];
        int outputs = Padded(wide * high);
        float[] weights = new float[64 * outputs];
        for (int v = 0; v < 8; v++)
        {
            for (int u = 0; u < 8; u++)
            {
                int k = (v * 8) + u;
                for (int y = 0; y < high; y++)
                {
                    for (int x = 0; x < wide; x++)
                    {
                        weights[(k * outputs) + (y * wide) + x] = quantisation[k] * down[(v * high) + y] * across[(u * wide) + x];
                    }
                }
            }
        }

        return weights;
    }

    /// <summary>
    /// The reduced transform from precomputed weights (<see cref="ReducedWeights"/>): each non-zero coefficient adds its
    /// weight row to all the samples at once, so a block costs one vector multiply-add per coefficient that isn't zero.
    /// The same sums as <see cref="Transform"/>'s, in another order.
    /// </summary>
    public static void TransformReduced(ReadOnlySpan<short> coefficients, float[] weights, int wide, int high, Span<byte> output, int stride)
    {
        // Which coefficients aren't zero, eight vector compares.
        ReadOnlySpan<Vector128<short>> rows = MemoryMarshal.Cast<short, Vector128<short>>(coefficients[..64]);
        ulong mask = 0;
        for (int i = 0; i < 8; i++)
        {
            uint zero = Vector128.Equals(rows[i], Vector128<short>.Zero).ExtractMostSignificantBits();
            mask |= (ulong)(~zero & 0xFF) << (8 * i);
        }

        int outputs = Padded(wide * high);
        Span<float> sums = stackalloc float[32];
        Span<Vector128<float>> sumVectors = MemoryMarshal.Cast<float, Vector128<float>>(sums[..outputs]);
        ReadOnlySpan<Vector128<float>> weightVectors = MemoryMarshal.Cast<float, Vector128<float>>(weights);
        Vector128<float> start = Vector128.Create(128f);
        if (outputs == 4)
        {
            // 2 x 2 (luma at 1/4): one vector of sums, kept in a register.
            Vector128<float> a = start;
            for (; mask != 0; mask &= mask - 1)
            {
                int k = BitOperations.TrailingZeroCount(mask);
                a += Vector128.Create((float)coefficients[k]) * weightVectors[k];
            }

            sumVectors[0] = a;
        }
        else if (outputs == 16)
        {
            // 4 x 4 (chroma at 1/4, luma at 1/2): four vectors of sums.
            Vector128<float> a = start, b = start, c = start, d = start;
            for (; mask != 0; mask &= mask - 1)
            {
                int k = BitOperations.TrailingZeroCount(mask);
                Vector128<float> value = Vector128.Create((float)coefficients[k]);
                ReadOnlySpan<Vector128<float>> row = weightVectors.Slice(k * 4, 4);
                a += value * row[0];
                b += value * row[1];
                c += value * row[2];
                d += value * row[3];
            }

            sumVectors[0] = a;
            sumVectors[1] = b;
            sumVectors[2] = c;
            sumVectors[3] = d;
        }
        else
        {
            sumVectors.Fill(start);
            int vectorsPerCoefficient = outputs / 4;
            for (; mask != 0; mask &= mask - 1)
            {
                int k = BitOperations.TrailingZeroCount(mask);
                Vector128<float> value = Vector128.Create((float)coefficients[k]);
                ReadOnlySpan<Vector128<float>> row = weightVectors.Slice(k * vectorsPerCoefficient, vectorsPerCoefficient);
                for (int i = 0; i < row.Length; i++)
                {
                    sumVectors[i] += value * row[i];
                }
            }
        }

        // Round half up, clamp to 0 to 255 and narrow, four samples at a time; then the rows into place.
        Span<byte> samples = stackalloc byte[32];
        Vector128<float> half = Vector128.Create(0.5f);
        Vector128<int> top = Vector128.Create(255);
        for (int i = 0; i < sumVectors.Length; i++)
        {
            Vector128<int> ints = Vector128.Min(Vector128.Max(Vector128.ConvertToInt32(Vector128.Floor(sumVectors[i] + half)), Vector128<int>.Zero), top);
            Vector128<short> shorts = Vector128.Narrow(ints, ints);
            Vector128<byte> bytes = Vector128.Narrow(shorts.AsUInt16(), shorts.AsUInt16());
            MemoryMarshal.Write(samples.Slice(i * 4, 4), bytes.AsUInt32().ToScalar());
        }

        // Rows of 2 or 4 samples go out as single stores, not copies.
        for (int y = 0; y < high; y++)
        {
            switch (wide)
            {
                case 2:
                    MemoryMarshal.Write(output.Slice(y * stride, 2), MemoryMarshal.Read<ushort>(samples.Slice(y * 2, 2)));
                    break;
                case 4:
                    MemoryMarshal.Write(output.Slice(y * stride, 4), MemoryMarshal.Read<uint>(samples.Slice(y * 4, 4)));
                    break;
                default:
                    samples.Slice(y * wide, wide).CopyTo(output.Slice(y * stride, wide));
                    break;
            }
        }
    }

    private static int Padded(int outputs) => (outputs + 3) & ~3;

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
