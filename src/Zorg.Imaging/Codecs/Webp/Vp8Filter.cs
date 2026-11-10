using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Zorg.Imaging.Codecs.Webp;

/// <summary>
/// The VP8 loop filter (RFC 6386, section 15, and the reference dixie_loopfilter.c) on eight pixels along an edge at a
/// time, in 16-bit lanes: the same integer arithmetic as the scalar reference in <see cref="Vp8Decoder"/>, so the
/// result is identical.
/// </summary>
/// <remarks>
/// For a horizontal edge the eight pixels across it are eight rows, loaded straight into registers; for a vertical edge
/// the 8 x 8 block around it is transposed into the same shape first, and back afterwards. Every pixel along an edge is
/// filtered independently, which is what makes the lanes possible; edges stay in the reference order.
/// </remarks>
internal static class Vp8Filter
{
    public enum Kind
    {
        Simple,
        Subblock,
        Macroblock,
    }

    /// <summary>Filters <paramref name="count"/> (8 or 16) pixels along an edge starting at <paramref name="at"/>.</summary>
    /// <param name="data">The plane.</param>
    /// <param name="at">The first pixel after the edge (q0) of the first point.</param>
    /// <param name="across">The step across the edge: 1 for a vertical edge, the stride for a horizontal one.</param>
    /// <param name="along">The step along the edge: the stride for a vertical edge, 1 for a horizontal one.</param>
    /// <param name="count">How many points: 8 or 16.</param>
    /// <param name="kind">Which filter.</param>
    /// <param name="edgeLimit">The edge limit (already raised by 2 for macroblock edges); for the simple filter, the whole limit.</param>
    /// <param name="interior">The interior limit.</param>
    /// <param name="hevThreshold">The high edge variance threshold.</param>
    public static void Edge(byte[] data, int at, int across, int along, int count, Kind kind, int edgeLimit, int interior, int hevThreshold)
    {
        // Every byte the loads and stores below can touch (eight across each point, from p3), checked once here.
        int lowest = at - (4 * across);
        int highest = at + (3 * across) + ((count - 1) * along);
        if (lowest < 0 || highest >= data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(at));
        }

        int reach = kind == Kind.Macroblock ? 3 : 2;
        if (along == 1 ? Vector128.IsHardwareAccelerated : Sse2.IsSupported)
        {
            ref byte origin = ref MemoryMarshal.GetArrayDataReference(data);
            for (int group = 0; group < count; group += 8)
            {
                ref byte start = ref Unsafe.Add(ref origin, at + (group * along));
                if (along == 1)
                {
                    HorizontalEdge(ref start, across, kind, edgeLimit, interior, hevThreshold, reach);
                }
                else
                {
                    VerticalEdge(ref start, along, kind, edgeLimit, interior, hevThreshold, reach);
                }
            }

            return;
        }

