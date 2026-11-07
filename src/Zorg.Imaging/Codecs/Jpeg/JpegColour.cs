using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Zorg.Imaging.Codecs.Jpeg;

/// <summary>YCbCr to RGB (JFIF, T.871 section 7) for 16 pixels at a time.</summary>
/// <remarks>
/// The same 16.16 fixed-point arithmetic as the scalar code, lane by lane, so the result is identical; RGB is then
/// interleaved with byte shuffles (SSSE3 <c>pshufb</c>, AdvSimd <c>tbl</c>). Without either, nothing is done here and the
/// caller's scalar loop converts every pixel.
/// </remarks>
internal static class JpegColour
{
    // For output vector o (0..2) and channel c: which of the 16 pixels each output byte takes, 0x80 for none.
    private static readonly Vector128<byte>[] Interleave = MakeMasks();

    /// <summary>Converts as many whole groups of 16 pixels as the hardware allows; returns how many pixels it did.</summary>
    public static int YccToRgb24(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> blue, ReadOnlySpan<byte> red, Span<byte> output, int width)
    {
        if (!Vector256.IsHardwareAccelerated || !(Ssse3.IsSupported || AdvSimd.Arm64.IsSupported))
        {
            return 0;
        }

        int x = 0;
        ref byte y0 = ref MemoryMarshal.GetReference(luma);
        ref byte cb0 = ref MemoryMarshal.GetReference(blue);
        ref byte cr0 = ref MemoryMarshal.GetReference(red);
        ref byte out0 = ref MemoryMarshal.GetReference(output);
        Vector256<int> half = Vector256.Create(32768);
        Vector256<int> bias = Vector256.Create(128);
        Vector256<int> top = Vector256.Create(255);
        for (; x + 16 <= width && x + 16 <= luma.Length && x + 16 <= blue.Length && x + 16 <= red.Length && (3 * x) + 48 <= output.Length; x += 16)
        {
            (Vector256<int> yLow, Vector256<int> yHigh) = Widen(Vector128.LoadUnsafe(ref y0, (nuint)x));
            (Vector256<int> cbLow, Vector256<int> cbHigh) = Widen(Vector128.LoadUnsafe(ref cb0, (nuint)x));
            (Vector256<int> crLow, Vector256<int> crHigh) = Widen(Vector128.LoadUnsafe(ref cr0, (nuint)x));
            cbLow -= bias;
            cbHigh -= bias;
            crLow -= bias;
            crHigh -= bias;

            Vector128<byte> r = Pack(
                yLow + Vector256.ShiftRightArithmetic((crLow * 91881) + half, 16),
                yHigh + Vector256.ShiftRightArithmetic((crHigh * 91881) + half, 16),
                top);
            Vector128<byte> g = Pack(
                yLow + Vector256.ShiftRightArithmetic((cbLow * -22554) - (crLow * 46802) + half, 16),
                yHigh + Vector256.ShiftRightArithmetic((cbHigh * -22554) - (crHigh * 46802) + half, 16),
                top);
            Vector128<byte> b = Pack(
                yLow + Vector256.ShiftRightArithmetic((cbLow * 116130) + half, 16),
                yHigh + Vector256.ShiftRightArithmetic((cbHigh * 116130) + half, 16),
                top);

            ref byte destination = ref Unsafe.Add(ref out0, 3 * x);
            for (int o = 0; o < 3; o++)
            {
                Vector128<byte> bytes = Shuffle(r, Interleave[o * 3]) | Shuffle(g, Interleave[(o * 3) + 1]) | Shuffle(b, Interleave[(o * 3) + 2]);
                bytes.StoreUnsafe(ref destination, (nuint)(16 * o));
            }
        }

        return x;
    }

