using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;

namespace Zorg.Imaging.Codecs.Png;

/// <summary>
/// Filters and compresses a PNG's rows in independent bands, so bands can run on separate cores, and joins them into
/// one zlib stream, as pigz does.
/// </summary>
/// <remarks>
/// Each band is a raw deflate stream ended with a sync flush (byte-aligned, not final), except the last, which ends
/// the stream. The bands are fixed by the image's size alone, so the file is the same whatever the number of cores.
/// A band can't refer back into the one before it, which costs a few hundred bytes a band.
/// </remarks>
internal static class PngDeflate
{
    /// <summary>About how many bytes of filtered rows go in a band.</summary>
    public const int BandBytes = 1 << 20;

    private const uint AdlerBase = 65521;

    // What Optimize tries on each band: these filter strategies, each with these zlib strategies, at level 9.
    private static readonly PngFilter[] OptimizeFilters = [PngFilter.None, PngFilter.Sub, PngFilter.Up, PngFilter.Average, PngFilter.Paeth, PngFilter.MinimumSum, PngFilter.Entropy, PngFilter.Bigrams, PngFilter.BigramEntropy];

    // What Adaptive tries on a sample of each band's rows, in this order (the first wins a tie).
    private static readonly PngFilter[] AdaptiveFilters = [PngFilter.Sub, PngFilter.Up, PngFilter.Paeth, PngFilter.MinimumSum];
    private static readonly ZLibCompressionStrategy[] OptimizeStrategies = [ZLibCompressionStrategy.Default, ZLibCompressionStrategy.Filtered, ZLibCompressionStrategy.HuffmanOnly, ZLibCompressionStrategy.RunLengthEncoding];

    /// <summary>Rows in each band.</summary>
    public static int RowsPerBand(int rowBytes) => Math.Max(1, BandBytes / (rowBytes + 1));

    /// <summary>The two-byte zlib header for a compression level.</summary>
    public static byte[] ZlibHeader(int level) => level switch
    {
        <= 1 => [0x78, 0x01],
        <= 5 => [0x78, 0x5E],
        6 => [0x78, 0x9C],
        _ => [0x78, 0xDA],
    };

    /// <summary>
    /// The filter strategy <paramref name="filter"/> means for this layout and level; <see cref="PngFilter.Adaptive"/>
    /// stays as it is for <see cref="Compress"/> to choose per band.
    /// </summary>
    public static PngFilter Resolve(PngFilter filter, PngLayout layout, int level) =>
        level == 0 ? PngFilter.None
        : filter != PngFilter.Adaptive ? filter
        : layout.IsPaletteOrPacked ? PngFilter.None
        : PngFilter.Adaptive;