        Span<short> lanes = stackalloc short[64];
        for (int group = 0; group < count; group += 8)
        {
            int start = at + (group * along);
            Load(data, start, across, along, lanes);
            ref Vector128<short> v = ref Unsafe.As<short, Vector128<short>>(ref MemoryMarshal.GetReference(lanes));
            Filter(
                ref v,
                ref Unsafe.Add(ref v, 1),
                ref Unsafe.Add(ref v, 2),
                ref Unsafe.Add(ref v, 3),
                ref Unsafe.Add(ref v, 4),
                ref Unsafe.Add(ref v, 5),
                ref Unsafe.Add(ref v, 6),
                ref Unsafe.Add(ref v, 7),
                kind,
                edgeLimit,
                interior,
                hevThreshold);
            Store(data, start, across, along, lanes, reach);
        }
    }

    // Eight points of a horizontal edge: the eight rows across it are eight loads, filtered in registers, and the rows
    // the filter may change are stored back.
    private static void HorizontalEdge(ref byte start, int across, Kind kind, int edgeLimit, int interior, int hevThreshold, int reach)
    {
        Vector128<short> p3 = ReadRow(ref start, -4 * across);
        Vector128<short> p2 = ReadRow(ref start, -3 * across);
        Vector128<short> p1 = ReadRow(ref start, -2 * across);
        Vector128<short> p0 = ReadRow(ref start, -across);
        Vector128<short> q0 = ReadRow(ref start, 0);
        Vector128<short> q1 = ReadRow(ref start, across);
        Vector128<short> q2 = ReadRow(ref start, 2 * across);
        Vector128<short> q3 = ReadRow(ref start, 3 * across);
        if (!Filter(ref p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, ref q3, kind, edgeLimit, interior, hevThreshold))
        {
            return;
        }

        if (reach == 3)
        {
            WriteRow(ref start, -3 * across, p2);
            WriteRow(ref start, 2 * across, q2);
        }

        WriteRow(ref start, -2 * across, p1);
        WriteRow(ref start, -across, p0);
        WriteRow(ref start, 0, q0);
        WriteRow(ref start, across, q1);

        static Vector128<short> ReadRow(ref byte start, int offset) =>
            Vector128.WidenLower(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, offset))).AsByte()).AsInt16();

        // The filtered values are 0 to 255 already.
        static void WriteRow(ref byte start, int offset, Vector128<short> row) =>
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, offset), Vector128.Narrow(row.AsUInt16(), row.AsUInt16()).AsUInt64().ToScalar());
    }

    // Eight points of a vertical edge: the 8 x 8 block around it transposed into the shape of a horizontal edge.
    private static void VerticalEdge(ref byte start, int along, Kind kind, int edgeLimit, int interior, int hevThreshold, int reach)
    {
        ref byte block = ref Unsafe.Add(ref start, -4);
        Transpose(Rows(ref block, along, 0), Rows(ref block, along, 2), Rows(ref block, along, 4), Rows(ref block, along, 6), out var c0, out var c1, out var c2, out var c3);
        Vector128<short> p3 = Sse2.UnpackLow(c0, Vector128<byte>.Zero).AsInt16();
        Vector128<short> p2 = Sse2.UnpackHigh(c0, Vector128<byte>.Zero).AsInt16();
        Vector128<short> p1 = Sse2.UnpackLow(c1, Vector128<byte>.Zero).AsInt16();
        Vector128<short> p0 = Sse2.UnpackHigh(c1, Vector128<byte>.Zero).AsInt16();
        Vector128<short> q0 = Sse2.UnpackLow(c2, Vector128<byte>.Zero).AsInt16();
        Vector128<short> q1 = Sse2.UnpackHigh(c2, Vector128<byte>.Zero).AsInt16();
        Vector128<short> q2 = Sse2.UnpackLow(c3, Vector128<byte>.Zero).AsInt16();
        Vector128<short> q3 = Sse2.UnpackHigh(c3, Vector128<byte>.Zero).AsInt16();
        if (!Filter(ref p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, ref q3, kind, edgeLimit, interior, hevThreshold))
        {
            return;
        }

        // Back to rows with the same transpose; only the pixels the filter may change are written.
        Transpose(
            Sse2.PackUnsignedSaturate(p3, p2),
            Sse2.PackUnsignedSaturate(p1, p0),
            Sse2.PackUnsignedSaturate(q0, q1),
            Sse2.PackUnsignedSaturate(q2, q3),
            out var r0,
            out var r1,
            out var r2,
            out var r3);
        WriteRow(ref block, 0, reach, r0.AsUInt64().ToScalar());
        WriteRow(ref block, along, reach, r0.AsUInt64().GetElement(1));
        WriteRow(ref block, 2 * along, reach, r1.AsUInt64().ToScalar());
        WriteRow(ref block, 3 * along, reach, r1.AsUInt64().GetElement(1));
        WriteRow(ref block, 4 * along, reach, r2.AsUInt64().ToScalar());
        WriteRow(ref block, 5 * along, reach, r2.AsUInt64().GetElement(1));
        WriteRow(ref block, 6 * along, reach, r3.AsUInt64().ToScalar());
        WriteRow(ref block, 7 * along, reach, r3.AsUInt64().GetElement(1));

        // Rows first and first + 1 of the block, step apart, as one vector.
        static Vector128<byte> Rows(ref byte block, int step, int first) => Vector128.Create(
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref block, first * step)),
            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref block, (first + 1) * step))).AsByte();

        // The pixels a filter may change in one row of eight around a vertical edge: p2 .. q2 for reach 3, else p1 .. q1.
        static void WriteRow(ref byte block, int offset, int reach, ulong row)
        {
            ref byte at = ref Unsafe.Add(ref block, offset);
            if (reach == 3)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref at, 1), (uint)(row >> 8));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref at, 5), (ushort)(row >> 40));
            }
            else
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref at, 2), (uint)(row >> 16));
            }
        }
    }

    // Without vectors (or SSE2 for a vertical edge). lanes[k * 8 + i]: pixel k - 4 across the edge (p3 .. q3) at point i along it.
    private static void Load(byte[] data, int start, int across, int along, Span<short> lanes)
    {
        for (int i = 0; i < 8; i++)
        {
            for (int k = 0; k < 8; k++)
            {
                lanes[(k * 8) + i] = data[start + (i * along) + ((k - 4) * across)];
            }
        }
    }

    // Writes back the pixels a filter may change: p1 .. q1, or p2 .. q2 for a macroblock edge.
    private static void Store(byte[] data, int start, int across, int along, ReadOnlySpan<short> lanes, int reach)
    {
        for (int i = 0; i < 8; i++)
        {
            for (int k = 4 - reach; k < 4 + reach; k++)
            {
                data[start + (i * along) + ((k - 4) * across)] = (byte)lanes[(k * 8) + i];
            }
        }
    }

    // An 8 x 8 byte transpose. Each input holds two rows of eight bytes (rows 2c and 2c + 1 in input c); each output
    // holds two columns the same way.
    private static void Transpose(Vector128<byte> p0, Vector128<byte> p1, Vector128<byte> p2, Vector128<byte> p3, out Vector128<byte> c0, out Vector128<byte> c1, out Vector128<byte> c2, out Vector128<byte> c3)
    {
        Vector128<short> a0 = Sse2.UnpackLow(p0, Sse2.ShiftRightLogical128BitLane(p0, 8)).AsInt16();
        Vector128<short> a1 = Sse2.UnpackLow(p1, Sse2.ShiftRightLogical128BitLane(p1, 8)).AsInt16();
        Vector128<short> a2 = Sse2.UnpackLow(p2, Sse2.ShiftRightLogical128BitLane(p2, 8)).AsInt16();
        Vector128<short> a3 = Sse2.UnpackLow(p3, Sse2.ShiftRightLogical128BitLane(p3, 8)).AsInt16();
        Vector128<int> b0 = Sse2.UnpackLow(a0, a1).AsInt32(), b1 = Sse2.UnpackHigh(a0, a1).AsInt32();
        Vector128<int> b2 = Sse2.UnpackLow(a2, a3).AsInt32(), b3 = Sse2.UnpackHigh(a2, a3).AsInt32();
        c0 = Sse2.UnpackLow(b0, b2).AsByte();
        c1 = Sse2.UnpackHigh(b0, b2).AsByte();
        c2 = Sse2.UnpackLow(b1, b3).AsByte();
        c3 = Sse2.UnpackHigh(b1, b3).AsByte();
    }

    // Filters the eight points in the lanes; false when no point passes the thresholds, so nothing changed.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Filter(ref Vector128<short> p3, ref Vector128<short> p2, ref Vector128<short> p1, ref Vector128<short> p0, ref Vector128<short> q0, ref Vector128<short> q1, ref Vector128<short> q2, ref Vector128<short> q3, Kind kind, int edgeLimit, int interior, int hevThreshold)
    {
        // Which points to filter at all.
        Vector128<short> limit = Vector128.Create((short)(kind == Kind.Simple ? edgeLimit : (2 * edgeLimit) + interior));
        Vector128<short> mask = Vector128.LessThanOrEqual(
            (Vector128.Abs(p0 - q0) * 2) + Vector128.ShiftRightArithmetic(Vector128.Abs(p1 - q1), 1),
            limit);
        Vector128<short> hev = Vector128<short>.AllBitsSet;
        if (kind != Kind.Simple)
        {
            Vector128<short> i = Vector128.Create((short)interior);
            mask &= Vector128.LessThanOrEqual(Vector128.Abs(p3 - p2), i) & Vector128.LessThanOrEqual(Vector128.Abs(p2 - p1), i)
                & Vector128.LessThanOrEqual(Vector128.Abs(p1 - p0), i) & Vector128.LessThanOrEqual(Vector128.Abs(q3 - q2), i)
                & Vector128.LessThanOrEqual(Vector128.Abs(q2 - q1), i) & Vector128.LessThanOrEqual(Vector128.Abs(q1 - q0), i);
            Vector128<short> t = Vector128.Create((short)hevThreshold);
            hev = Vector128.GreaterThan(Vector128.Abs(p1 - p0), t) | Vector128.GreaterThan(Vector128.Abs(q1 - q0), t);
        }

        if (mask == Vector128<short>.Zero)
        {
            return false;
        }

        // filter_common, with the outer taps where `outer` is set (always for the simple filter and macroblock edges
        // with high variance; where variance is high for subblock edges).
        Vector128<short> outer = kind == Kind.Subblock ? hev : Vector128<short>.AllBitsSet;
        Vector128<short> a = Saturate8((q0 - p0) * 3 + (Saturate8(p1 - q1) & outer));
        Vector128<short> top = Vector128.Create((short)127);
        Vector128<short> f1 = Vector128.ShiftRightArithmetic(Vector128.Min(a + Vector128.Create((short)4), top), 3);
        Vector128<short> f2 = Vector128.ShiftRightArithmetic(Vector128.Min(a + Vector128.Create((short)3), top), 3);
        Vector128<short> commonP0 = Clamp255(p0 + f2);
        Vector128<short> commonQ0 = Clamp255(q0 - f1);

        Vector128<short> newP2 = p2, newP1 = p1, newP0, newQ0, newQ1 = q1, newQ2 = q2;
        switch (kind)
        {
            case Kind.Simple:
                newP0 = commonP0;
                newQ0 = commonQ0;
                break;

            case Kind.Subblock:
            {
                // Without the outer taps, p1 and q1 move too.
                Vector128<short> a2 = Vector128.ShiftRightArithmetic(f1 + Vector128<short>.One, 1);
                newP0 = commonP0;
                newQ0 = commonQ0;
                newP1 = Vector128.ConditionalSelect(hev, p1, Clamp255(p1 + a2));
                newQ1 = Vector128.ConditionalSelect(hev, q1, Clamp255(q1 - a2));
                break;
            }

            default:
            {
                // filter_mb_edge where variance is low: three taps either side, weights 27, 18, 9 over 128.
                Vector128<short> w = Saturate8(Saturate8(p1 - q1) + ((q0 - p0) * 3));
                Vector128<short> sixtyThree = Vector128.Create((short)63);
                Vector128<short> a27 = Vector128.ShiftRightArithmetic((w * 27) + sixtyThree, 7);
                Vector128<short> a18 = Vector128.ShiftRightArithmetic((w * 18) + sixtyThree, 7);
                Vector128<short> a9 = Vector128.ShiftRightArithmetic((w * 9) + sixtyThree, 7);
                newP0 = Vector128.ConditionalSelect(hev, commonP0, Clamp255(p0 + a27));
                newQ0 = Vector128.ConditionalSelect(hev, commonQ0, Clamp255(q0 - a27));
                newP1 = Vector128.ConditionalSelect(hev, p1, Clamp255(p1 + a18));
                newQ1 = Vector128.ConditionalSelect(hev, q1, Clamp255(q1 - a18));
                newP2 = Vector128.ConditionalSelect(hev, p2, Clamp255(p2 + a9));
                newQ2 = Vector128.ConditionalSelect(hev, q2, Clamp255(q2 - a9));
                break;
            }
        }

        p2 = Vector128.ConditionalSelect(mask, newP2, p2);
        p1 = Vector128.ConditionalSelect(mask, newP1, p1);
        p0 = Vector128.ConditionalSelect(mask, newP0, p0);
        q0 = Vector128.ConditionalSelect(mask, newQ0, q0);
        q1 = Vector128.ConditionalSelect(mask, newQ1, q1);
        q2 = Vector128.ConditionalSelect(mask, newQ2, q2);
        return true;
    }

    private static Vector128<short> Saturate8(Vector128<short> x) =>
        Vector128.Min(Vector128.Max(x, Vector128.Create((short)-128)), Vector128.Create((short)127));

    private static Vector128<short> Clamp255(Vector128<short> x) =>
        Vector128.Min(Vector128.Max(x, Vector128<short>.Zero), Vector128.Create((short)255));
}