    /// <summary>
    /// The vertical half of the triangle filter: 3 x near + far (or 4 x near without vertical upsampling), per column.
    /// </summary>
    public static void ColumnSums(ReadOnlySpan<byte> near, ReadOnlySpan<byte> far, bool vertical, Span<short> sums)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref byte n0 = ref MemoryMarshal.GetReference(near);
            ref byte f0 = ref MemoryMarshal.GetReference(far);
            ref short s0 = ref MemoryMarshal.GetReference(sums);
            Vector128<short> three = Vector128.Create((short)(vertical ? 3 : 4));
            for (; i + 16 <= sums.Length; i += 16)
            {
                Vector128<byte> n = Vector128.LoadUnsafe(ref n0, (nuint)i);
                Vector128<byte> f = vertical ? Vector128.LoadUnsafe(ref f0, (nuint)i) : Vector128<byte>.Zero;
                Vector128<short> low = (Vector128.WidenLower(n).AsInt16() * three) + Vector128.WidenLower(f).AsInt16();
                Vector128<short> high = (Vector128.WidenUpper(n).AsInt16() * three) + Vector128.WidenUpper(f).AsInt16();
                low.StoreUnsafe(ref s0, (nuint)i);
                high.StoreUnsafe(ref s0, (nuint)(i + 8));
            }
        }

        for (; i < sums.Length; i++)
        {
            sums[i] = (short)(vertical ? (3 * near[i]) + far[i] : 4 * near[i]);
        }
    }

    /// <summary>
    /// The horizontal half: output 2i is (3 x sum i + sum i-1 + 8) / 16 and output 2i+1 uses sum i+1, edges repeated.
    /// </summary>
    public static void UpsampleTwice(ReadOnlySpan<short> sums, Span<byte> output)
    {
        int columns = sums.Length;
        int last = columns - 1;
        int i = 0;
        if (Vector128.IsHardwareAccelerated && (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported) && columns > 9)
        {
            // Columns 1 .. columns-2 have both neighbours; eight at a time, as long as two whole output pixels fit.
            ref short s0 = ref MemoryMarshal.GetReference(sums);
            ref byte o0 = ref MemoryMarshal.GetReference(output);
            Vector128<short> eight = Vector128.Create((short)8);
            Vector128<byte> zip = Vector128.Create((byte)0, 8, 1, 9, 2, 10, 3, 11, 4, 12, 5, 13, 6, 14, 7, 15);
            for (i = 1; i + 8 < columns && (2 * (i + 8)) <= output.Length; i += 8)
            {
                Vector128<short> here = Vector128.LoadUnsafe(ref s0, (nuint)i);
                Vector128<short> before = Vector128.LoadUnsafe(ref s0, (nuint)(i - 1));
                Vector128<short> after = Vector128.LoadUnsafe(ref s0, (nuint)(i + 1));
                Vector128<short> three = (here * 3) + eight;
                Vector128<ushort> even = Vector128.ShiftRightLogical(three + before, 4).AsUInt16();
                Vector128<ushort> odd = Vector128.ShiftRightLogical(three + after, 4).AsUInt16();
                Vector128<byte> packed = Vector128.Narrow(even, odd);
                Shuffle(packed, zip).StoreUnsafe(ref o0, (nuint)(2 * i));
            }

            // Column 0 and whatever is left go through the scalar loop below.
            Scalar(sums, output, 0, 1, last);
        }
        else
        {
            i = 0;
        }

        Scalar(sums, output, i, columns, last);
    }

    private static void Scalar(ReadOnlySpan<short> sums, Span<byte> output, int from, int to, int last)
    {
        for (int i = from; i < to; i++)
        {
            int three = (3 * sums[i]) + 8;
            int x = 2 * i;
            output[x] = (byte)((three + sums[Math.Max(i - 1, 0)]) >> 4);
            if (x + 1 < output.Length)
            {
                output[x + 1] = (byte)((three + sums[Math.Min(i + 1, last)]) >> 4);
            }
        }
    }

    private static (Vector256<int> Low, Vector256<int> High) Widen(Vector128<byte> bytes)
    {
        Vector256<ushort> shorts = Vector256.Create(Vector128.WidenLower(bytes), Vector128.WidenUpper(bytes));
        (Vector256<int> low, Vector256<int> high) = Vector256.Widen(shorts.AsInt16());
        return (low, high);
    }

    // Clamps 16 ints to 0..255 and narrows them to bytes, in order.
    private static Vector128<byte> Pack(Vector256<int> low, Vector256<int> high, Vector256<int> top)
    {
        low = Vector256.Min(Vector256.Max(low, Vector256<int>.Zero), top);
        high = Vector256.Min(Vector256.Max(high, Vector256<int>.Zero), top);
        Vector256<ushort> shorts = Vector256.Narrow(low.AsUInt32(), high.AsUInt32());
        return Vector128.Narrow(shorts.GetLower(), shorts.GetUpper());
    }

    private static Vector128<byte> Shuffle(Vector128<byte> source, Vector128<byte> mask) =>
        Ssse3.IsSupported ? Ssse3.Shuffle(source, mask) : AdvSimd.Arm64.VectorTableLookup(source, mask);

    private static Vector128<byte>[] MakeMasks()
    {
        var masks = new Vector128<byte>[9];
        Span<byte> mask = stackalloc byte[16];
        for (int o = 0; o < 3; o++)
        {
            for (int c = 0; c < 3; c++)
            {
                for (int j = 0; j < 16; j++)
                {
                    int at = (16 * o) + j;
                    mask[j] = at % 3 == c ? (byte)(at / 3) : (byte)0x80;
                }

                masks[(o * 3) + c] = Vector128.Create((ReadOnlySpan<byte>)mask);
            }
        }

        return masks;
    }
}
