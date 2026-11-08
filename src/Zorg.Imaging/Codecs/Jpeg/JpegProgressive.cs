using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Zorg.Imaging.Codecs.Jpeg;

/// <summary>
/// The scans of a progressive JPEG (ITU-T T.81, Annex G): the coefficients the baseline encoder made, sent as a
/// coarse first picture that later scans refine. Each scan gets Huffman tables fitted to its own symbols.
/// </summary>
/// <remarks>
/// <para>
/// The scan script is the usual one (libjpeg's simple progression): the DC of every component at half precision; luma
/// AC 1 to 5 and the chroma AC, each one or two bits short; the rest of the luma AC; then the bits held back, one
/// refinement at a time. A browser can show the first scans while the rest arrive, and the file is usually a little
/// smaller than baseline, because each scan's tables fit its symbols.
/// </para>
/// <para>
/// The coefficients are the same as baseline's, so the decoded picture is exactly the same.
/// </para>
/// </remarks>
internal static class JpegProgressive
{
    // An end-of-band run can count at most this many blocks (G.1.2.2, EOB14).
    private const int MaxEndOfBandRun = 0x7FFF;

    /// <summary>One scan: which components, the band Ss to Se, and the successive approximation Ah and Al.</summary>
    public readonly record struct Scan(int[] Components, int Start, int End, int High, int Low);

    /// <summary>The scan script for one (grey) or three (YCbCr) components.</summary>
    public static Scan[] Script(int components) => components == 1
        ? [
            new([0], 0, 0, 0, 1),
            new([0], 1, 5, 0, 2),
            new([0], 6, 63, 0, 2),
            new([0], 1, 63, 2, 1),
            new([0], 0, 0, 1, 0),
            new([0], 1, 63, 1, 0),
        ]
        : [
            new([0, 1, 2], 0, 0, 0, 1),
            new([0], 1, 5, 0, 2),
            new([2], 1, 63, 0, 1),
            new([1], 1, 63, 0, 1),
            new([0], 6, 63, 0, 2),
            new([0], 1, 63, 2, 1),
            new([0, 1, 2], 0, 0, 1, 0),
            new([2], 1, 63, 1, 0),
            new([1], 1, 63, 1, 0),
            new([0], 1, 63, 1, 0),
        ];

    /// <summary>Where a scan's symbols go: counted to fit a table, or coded with one.</summary>
    public interface ISink
    {
        /// <summary>A Huffman symbol, from the DC table of class <paramref name="table"/> (0 luma, 1 chroma) or the scan's AC table.</summary>
        public void Symbol(int table, int symbol);

        /// <summary>Raw bits, most significant first.</summary>
        public void Bits(int value, int length);
    }

    /// <summary>Called for each block a scan visits, with the component it belongs to.</summary>
    public delegate void BlockVisitor(int component, ReadOnlySpan<short> block);

    /// <summary>Visits the blocks in the order a scan sends them.</summary>
    public delegate void BlockOrder(BlockVisitor visit);

    /// <summary>Codes one scan's blocks into the sink, the blocks given in the scan's order.</summary>
    public static void Encode<TSink>(ref TSink sink, Scan scan, BlockOrder blocks)
        where TSink : struct, ISink
    {
        if (scan.Start == 0)
        {
            EncodeDc(ref sink, scan, blocks);
        }
        else if (scan.High == 0)
        {
            EncodeAcFirst(ref sink, scan, blocks);
        }
        else
        {
            EncodeAcRefine(ref sink, scan, blocks);
        }
    }

    // G.1.2.1: the first DC scan codes differences of the DC shifted right by Al, as baseline does; a refinement sends
    // bit Al of each DC as it is.
    private static void EncodeDc<TSink>(ref TSink sink, Scan scan, BlockOrder blocks)
        where TSink : struct, ISink
    {
        int[] previous = new int[3];
        TSink copy = sink;
        blocks((component, block) =>
        {
            if (scan.High == 0)
            {
                int value = block[0] >> scan.Low;
                int difference = value - previous[component];
                previous[component] = value;
                int size = Category(difference);
                copy.Symbol(component == 0 ? 0 : 1, size);
                copy.Bits(Magnitude(difference, size), size);
            }
            else
            {
                copy.Bits((block[0] >> scan.Low) & 1, 1);
            }
        });
        sink = copy;
    }

    // G.1.2.2: each coefficient is sent as its magnitude shifted right by Al, with its sign; blocks with nothing left in
    // the band are counted into an end-of-band run that one symbol codes for many blocks.
    private static void EncodeAcFirst<TSink>(ref TSink sink, Scan scan, BlockOrder blocks)
        where TSink : struct, ISink
    {
        int run = 0;
        ulong band = Band(scan.Start, scan.End);
        TSink copy = sink;
        blocks((_, block) =>
        {
            // Only the non-zero coefficients are visited; the zeros between them are counted from their positions.
            int next = scan.Start;
            for (ulong mask = NonZero(block) & band; mask != 0; mask &= mask - 1)
            {
                int k = BitOperations.TrailingZeroCount(mask);
                int coefficient = block[k];
                int magnitude = Math.Abs(coefficient) >> scan.Low;
                if (magnitude == 0)
                {
                    continue;
                }

                int zeros = k - next;
                next = k + 1;
                EndOfBandRun(ref copy, ref run);
                while (zeros > 15)
                {
                    copy.Symbol(2, 0xF0);
                    zeros -= 16;
                }

                int value = coefficient < 0 ? -magnitude : magnitude;
                int size = Category(value);
                copy.Symbol(2, (zeros << 4) | size);
                copy.Bits(Magnitude(value, size), size);
            }

            if (next <= scan.End && ++run == MaxEndOfBandRun)
            {
                EndOfBandRun(ref copy, ref run);
            }
        });
        EndOfBandRun(ref copy, ref run);
        sink = copy;
    }