    /// <summary>Filters and compresses rows <paramref name="first"/> to <paramref name="first"/> + <paramref name="count"/>.</summary>
    /// <param name="image">The image.</param>
    /// <param name="layout">How rows are written.</param>
    /// <param name="first">The band's first row.</param>
    /// <param name="count">Rows in the band.</param>
    /// <param name="filter">The filter strategy, already resolved.</param>
    /// <param name="level">The zlib level.</param>
    /// <param name="optimize">Whether to try several strategies at level 9 and keep the smallest.</param>
    /// <param name="last">Whether this band ends the stream.</param>
    /// <returns>The compressed band, the Adler-32 of its filtered bytes and how many there were.</returns>
    public static Band Compress(Image image, PngLayout layout, int first, int count, PngFilter filter, int level, bool optimize, bool last)
    {
        int rowBytes = layout.RowBytes(image.Width);
        int stride = rowBytes + 1;
        byte[] raws = ArrayPool<byte>.Shared.Rent((count + 1) * rowBytes);
        byte[] filtered = ArrayPool<byte>.Shared.Rent(count * stride);
        try
        {
            uint[] scratch = new uint[image.Width];
            raws.AsSpan(0, rowBytes).Clear();
            if (first > 0)
            {
                layout.WriteRow(image, first - 1, raws.AsSpan(0, rowBytes), scratch);
            }

            for (int r = 0; r < count; r++)
            {
                layout.WriteRow(image, first + r, raws.AsSpan((r + 1) * rowBytes, rowBytes), scratch);
            }

            var chooser = new RowFilter(rowBytes, layout.FilterStep);
            if (filter == PngFilter.Adaptive && !optimize)
            {
                filter = ChooseForBand(chooser, raws, filtered, rowBytes, count);
            }

            Band? best = null;
            PngFilter bestFilter = filter;
            PngFilter[] filters = optimize ? OptimizeFilters : [filter];
            ZLibCompressionStrategy[] strategies = optimize ? OptimizeStrategies : [ZLibCompressionStrategy.Default];
            foreach (PngFilter f in filters)
            {
                for (int r = 0; r < count; r++)
                {
                    chooser.Choose(f, raws.AsSpan((r + 1) * rowBytes, rowBytes), raws.AsSpan(r * rowBytes, rowBytes), filtered.AsSpan(r * stride, stride));
                }

                ReadOnlySpan<byte> data = filtered.AsSpan(0, count * stride);
                uint adler = Adler32(1, data);
                foreach (ZLibCompressionStrategy strategy in strategies)
                {
                    (byte[] bytes, int length) = Deflate(data, optimize ? 9 : level, strategy, last);
                    if (best is null || length < best.Value.Length)
                    {
                        best = new Band(bytes, length, adler, data.Length);
                        bestFilter = f;
                    }
                }
            }

            if (optimize)
            {
                // The winning filter's bytes again, through the slow optimal-parsing encoder; kept when smaller.
                for (int r = 0; r < count; r++)
                {
                    chooser.Choose(bestFilter, raws.AsSpan((r + 1) * rowBytes, rowBytes), raws.AsSpan(r * rowBytes, rowBytes), filtered.AsSpan(r * stride, stride));
                }

                ReadOnlySpan<byte> data = filtered.AsSpan(0, count * stride);
                (byte[] bytes, int length) = OptimalDeflate.Compress(data, last);
                if (length < best!.Value.Length)
                {
                    best = new Band(bytes, length, best.Value.Adler, data.Length);
                }
            }

            return best!.Value;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raws);
            ArrayPool<byte>.Shared.Return(filtered);
        }
    }

    // The filter for a band: each of AdaptiveFilters on a sample of its rows (eight in every 64, or all of a small band),
    // compressed at level 1, and the smallest kept. One filter for the band beat choosing per row by the smallest sum
    // (libpng's way, the default before) on photos, graphics and cut-outs alike, by 1 to 10%, and costs less than
    // filtering each row five ways; level 1 ranks the filters as level 6 does, or within 1%.
    private static PngFilter ChooseForBand(RowFilter chooser, byte[] raws, byte[] filtered, int rowBytes, int count)
    {
        int stride = rowBytes + 1;
        PngFilter best = AdaptiveFilters[0];
        int bestLength = int.MaxValue;
        foreach (PngFilter f in AdaptiveFilters)
        {
            int rows = 0;
            for (int r = 0; r < count; r++)
            {
                if (count > 64 && (r & 63) >= 8)
                {
                    continue;
                }

                chooser.Choose(f, raws.AsSpan((r + 1) * rowBytes, rowBytes), raws.AsSpan(r * rowBytes, rowBytes), filtered.AsSpan(rows * stride, stride));
                rows++;
            }

            (_, int length) = Deflate(filtered.AsSpan(0, rows * stride), 1, ZLibCompressionStrategy.Default, last: true);
            if (length < bestLength)
            {
                (best, bestLength) = (f, length);
            }
        }

        return best;
    }

    /// <summary>The Adler-32 of <paramref name="data"/>, continuing from <paramref name="adler"/> (1 to start).</summary>
    public static uint Adler32(uint adler, ReadOnlySpan<byte> data)
    {
        uint a = adler & 0xFFFF;
        uint b = adler >> 16;
        while (!data.IsEmpty)
        {
            // 5552 is the most bytes before b can overflow 32 bits.
            int take = Math.Min(data.Length, 5552);
            foreach (byte value in data[..take])
            {
                a += value;
                b += a;
            }

            a %= AdlerBase;
            b %= AdlerBase;
            data = data[take..];
        }

        return a | (b << 16);
    }

    /// <summary>The Adler-32 of two pieces joined, from each one's and the second's length (zlib's adler32_combine).</summary>
    public static uint CombineAdler(uint first, uint second, long secondLength)
    {
        uint remainder = (uint)(secondLength % AdlerBase);
        uint sum1 = first & 0xFFFF;
        uint sum2 = (uint)((ulong)remainder * sum1 % AdlerBase);
        sum1 += (second & 0xFFFF) + AdlerBase - 1;
        sum2 += (first >> 16) + (second >> 16) + AdlerBase - remainder;
        if (sum1 >= AdlerBase)
        {
            sum1 -= AdlerBase;
        }

        if (sum1 >= AdlerBase)
        {
            sum1 -= AdlerBase;
        }

        if (sum2 >= AdlerBase << 1)
        {
            sum2 -= AdlerBase << 1;
        }

        if (sum2 >= AdlerBase)
        {
            sum2 -= AdlerBase;
        }

        return sum1 | (sum2 << 16);
    }

    private static (byte[] Bytes, int Length) Deflate(ReadOnlySpan<byte> data, int level, ZLibCompressionStrategy strategy, bool last)
    {
        var output = new MemoryStream((data.Length / 2) + 64);
        var deflate = new DeflateStream(output, new ZLibCompressionOptions { CompressionLevel = level, CompressionStrategy = strategy }, leaveOpen: true);
        deflate.Write(data);
        if (last)
        {
            deflate.Dispose();
            return (output.GetBuffer(), (int)output.Length);
        }

        // A sync flush ends the band on a byte boundary without ending the stream; what Dispose adds is dropped.
        deflate.Flush();
        int length = (int)output.Length;
        deflate.Dispose();
        return (output.GetBuffer(), length);
    }

    /// <summary>A compressed band.</summary>
    /// <param name="Bytes">The compressed bytes, then unused room.</param>
    /// <param name="Length">How many of <paramref name="Bytes"/> are the band.</param>
    /// <param name="Adler">The Adler-32 of the band's filtered bytes.</param>
    /// <param name="RawLength">How many filtered bytes there were.</param>
    public readonly record struct Band(byte[] Bytes, int Length, uint Adler, long RawLength);

    /// <summary>Picks a row's filter by a strategy; keeps the room the strategies need.</summary>
    private sealed class RowFilter
    {
        private readonly int _step;
        private readonly byte[][] _candidates;
        private int[]? _seen;
        private int[]? _counts;
        private int[]? _touched;
        private int _stamp;

        public RowFilter(int rowBytes, int step)
        {
            _step = step;
            _candidates = new byte[5][];
            for (int i = 0; i < 5; i++)
            {
                _candidates[i] = new byte[rowBytes];
            }
        }

        /// <summary>Writes the filter byte and the filtered row to <paramref name="destination"/>.</summary>
        public void Choose(PngFilter strategy, ReadOnlySpan<byte> raw, ReadOnlySpan<byte> previous, Span<byte> destination)
        {
            int fixedFilter = strategy switch
            {
                PngFilter.None => 0,
                PngFilter.Sub => 1,
                PngFilter.Up => 2,
                PngFilter.Average => 3,
                PngFilter.Paeth => 4,
                _ => -1,
            };
            if (fixedFilter >= 0)
            {
                destination[0] = (byte)fixedFilter;
                PngFilters.FilterAndScore(fixedFilter, raw, previous, _step, destination[1..]);
                return;
            }

            int best = 0;
            double bestScore = double.MaxValue;
            for (int f = 0; f < 5; f++)
            {
                long sum = PngFilters.FilterAndScore(f, raw, previous, _step, _candidates[f]);
                double score = strategy switch
                {
                    PngFilter.Entropy => Entropy(_candidates[f]),
                    PngFilter.Bigrams => Bigrams(_candidates[f]),
                    PngFilter.BigramEntropy => BigramEntropy(_candidates[f]),
                    _ => sum,
                };
                if (score < bestScore)
                {
                    bestScore = score;
                    best = f;
                }
            }

            destination[0] = (byte)best;
            _candidates[best].CopyTo(destination[1..]);
        }

        // Shannon entropy times length, less a constant: n log n - sum(h log h) ranks the same as -sum(h log h).
        private static double Entropy(ReadOnlySpan<byte> row)
        {
            Span<int> histogram = stackalloc int[256];
            histogram.Clear();
            foreach (byte b in row)
            {
                histogram[b]++;
            }

            double sum = 0;
            foreach (int h in histogram)
            {
                if (h > 1)
                {
                    sum += h * Math.Log2(h);
                }
            }

            return -sum;
        }

        private int Bigrams(ReadOnlySpan<byte> row)
        {
            _seen ??= new int[65536];
            int stamp = NextStamp();
            int distinct = 0;
            for (int i = 1; i < row.Length; i++)
            {
                int key = (row[i - 1] << 8) | row[i];
                if (_seen[key] != stamp)
                {
                    _seen[key] = stamp;
                    distinct++;
                }
            }

            return distinct;
        }

        private double BigramEntropy(ReadOnlySpan<byte> row)
        {
            _seen ??= new int[65536];
            _counts ??= new int[65536];
            if (_touched is null || _touched.Length < row.Length)
            {
                _touched = new int[row.Length];
            }

            int stamp = NextStamp();
            int touched = 0;
            for (int i = 1; i < row.Length; i++)
            {
                int key = (row[i - 1] << 8) | row[i];
                if (_seen[key] != stamp)
                {
                    _seen[key] = stamp;
                    _counts[key] = 0;
                    _touched[touched++] = key;
                }

                _counts[key]++;
            }

            double sum = 0;
            for (int i = 0; i < touched; i++)
            {
                int h = _counts[_touched[i]];
                if (h > 1)
                {
                    sum += h * Math.Log2(h);
                }
            }

            return -sum;
        }

        private int NextStamp()
        {
            if (++_stamp == int.MaxValue)
            {
                Array.Clear(_seen!);
                _stamp = 1;
            }

            return _stamp;
        }
    }
}
