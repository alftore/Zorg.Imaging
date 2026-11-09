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
/// For a horizontal edge the eight pixels across it are eight rows, loaded directly; for a vertical edge the 8 x 8
/// block around it is transposed into the same shape first, and back afterwards. Every pixel along an edge is filtered
/// independently, which is what makes the lanes possible; edges stay in the reference order.
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
        Span<short> lanes = stackalloc short[64];
        for (int group = 0; group < count; group += 8)
        {
            int start = at + (group * along);
            Load(data, start, across, along, lanes);
            ref Vector128<short> v = ref Unsafe.As<short, Vector128<short>>(ref MemoryMarshal.GetReference(lanes));
            Filter(ref v, kind, edgeLimit, interior, hevThreshold);
            Store(data, start, across, along, lanes, kind == Kind.Macroblock ? 3 : 2);
        }
    }

    // lanes[k * 8 + i]: pixel k - 4 across the edge (p3 .. q3) at point i along it.
    private static void Load(byte[] data, int start, int across, int along, Span<short> lanes)
    {
        if (along == 1)
        {
            // Horizontal edge: each of the eight rows holds eight consecutive points.
            for (int k = 0; k < 8; k++)
            {
                ulong row = MemoryMarshal.Read<ulong>(data.AsSpan(start + ((k - 4) * across), 8));
                Vector128<short> wide = Vector128.WidenLower(Vector128.CreateScalar(row).AsByte()).AsInt16();
                wide.CopyTo(lanes.Slice(k * 8, 8));
            }

            return;
        }

        // Vertical edge: transpose eight rows of eight pixels.
        if (Sse2.IsSupported)
        {
            Transpose(Rows(data, start - 4, along, 0), Rows(data, start - 4, along, 2), Rows(data, start - 4, along, 4), Rows(data, start - 4, along, 6), out var c0, out var c1, out var c2, out var c3);
            Span<Vector128<short>> vectors = MemoryMarshal.Cast<short, Vector128<short>>(lanes);
            vectors[0] = Vector128.WidenLower(c0).AsInt16();
            vectors[1] = Vector128.WidenUpper(c0).AsInt16();
            vectors[2] = Vector128.WidenLower(c1).AsInt16();
            vectors[3] = Vector128.WidenUpper(c1).AsInt16();
            vectors[4] = Vector128.WidenLower(c2).AsInt16();
            vectors[5] = Vector128.WidenUpper(c2).AsInt16();
            vectors[6] = Vector128.WidenLower(c3).AsInt16();
            vectors[7] = Vector128.WidenUpper(c3).AsInt16();
            return;
        }

        for (int i = 0; i < 8; i++)
        {
            ReadOnlySpan<byte> row = data.AsSpan(start + (i * along) - 4, 8);
            for (int k = 0; k < 8; k++)
            {
                lanes[(k * 8) + i] = row[k];
            }
        }
    }

    // Writes back the pixels a filter may change: p1 .. q1, or p2 .. q2 for a macroblock edge.
    private static void Store(byte[] data, int start, int across, int along, ReadOnlySpan<short> lanes, int reach)
    {
        if (along == 1)
        {
            for (int k = 4 - reach; k < 4 + reach; k++)
            {
                Vector128<short> wide = Vector128.Create(lanes.Slice(k * 8, 8));
                Vector128<byte> bytes = Vector128.Narrow(wide.AsUInt16(), wide.AsUInt16());
                MemoryMarshal.Write(data.AsSpan(start + ((k - 4) * across), 8), bytes.AsUInt64().ToScalar());
            }

            return;
        }

        if (Sse2.IsSupported)
        {
            // Back to rows with the same transpose; only the pixels the filter may change are written.
            ReadOnlySpan<Vector128<short>> vectors = MemoryMarshal.Cast<short, Vector128<short>>(lanes);
            Transpose(
                Sse2.PackUnsignedSaturate(vectors[0], vectors[1]),
                Sse2.PackUnsignedSaturate(vectors[2], vectors[3]),
                Sse2.PackUnsignedSaturate(vectors[4], vectors[5]),
                Sse2.PackUnsignedSaturate(vectors[6], vectors[7]),
                out var r0,
                out var r1,
                out var r2,
                out var r3);
            WriteRow(data, start - 4, reach, r0.AsUInt64().ToScalar());
            WriteRow(data, start - 4 + along, reach, r0.AsUInt64().GetElement(1));
            WriteRow(data, start - 4 + (2 * along), reach, r1.AsUInt64().ToScalar());
            WriteRow(data, start - 4 + (3 * along), reach, r1.AsUInt64().GetElement(1));
            WriteRow(data, start - 4 + (4 * along), reach, r2.AsUInt64().ToScalar());
            WriteRow(data, start - 4 + (5 * along), reach, r2.AsUInt64().GetElement(1));
            WriteRow(data, start - 4 + (6 * along), reach, r3.AsUInt64().ToScalar());
            WriteRow(data, start - 4 + (7 * along), reach, r3.AsUInt64().GetElement(1));
            return;
        }

        for (int i = 0; i < 8; i++)
        {
            Span<byte> row = data.AsSpan(start + (i * along) - 4, 8);
            for (int k = 4 - reach; k < 4 + reach; k++)
            {
                row[k] = (byte)lanes[(k * 8) + i];
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

    // Rows first and first + 1 of an 8 x 8 block at start, step apart, as one vector.
    private static Vector128<byte> Rows(byte[] data, int start, int step, int first) => Vector128.Create(
        MemoryMarshal.Read<ulong>(data.AsSpan(start + (first * step), 8)),
        MemoryMarshal.Read<ulong>(data.AsSpan(start + ((first + 1) * step), 8))).AsByte();

    // The pixels a filter may change in one row of eight around a vertical edge: p2 .. q2 for reach 3, else p1 .. q1.
    private static void WriteRow(byte[] data, int at, int reach, ulong row)
    {
        if (reach == 3)
        {
            MemoryMarshal.Write(data.AsSpan(at + 1, 4), (uint)(row >> 8));
            MemoryMarshal.Write(data.AsSpan(at + 5, 2), (ushort)(row >> 40));
        }
        else
        {
            MemoryMarshal.Write(data.AsSpan(at + 2, 4), (uint)(row >> 16));
        }
    }

    private static void Filter(ref Vector128<short> v, Kind kind, int edgeLimit, int interior, int hevThreshold)
    {
        ref Vector128<short> p3 = ref v;
        ref Vector128<short> p2 = ref Unsafe.Add(ref v, 1);
        ref Vector128<short> p1 = ref Unsafe.Add(ref v, 2);
        ref Vector128<short> p0 = ref Unsafe.Add(ref v, 3);
        ref Vector128<short> q0 = ref Unsafe.Add(ref v, 4);
        ref Vector128<short> q1 = ref Unsafe.Add(ref v, 5);
        ref Vector128<short> q2 = ref Unsafe.Add(ref v, 6);
        ref Vector128<short> q3 = ref Unsafe.Add(ref v, 7);

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
            return;
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
    }

    private static Vector128<short> Saturate8(Vector128<short> x) =>
        Vector128.Min(Vector128.Max(x, Vector128.Create((short)-128)), Vector128.Create((short)127));

    private static Vector128<short> Clamp255(Vector128<short> x) =>
        Vector128.Min(Vector128.Max(x, Vector128<short>.Zero), Vector128.Create((short)255));
}