    // G.1.2.3: a coefficient that becomes non-zero at bit Al is coded as a run of still-zero coefficients and a sign;
    // every coefficient already non-zero gets one correction bit, sent after the next symbol, since the decoder reads
    // them as it passes those coefficients on the way to the symbol's position. An end-of-band run carries the
    // correction bits of all its blocks after it.
    private static void EncodeAcRefine<TSink>(ref TSink sink, Scan scan, BlockOrder blocks)
        where TSink : struct, ISink
    {
        int run = 0;
        var pending = new List<byte>();      // correction bits owed after the end-of-band run
        var corrections = new List<byte>();  // correction bits owed after the next symbol in this block
        ulong band = Band(scan.Start, scan.End);
        TSink copy = sink;
        blocks((_, block) =>
        {
            // Where the last coefficient that becomes non-zero in this pass is: zero runs past it fold into the EOB.
            ulong mask = NonZero(block) & band;
            int last = -1;
            for (ulong m = mask; m != 0; m &= m - 1)
            {
                int k = BitOperations.TrailingZeroCount(m);
                if (Math.Abs(block[k]) >> scan.Low == 1)
                {
                    last = k;
                }
            }

            // Visit the coefficients non-zero at this bit; the zeros between them are counted from their positions.
            int zeros = 0;
            int previous = scan.Start - 1;
            corrections.Clear();
            for (; mask != 0; mask &= mask - 1)
            {
                int k = BitOperations.TrailingZeroCount(mask);
                int magnitude = Math.Abs(block[k]) >> scan.Low;
                if (magnitude == 0)
                {
                    continue;
                }

                zeros += k - previous - 1;
                previous = k;
                while (zeros > 15 && k <= last)
                {
                    EndOfBandRun(ref copy, ref run, pending);
                    copy.Symbol(2, 0xF0);
                    zeros -= 16;
                    Flush(ref copy, corrections);
                }

                if (magnitude > 1)
                {
                    // Already non-zero: its next bit, owed until the next symbol.
                    corrections.Add((byte)(magnitude & 1));
                    continue;
                }

                EndOfBandRun(ref copy, ref run, pending);
                copy.Symbol(2, (zeros << 4) | 1);
                copy.Bits(block[k] < 0 ? 0 : 1, 1);
                Flush(ref copy, corrections);
                zeros = 0;
            }

            zeros += scan.End - previous;
            if (zeros > 0 || corrections.Count > 0)
            {
                pending.AddRange(corrections);
                if (++run == MaxEndOfBandRun)
                {
                    EndOfBandRun(ref copy, ref run, pending);
                }
            }
        });
        EndOfBandRun(ref copy, ref run, pending);
        sink = copy;
    }

    // Bits Ss to Se set.
    private static ulong Band(int start, int end) => (end == 63 ? ulong.MaxValue : (1UL << (end + 1)) - 1) & ~((1UL << start) - 1);

    // Bit k set when coefficient k isn't zero, from eight vector compares.
    private static ulong NonZero(ReadOnlySpan<short> block)
    {
        ReadOnlySpan<Vector128<short>> vectors = MemoryMarshal.Cast<short, Vector128<short>>(block[..64]);
        ulong mask = 0;
        for (int i = 0; i < 8; i++)
        {
            uint zero = Vector128.Equals(vectors[i], Vector128<short>.Zero).ExtractMostSignificantBits();
            mask |= (ulong)(~zero & 0xFF) << (8 * i);
        }

        return mask;
    }

    // An end-of-band run of n blocks: symbol (r << 4) with r = floor(log2 n), then the low r bits of n (G.1.2.2).
    private static void EndOfBandRun<TSink>(ref TSink sink, ref int run, List<byte>? pending = null)
        where TSink : struct, ISink
    {
        if (run == 0)
        {
            return;
        }

        int bits = 31 - int.LeadingZeroCount(run);
        sink.Symbol(2, bits << 4);
        sink.Bits(run & ((1 << bits) - 1), bits);
        run = 0;
        if (pending is not null)
        {
            Flush(ref sink, pending);
        }
    }

    private static void Flush<TSink>(ref TSink sink, List<byte> bits)
        where TSink : struct, ISink
    {
        foreach (byte bit in bits)
        {
            sink.Bits(bit, 1);
        }

        bits.Clear();
    }

    private static int Category(int value)
    {
        int magnitude = Math.Abs(value);
        return magnitude == 0 ? 0 : 32 - int.LeadingZeroCount(magnitude);
    }

    // T.81 F.1.2.1: a negative value is sent as its one's complement in `size` bits.
    private static int Magnitude(int value, int size) => (value < 0 ? value - 1 : value) & ((1 << size) - 1);
}
