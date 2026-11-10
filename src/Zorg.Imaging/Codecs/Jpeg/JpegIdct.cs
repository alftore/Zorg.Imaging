using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Zorg.Imaging.Codecs.Jpeg;

/// <summary>
/// The inverse DCT (ITU-T T.81, A.3.3): at full size libjpeg's integer transform, sample for sample, and reduced to
/// the average of each 2 x 2, 4 x 4 or 8 x 8 cell for scaled decoding.
/// </summary>
/// <remarks>
/// <para>
/// Full size is the integer transform of libjpeg's <c>jpeg_idct_islow</c>, the default of libjpeg-turbo's
/// <c>djpeg</c>, with the same constants, scaling, rounding and range limit, so a decoded block is exactly djpeg's.
/// </para>
/// <para>
/// The 8 x 8 block is f(x, y) = Σu Σv C(u)/2 C(v)/2 F(v, u) cos((2x+1)uπ/16) cos((2y+1)vπ/16), with C(0) = 1/√2.
/// Reduced to n samples along a side, each sample is the mean of the 8/n full-size samples in its cell. The mean is
/// linear, so it moves inside the sum: the basis is replaced by its mean over each cell,
/// B(u, x') = (n/8) Σ over the cell of C(u)/2 cos((2x+1)uπ/16), and all 64 coefficients still count. The result is
/// the area average of the exact transform (before rounding), at a fraction of the cost: a box pre-shrink fused into
/// the transform, which is what lets a large JPEG decode at 1/2, 1/4 or 1/8 of its size. Sizes may differ across and
/// down, so chroma stored at half width only can be brought to luma's grid by the transform itself.
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

    // The integer transform's constants: each is round(x · 2^13), the precision libjpeg's islow transform uses.
    private const int ConstBits = 13;
    private const int Pass1Bits = 2;
    private const int F0298631336 = 2446;
    private const int F0390180644 = 3196;
    private const int F0541196100 = 4433;
    private const int F0765366865 = 6270;
    private const int F0899976223 = 7373;
    private const int F1175875602 = 9633;
    private const int F1501321110 = 12299;
    private const int F1847759065 = 15137;
    private const int F1961570560 = 16069;
    private const int F2053119869 = 16819;
    private const int F2562915447 = 20995;
    private const int F3072711026 = 25172;

    // Pass 1 keeps two more bits than the samples need; pass 2 removes them with the 2^13 and the 8 of the 2-D scale,
    // and adds 128 on the way (a whole multiple of the divisor, so the rounding is unchanged).
    private const int Pass1Shift = ConstBits - Pass1Bits;
    private const int Pass2Shift = ConstBits + Pass1Bits + 3;
    private const int Pass2Bias = (1 << (Pass2Shift - 1)) + (128 << Pass2Shift);

    // Pass 1's multipliers, as pairs of 16-bit values for multiply-add-adjacent: see TransformAvx2.
    private static readonly Vector256<short>[] Pass1Pairs = MakePass1Pairs();

    // Pass 2's: for outputs 0 to 3 (in both halves), the multipliers of inputs (0, 4), (2, 6), (1, 3) and (5, 7).
    private static readonly Vector256<short>[] Pass2Pairs = MakePass2Pairs();

    // The byte shuffle that puts each row's inputs in that order.
    private static readonly Vector256<byte> Pass2Order = Vector256.Create(
        Vector128.Create((byte)0, 1, 8, 9, 4, 5, 12, 13, 2, 3, 6, 7, 10, 11, 14, 15),
        Vector128.Create((byte)0, 1, 8, 9, 4, 5, 12, 13, 2, 3, 6, 7, 10, 11, 14, 15));

    // Basis[n - 1][u * n + x']: the mean of C(u)/2 · cos((2x+1)uπ/16) over cell x', for the reduced transform.
    private static readonly float[][] Basis = [Make(1), Make(2), [], Make(4), [], [], [], Make(8)];

    /// <summary>
    /// Turns one block of quantised coefficients into 8 x 8 samples with the integer transform of libjpeg's
    /// <c>jpeg_idct_islow</c> (the default of libjpeg-turbo's <c>djpeg</c>), so its samples are exactly djpeg's.
    /// </summary>
    /// <remarks>
    /// The arithmetic is libjpeg-turbo's SIMD version of it: coefficients times quantisation in 16 bits, the columns
    /// with 13-bit constants descaled to keep 2 extra bits and saturated to 16 bits, then the rows descaled and
    /// clamped to 0 to 255. Every step between the descales is a sum of products, so whatever order they are added in,
    /// the 32-bit results are the same: the vector paths agree with each other and with the butterfly for all inputs.
    /// </remarks>
    /// <param name="coefficients">The 64 coefficients in natural (row-major) order, as decoded.</param>
    /// <param name="quantisation">The 64 quantisation values in natural order.</param>
    /// <param name="output">Where row 0 of the samples goes.</param>
    /// <param name="stride">The distance between rows in <paramref name="output"/>.</param>
    public static void Transform(ReadOnlySpan<short> coefficients, ReadOnlySpan<short> quantisation, Span<byte> output, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(coefficients.Length, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(quantisation.Length, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(output.Length, (7 * stride) + 8);

        // Only DC: the block is flat. Column 0 of pass 1 is the DC times 4 (saturated), and every row of pass 2 descales it.
        ReadOnlySpan<Vector128<short>> rows = MemoryMarshal.Cast<short, Vector128<short>>(coefficients[..64]);
        Vector128<short> any = (rows[0] & Vector128.Create(0, -1, -1, -1, -1, -1, -1, -1)) | rows[1] | rows[2] | rows[3] | rows[4] | rows[5] | rows[6] | rows[7];
        if (any == Vector128<short>.Zero)
        {
            int dc = Math.Clamp((short)(coefficients[0] * quantisation[0]) << Pass1Bits, short.MinValue, short.MaxValue);
            byte flat = (byte)Math.Clamp(((dc << ConstBits) + Pass2Bias) >> Pass2Shift, 0, 255);
            ulong eight = flat * 0x0101010101010101UL;
            for (int y = 0; y < 8; y++)
            {
                MemoryMarshal.Write(output.Slice(y * stride, 8), eight);
            }

            return;
        }

        if (Avx2.IsSupported)
        {
            TransformAvx2(rows, MemoryMarshal.Cast<short, Vector128<short>>(quantisation[..64]), output, stride);
        }
        else
        {
            TransformPortable(coefficients, quantisation, output, stride);
        }
    }

    /// <summary>
    /// Both passes in 256-bit vectors. Pass 1 runs down the columns, eight at a time, as the butterfly's even and odd
    /// halves: interleaving two rows of coefficients makes each 32-bit lane a pair, which one multiply-add-adjacent
    /// turns into one column's share of an output. Pass 2 runs along two rows at a time, each pair of inputs broadcast
    /// and multiplied by its matrix entries for four outputs, the even and odd halves giving the other four.
    /// </summary>
    private static void TransformAvx2(ReadOnlySpan<Vector128<short>> rows, ReadOnlySpan<Vector128<short>> quantisation, Span<byte> output, int stride)
    {
        ReadOnlySpan<Vector256<short>> k = Pass1Pairs;
        Vector128<short> r0 = rows[0] * quantisation[0];
        Vector128<short> r1 = rows[1] * quantisation[1];
        Vector128<short> r2 = rows[2] * quantisation[2];
        Vector128<short> r3 = rows[3] * quantisation[3];
        Vector128<short> r4 = rows[4] * quantisation[4];
        Vector128<short> r5 = rows[5] * quantisation[5];
        Vector128<short> r6 = rows[6] * quantisation[6];
        Vector128<short> r7 = rows[7] * quantisation[7];

        // Even half: outputs y and 7 - y share it, from rows 0, 4 and 2, 6.
        Vector256<short> p04 = Interleave(r0, r4);
        Vector256<short> p26 = Interleave(r2, r6);
        Vector256<int> sum04 = Avx2.MultiplyAddAdjacent(p04, k[0]);
        Vector256<int> difference04 = Avx2.MultiplyAddAdjacent(p04, k[1]);
        Vector256<int> rotated0 = Avx2.MultiplyAddAdjacent(p26, k[2]);
        Vector256<int> rotated1 = Avx2.MultiplyAddAdjacent(p26, k[3]);
        Vector256<int> even0 = sum04 + rotated0;
        Vector256<int> even3 = sum04 - rotated0;
        Vector256<int> even1 = difference04 + rotated1;
        Vector256<int> even2 = difference04 - rotated1;

        // Odd half: added for output y, subtracted for 7 - y, from rows 1, 3 and 5, 7.
        Vector256<short> p13 = Interleave(r1, r3);
        Vector256<short> p57 = Interleave(r5, r7);
        Vector256<int> odd0 = Avx2.MultiplyAddAdjacent(p13, k[4]) + Avx2.MultiplyAddAdjacent(p57, k[5]);
        Vector256<int> odd1 = Avx2.MultiplyAddAdjacent(p13, k[6]) + Avx2.MultiplyAddAdjacent(p57, k[7]);
        Vector256<int> odd2 = Avx2.MultiplyAddAdjacent(p13, k[8]) + Avx2.MultiplyAddAdjacent(p57, k[9]);
        Vector256<int> odd3 = Avx2.MultiplyAddAdjacent(p13, k[10]) + Avx2.MultiplyAddAdjacent(p57, k[11]);

        // Descaled and saturated to 16 bits, two rows to a vector: [row y | row y + 1].
        Vector256<short> w01 = Pack1(even0 + odd0, even1 + odd1);
        Vector256<short> w23 = Pack1(even2 + odd2, even3 + odd3);
        Vector256<short> w45 = Pack1(even3 - odd3, even2 - odd2);
        Vector256<short> w67 = Pack1(even1 - odd1, even0 - odd0);

        Vector256<byte> top = Avx2.PackUnsignedSaturate(Rows(w01), Rows(w23));
        Vector256<byte> bottom = Avx2.PackUnsignedSaturate(Rows(w45), Rows(w67));

        // Each 128-bit half holds two rows: [row 0, row 2 | row 1, row 3].
        Vector256<ulong> a = top.AsUInt64();
        Vector256<ulong> b = bottom.AsUInt64();
        MemoryMarshal.Write(output, a.GetElement(0));
        MemoryMarshal.Write(output[stride..], a.GetElement(2));
        MemoryMarshal.Write(output[(2 * stride)..], a.GetElement(1));
        MemoryMarshal.Write(output[(3 * stride)..], a.GetElement(3));
        MemoryMarshal.Write(output[(4 * stride)..], b.GetElement(0));
        MemoryMarshal.Write(output[(5 * stride)..], b.GetElement(2));
        MemoryMarshal.Write(output[(6 * stride)..], b.GetElement(1));
        MemoryMarshal.Write(output[(7 * stride)..], b.GetElement(3));
    }

    // Columns 0 to 3 of two rows as pairs in the low half, 4 to 7 in the high half: lane i is (a[i], b[i]).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Interleave(Vector128<short> a, Vector128<short> b) =>
        Vector256.Create(Sse2.UnpackLow(a, b), Sse2.UnpackHigh(a, b));

    // Pass 1's descale of two rows, saturated to 16 bits and put in order: [row a | row b].
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Pack1(Vector256<int> a, Vector256<int> b)
    {
        Vector256<int> half = Vector256.Create(1 << (Pass1Shift - 1));
        Vector256<short> packed = Avx2.PackSignedSaturate(Avx2.ShiftRightArithmetic(a + half, Pass1Shift), Avx2.ShiftRightArithmetic(b + half, Pass1Shift));
        return Avx2.Permute4x64(packed.AsInt64(), 0b11_01_10_00).AsInt16();
    }

    // Pass 2 on [row a | row b], by the same symmetry as pass 1: the inputs reordered to pairs (0, 4), (2, 6), (1, 3)
    // and (5, 7), each broadcast and multiplied by its matrix entries for outputs 0 to 3, give the even and odd halves;
    // their sum is outputs 0 to 3 and their difference 7 to 4. Descaled with 128 added and saturated to 16 bits, in
    // order: [row a | row b].
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Rows(Vector256<short> rows)
    {
        ReadOnlySpan<Vector256<short>> k = Pass2Pairs;
        Vector256<int> pairs = Avx2.Shuffle(rows.AsByte(), Pass2Order).AsInt32();
        Vector256<int> bias = Vector256.Create(Pass2Bias);
        Vector256<int> even = Avx2.MultiplyAddAdjacent(Avx2.Shuffle(pairs, 0x00).AsInt16(), k[0]) + Avx2.MultiplyAddAdjacent(Avx2.Shuffle(pairs, 0x55).AsInt16(), k[1]) + bias;
        Vector256<int> odd = Avx2.MultiplyAddAdjacent(Avx2.Shuffle(pairs, 0xAA).AsInt16(), k[2]) + Avx2.MultiplyAddAdjacent(Avx2.Shuffle(pairs, 0xFF).AsInt16(), k[3]);
        Vector256<int> low = Avx2.ShiftRightArithmetic(even + odd, Pass2Shift);
        Vector256<int> high = Avx2.Shuffle(Avx2.ShiftRightArithmetic(even - odd, Pass2Shift), 0b00_01_10_11);
        return Avx2.PackSignedSaturate(low, high);
    }

    /// <summary>
    /// The butterfly itself, four columns (then four rows) at a time, for hardware without AVX2. Internal so the tests
    /// can hold it to the same output as the AVX2 path on any machine.
    /// </summary>
    internal static void TransformPortable(ReadOnlySpan<short> coefficients, ReadOnlySpan<short> quantisation, Span<byte> output, int stride)
    {
        // Pass 1 down the columns, stored transposed (work[u * 8 + y]) so pass 2 loads its inputs as vectors.
        Span<int> work = stackalloc int[64];
        Vector128<int> half = Vector128.Create(1 << (Pass1Shift - 1));
        Vector128<int> low = Vector128.Create((int)short.MinValue);
        Vector128<int> high = Vector128.Create((int)short.MaxValue);
        Span<Vector128<int>> sums = stackalloc Vector128<int>[8];
        for (int column = 0; column < 8; column += 4)
        {
            Butterfly(
                Dequantise(coefficients, quantisation, 0, column),
                Dequantise(coefficients, quantisation, 1, column),
                Dequantise(coefficients, quantisation, 2, column),
                Dequantise(coefficients, quantisation, 3, column),
                Dequantise(coefficients, quantisation, 4, column),
                Dequantise(coefficients, quantisation, 5, column),
                Dequantise(coefficients, quantisation, 6, column),
                Dequantise(coefficients, quantisation, 7, column),
                sums);
            for (int y = 0; y < 8; y++)
            {
                Vector128<int> value = Vector128.Clamp(Vector128.ShiftRightArithmetic(sums[y] + half, Pass1Shift), low, high);
                for (int lane = 0; lane < 4; lane++)
                {
                    work[((column + lane) * 8) + y] = value.GetElement(lane);
                }
            }
        }

        // Pass 2 along the rows, four rows at a time: sums[x] holds output x of each.
        Vector128<int> bias = Vector128.Create(Pass2Bias);
        for (int row = 0; row < 8; row += 4)
        {
            Butterfly(
                Vector128.Create(work.Slice(row, 4)),
                Vector128.Create(work.Slice(8 + row, 4)),
                Vector128.Create(work.Slice(16 + row, 4)),
                Vector128.Create(work.Slice(24 + row, 4)),
                Vector128.Create(work.Slice(32 + row, 4)),
                Vector128.Create(work.Slice(40 + row, 4)),
                Vector128.Create(work.Slice(48 + row, 4)),
                Vector128.Create(work.Slice(56 + row, 4)),
                sums);
            for (int x = 0; x < 8; x++)
            {
                Vector128<int> value = Vector128.Clamp(Vector128.ShiftRightArithmetic(sums[x] + bias, Pass2Shift), Vector128<int>.Zero, Vector128.Create(255));
                for (int lane = 0; lane < 4; lane++)
                {
                    output[((row + lane) * stride) + x] = (byte)value.GetElement(lane);
                }
            }
        }
    }

    // Four coefficients of a row times their quantisation, in 16 bits as the SIMD versions multiply, then widened.
    private static Vector128<int> Dequantise(ReadOnlySpan<short> coefficients, ReadOnlySpan<short> quantisation, int row, int column)
    {
        Vector128<short> product = Vector64.Create(coefficients.Slice((row * 8) + column, 4)).ToVector128() * Vector64.Create(quantisation.Slice((row * 8) + column, 4)).ToVector128();
        return Vector128.WidenLower(product);
    }

    /// <summary>
    /// One 1-D pass of the islow transform on four lanes: the eight inputs (i0 the DC) to the eight outputs' sums,
    /// still scaled by 2^13. A rotation by √2·cos(6π/16) for the even half, the LL&amp;M odd half with its shared z5.
    /// </summary>
    private static void Butterfly(Vector128<int> i0, Vector128<int> i1, Vector128<int> i2, Vector128<int> i3, Vector128<int> i4, Vector128<int> i5, Vector128<int> i6, Vector128<int> i7, Span<Vector128<int>> sums)
    {
        // Even half.
        Vector128<int> z1 = (i2 + i6) * F0541196100;
        Vector128<int> tmp2 = z1 - (i6 * F1847759065);
        Vector128<int> tmp3 = z1 + (i2 * F0765366865);
        Vector128<int> tmp0 = (i0 + i4) << ConstBits;
        Vector128<int> tmp1 = (i0 - i4) << ConstBits;
        Vector128<int> tmp10 = tmp0 + tmp3;
        Vector128<int> tmp13 = tmp0 - tmp3;
        Vector128<int> tmp11 = tmp1 + tmp2;
        Vector128<int> tmp12 = tmp1 - tmp2;

        // Odd half.
        Vector128<int> z5 = (i7 + i3 + i5 + i1) * F1175875602;
        Vector128<int> a = (i7 + i1) * -F0899976223;
        Vector128<int> b = (i5 + i3) * -F2562915447;
        Vector128<int> c = ((i7 + i3) * -F1961570560) + z5;
        Vector128<int> d = ((i5 + i1) * -F0390180644) + z5;
        Vector128<int> odd0 = (i7 * F0298631336) + a + c;
        Vector128<int> odd1 = (i5 * F2053119869) + b + d;
        Vector128<int> odd2 = (i3 * F3072711026) + b + c;
        Vector128<int> odd3 = (i1 * F1501321110) + a + d;

        sums[0] = tmp10 + odd3;
        sums[7] = tmp10 - odd3;
        sums[1] = tmp11 + odd2;
        sums[6] = tmp11 - odd2;
        sums[2] = tmp12 + odd1;
        sums[5] = tmp12 - odd1;
        sums[3] = tmp13 + odd0;
        sums[4] = tmp13 - odd0;
    }

    // The matrix of one 1-D pass, matrix[x * 8 + u] being how much input u adds to output x before descaling. A pass
    // has no rounding inside it, so these 64 integers are exactly the butterfly, run on each unit input.
    private static int[] MakeMatrix()
    {
        int[] matrix = new int[64];
        Span<Vector128<int>> sums = stackalloc Vector128<int>[8];
        Span<Vector128<int>> unit = stackalloc Vector128<int>[8];
        for (int u = 0; u < 8; u++)
        {
            unit.Clear();
            unit[u] = Vector128<int>.One;
            Butterfly(unit[0], unit[1], unit[2], unit[3], unit[4], unit[5], unit[6], unit[7], sums);
            for (int x = 0; x < 8; x++)
            {
                matrix[(x * 8) + u] = sums[x].ToScalar();
            }
        }

        return matrix;
    }

    // Pass 1's pairs, from the matrix: rows 0 and 4 give output 0 (their sum) and output 1 (their difference); rows 2
    // and 6 give outputs 0 and 1's rotation; then rows 1, 3 and 5, 7 for the odd half of outputs 0, 1, 2 and 3.
    private static Vector256<short>[] MakePass1Pairs()
    {
        int[] m = MakeMatrix();
        Vector256<short> Pair(int y, int u, int v) => Vector256.Create((int)(ushort)m[(y * 8) + u] | (m[(y * 8) + v] << 16)).AsInt16();
        return
        [
            Pair(0, 0, 4), Pair(1, 0, 4), Pair(0, 2, 6), Pair(1, 2, 6),
            Pair(0, 1, 3), Pair(0, 5, 7), Pair(1, 1, 3), Pair(1, 5, 7),
            Pair(2, 1, 3), Pair(2, 5, 7), Pair(3, 1, 3), Pair(3, 5, 7),
        ];
    }

    private static Vector256<short>[] MakePass2Pairs()
    {
        int[] m = MakeMatrix();
        Vector256<short> Pair(int u, int v) => Vector256.Create(
            Vector128.Create((int)(ushort)m[u] | (m[v] << 16), (ushort)m[8 + u] | (m[8 + v] << 16), (ushort)m[16 + u] | (m[16 + v] << 16), (ushort)m[24 + u] | (m[24 + v] << 16))).AsInt16();
        return [Pair(0, 4), Pair(2, 6), Pair(1, 3), Pair(5, 7)];
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
