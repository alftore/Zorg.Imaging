using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.Intrinsics;

namespace Zorg.Imaging.Processing;

/// <summary>
/// Reduces straight-alpha ARGB pixels to at most 256 colours with full alpha, for palette PNGs (a <c>tRNS</c> entry per
/// colour) and GIF, comparing colours in Oklab, a space where distance follows what the eye sees.
/// </summary>
/// <remarks>
/// <para>
/// Each colour is a point of up to eight float lanes, each 0 to 255. An opaque picture uses three: Oklab's L, a and b
/// (<see cref="Oklab"/>). A picture with partly transparent pixels uses six: the Oklab of the colour as it shows over
/// black and as it shows over white, so an error in alpha counts as much as a page shows it and a nearly transparent
/// pixel's colour counts little. Either way the distance is Euclidean in fixed lanes, so means, k-means and the search
/// bounds below hold. Fully transparent pixels share one transparent entry.
/// </para>
/// <para>
/// Squared sRGB error, used before, spends the palette on hue and saturation, where the eye forgives more than in
/// lightness: on 168 test images Oklab raised SSIMULACRA 2 by 1.7 on average and 2 to 3 on photos, and by about 1 on
/// cut-outs with soft edges, for about 1 dB less PSNR. Three flat game screenshots lost up to 1.7.
/// </para>
/// <para>
/// The pixels are counted into cells, one for each distinct colour when there are at most 65,536 (icons and graphics with
/// antialiased edges gain 4 to 5 dB over grouping their colours), else of 64 levels a lane. Median cut splits the cells
/// into as many boxes as there are colours, always the box with the largest squared error, across its widest lane at
/// the weighted median; then 12 rounds of k-means move each colour to the mean of the cells nearest it. A colour whose
/// pixels are all opaque stays exactly opaque. Pixels are matched to their exact nearest colour through a cache of the
/// colours that can be nearest in each cell of a grid, with optional Floyd–Steinberg dithering of the premultiplied
/// values. Everything runs in a fixed order, so the result doesn't depend on the number of threads.
/// </para>
/// </remarks>
internal static class RgbaQuantizer
{
    // Rounds of k-means after median cut: 12 rather than 4 brought photos and screenshots 0.1 to 0.16 dB closer to the
    // source, with files 0.3% smaller, for a quarter more time on a photo. More gained little (16: 0.01 to 0.02 dB).
    private const int Rounds = 12;

    // The share of each pixel's error that full-strength dithering passes on: all of it left photos grainy, about 4 dB
    // further from the source, for hardly less banding. Dithering flat areas more, or edges and texture less, scored
    // lower by SSIMULACRA 2 than the same share everywhere.
    private const double DitherShare = 0.6;

    /// <summary>The palette as straight ARGB, and an index per pixel.</summary>
    public readonly record struct Result(uint[] Palette, byte[] Indices);

    public static Result Run(uint[] argb, int width, int height, int maxColours, double dither)
    {
        bool anyTransparent = false;
        bool anyTranslucent = false;
        var exact = new HashSet<uint>();
        uint previous = 0;
        for (int i = 0; i < argb.Length; i++)
        {
            uint p = argb[i];
            if (i > 0 && p == previous)
            {
                continue;
            }

            previous = p;
            uint key = p < 0x01000000u ? 0u : p;
            anyTransparent |= key == 0;
            anyTranslucent |= key != 0 && p < 0xFF000000u;
            if (exact.Count <= maxColours)
            {
                exact.Add(key);
            }
        }

        if (exact.Count <= maxColours)
        {
            return Exact(argb, [.. exact]);
        }

        var space = new Space(anyTranslucent);
        int colours = maxColours - (anyTransparent ? 1 : 0);
        Cells cells = Cells.Count(argb, FewColours(argb), space);
        Vector256<float>[] means = MedianCut(cells, Math.Max(colours, 1), space.Lanes);
        bool[] opaque = Refine(cells, means, space);

        var palette = new List<uint>(means.Length + 1);
        var search = new Vector256<float>[means.Length];
        var premultiplied = new Vector128<float>[means.Length];
        for (int k = 0; k < means.Length; k++)
        {
            uint colour = space.ToArgb(means[k], opaque[k]);
            palette.Add(colour);
            search[k] = space.FromArgb(colour);
            premultiplied[k] = Premultiply(colour);
        }

        int transparentIndex = -1;
        if (anyTransparent)
        {
            transparentIndex = palette.Count;
            palette.Add(0);
        }

        byte[] indices = Map(argb, width, height, search, premultiplied, transparentIndex, dither, space);
        return new Result([.. palette], indices);
    }

    private static Result Exact(uint[] argb, uint[] palette)
    {
        Array.Sort(palette);
        var lookup = new Dictionary<uint, byte>(palette.Length);
        for (int i = 0; i < palette.Length; i++)
        {
            lookup[palette[i]] = (byte)i;
        }

        byte[] indices = new byte[argb.Length];
        for (int i = 0; i < argb.Length; i++)
        {
            indices[i] = i > 0 && argb[i] == argb[i - 1] ? indices[i - 1] : lookup[argb[i] < 0x01000000u ? 0u : argb[i]];
        }

        return new Result(palette, indices);
    }

    // A pixel as premultiplied (A, R, G, B) floats from 0 to 255.
    internal static Vector128<float> Premultiply(uint p)
    {
        // (A, R, G, B) as floats, then R, G and B times A / 255.
        Vector128<byte> lanes = Vector128.Create((byte)3, 255, 255, 255, 2, 255, 255, 255, 1, 255, 255, 255, 0, 255, 255, 255);
        Vector128<float> v = Vector128.ConvertToSingle(Vector128.Shuffle(Vector128.CreateScalar(p).AsByte(), lanes).AsInt32());
        float a = p >> 24;
        return v * Vector128.Create(1, a / 255, a / 255, a / 255);
    }

    /// <summary>How colours become lanes and back: three Oklab lanes for opaque pictures, six (over black, over white) otherwise.</summary>
    internal readonly struct Space(bool overPage)
    {
        /// <summary>Whether colours are compared as they show over black and over white.</summary>
        public bool OverPage { get; } = overPage;

        /// <summary>The lanes in use, from lane 0; the rest are 0.</summary>
        public int Lanes => OverPage ? 6 : 3;

        /// <summary>A colour's lanes.</summary>
        public Vector256<float> FromArgb(uint argb) =>
            OverPage ? FromPremultiplied(Premultiply(argb)) : Vector256.Create(Oklab.FromArgb(argb), Vector128<float>.Zero);

        /// <summary>The lanes of a premultiplied (A, R, G, B) colour, as error diffusion carries it.</summary>
        public Vector256<float> FromPremultiplied(Vector128<float> pm)
        {
            if (!OverPage)
            {
                return Vector256.Create(Oklab.FromRgb(pm), Vector128<float>.Zero);
            }

            // Over black a premultiplied colour is itself; over white each channel gains 255 less its alpha, nothing
            // when opaque (most pixels of a cut-out), so one conversion serves both.
            float k = 255 - pm.GetElement(0);
            if (k == 0)
            {
                Vector128<float> lab = Oklab.FromRgb(pm);
                return Vector256.Create(lab.GetElement(0), lab.GetElement(1), lab.GetElement(2), lab.GetElement(0), lab.GetElement(1), lab.GetElement(2), 0, 0);
            }

            return Oklab.FromRgbPair(pm, Vector128.Min(pm + Vector128.Create(0, k, k, k), Vector128.Create(255f)));
        }

        /// <summary>The straight ARGB colour for a mean; an opaque one keeps alpha 255.</summary>
        public uint ToArgb(Vector256<float> mean, bool opaque)
        {
            uint black = Oklab.ToArgb(mean.GetElement(0), mean.GetElement(1), mean.GetElement(2));
            if (!OverPage || opaque)
            {
                return black;
            }

            // Over white each channel is the one over black plus 255 less alpha: alpha from the mean difference, then
            // the premultiplied channel as the average of its two estimates, made straight.
            uint white = Oklab.ToArgb(mean.GetElement(3), mean.GetElement(4), mean.GetElement(5));
            float difference = 0;
            for (int shift = 0; shift <= 16; shift += 8)
            {
                difference += (int)((white >> shift) & 255) - (int)((black >> shift) & 255);
            }

            float alpha = Math.Clamp(MathF.Round(255 - (difference / 3)), 1, 255);
            uint Channel(int shift)
            {
                float overBlack = (black >> shift) & 255;
                float overWhite = ((white >> shift) & 255) - (255 - alpha);
                return (uint)Math.Clamp(MathF.Round((overBlack + overWhite) / 2 * 255 / alpha), 0, 255);
            }

            return ((uint)alpha << 24) | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
        }
    }

    // The boxes, split until there are enough or none can be split: each step takes the box with the largest squared
    // error and cuts its widest lane at the weighted median.
    private static Vector256<float>[] MedianCut(Cells cells, int colours, int lanes)
    {
        int[] order = new int[cells.Length];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        // Means are 0 or more, so their float bits order as the values do.
        var cellMeans = new Vector256<float>[cells.Length];
        for (int c = 0; c < cellMeans.Length; c++)
        {
            cellMeans[c] = cells.Mean(c);
        }

        long[] keys = new long[order.Length];
        var boxes = new List<(int Start, int End, double Error)> { (0, order.Length, cells.Error(order, 0, order.Length)) };
        while (boxes.Count < colours)
        {
            int worst = -1;
            double most = 0;
            for (int b = 0; b < boxes.Count; b++)
            {
                if (boxes[b].End - boxes[b].Start > 1 && boxes[b].Error > most)
                {
                    (worst, most) = (b, boxes[b].Error);
                }
            }

            if (worst < 0)
            {
                break;
            }

            (int start, int end, _) = boxes[worst];
            int lane = cells.WidestLane(order, start, end);

            // By the lane's mean, then by cell: keys sorted with the cells, the mean in the high bits.
            for (int i = start; i < end; i++)
            {
                keys[i] = ((long)BitConverter.SingleToInt32Bits(cellMeans[order[i]].GetElement(lane)) << 32) | (uint)order[i];
            }

            Array.Sort(keys, order, start, end - start);
            long total = 0;
            for (int i = start; i < end; i++)
            {
                total += cells.Weight[order[i]];
            }

            long running = 0;
            int cut = start + 1;
            for (int i = start; i < end - 1; i++)
            {
                running += cells.Weight[order[i]];
                cut = i + 1;
                if (running * 2 >= total)
                {
                    break;
                }
            }

            boxes[worst] = (start, cut, cells.Error(order, start, cut));
            boxes.Add((cut, end, cells.Error(order, cut, end)));
        }

        var means = new Vector256<float>[boxes.Count];
        Span<double> sum = stackalloc double[Cells.Stride];
        for (int b = 0; b < boxes.Count; b++)
        {
            sum.Clear();
            long weight = 0;
            for (int i = boxes[b].Start; i < boxes[b].End; i++)
            {
                cells.AddSums(order[i], sum);
                weight += cells.Weight[order[i]];
            }

            means[b] = Cells.MeanOf(sum, weight, lanes);
        }

        return means;
    }

    // Rounds of k-means over the cells: each cell goes to its nearest colour, then each colour to the mean of its cells.
    // Returns, per colour, whether all its pixels are opaque (always so for an opaque picture, which skips the last
    // assignment that only finds that out).
    private static bool[] Refine(Cells cells, Vector256<float>[] means, Space space)
    {
        int[] nearest = new int[cells.Length];
        bool[] opaque = new bool[means.Length];
        Array.Fill(opaque, true);
        var finder = new NearestColour(means, space.Lanes, NearestColour.CoarseBits);

        // The cells are visited in the order of their place in the search cache, so each part of it is used while at hand.
        var cellMeans = new Vector256<float>[cells.Length];
        long[] place = new long[cells.Length];
        int[] visit = new int[cells.Length];
        for (int c = 0; c < cellMeans.Length; c++)
        {
            cellMeans[c] = cells.Mean(c);
            place[c] = finder.KeyOf(cellMeans[c]);
            visit[c] = c;
        }

        Array.Sort(place, visit);

        int bands = Parallelism.Bands((long)cells.Length * means.Length / 16, 64);
        var sums = new double[means.Length * Cells.Stride];
        long[] weights = new long[means.Length];
        for (int round = 0; round <= Rounds; round++)
        {
            if (round == Rounds && !space.OverPage)
            {
                break;
            }

            finder.Reset();
            Parallelism.For(bands, band =>
            {
                for (int v = (int)((long)cells.Length * band / bands); v < (int)((long)cells.Length * (band + 1) / bands); v++)
                {
                    int c = visit[v];
                    nearest[c] = finder.Find(cellMeans[c]);
                }
            });

            Array.Clear(sums);
            Array.Clear(weights);
            Array.Fill(opaque, true);
            for (int c = 0; c < cells.Length; c++)
            {
                int k = nearest[c];
                cells.AddSums(c, sums.AsSpan(k * Cells.Stride, Cells.Stride));
                weights[k] += cells.Weight[c];
                opaque[k] &= cells.Opaque[c];
            }

            if (round == Rounds)
            {
                break;
            }

            for (int k = 0; k < means.Length; k++)
            {
                if (weights[k] > 0)
                {
                    means[k] = Cells.MeanOf(sums.AsSpan(k * Cells.Stride, Cells.Stride), weights[k], space.Lanes);
                }
            }
        }

        return opaque;
    }

    // Whether the visible colours are few enough (65,536 at most) for each to be a cell of its own: icons and graphics
    // with antialiased edges have hundreds to thousands, blends of a few base colours, which cells of 64 levels merged.
    private static bool FewColours(uint[] argb)
    {
        var seen = new HashSet<uint>();
        uint last = 0;
        foreach (uint p in argb)
        {
            if (p < 0x01000000u || p == last)
            {
                continue;
            }

            last = p;
            if (seen.Add(p) && seen.Count > 65_536)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The exact nearest colour (the lowest index of equals) through a cache over a grid of the lanes in use: each grid
    /// cell keeps the colours that can be nearest to some point in it, filled as first met, by any thread, each the same
    /// whoever fills it. A cell's candidates are those within the distance of the colour nearest its centre plus twice
    /// the farthest a point in it lies from the centre (half a cell's width in each lane in use). Small grids live in an
    /// array; larger ones (six lanes) in a table of the cells met. Candidates are kept a lane at a time, so they are
    /// measured eight at a time.
    /// </summary>
    internal sealed class NearestColour
    {
        /// <summary>32 levels a lane: k-means, whose cache is cleared every round.</summary>
        public const int CoarseBits = 5;

        /// <summary>64 levels a lane: mapping three lanes, about half the candidates of the coarse grid.</summary>
        public const int FineBits = 6;

        private readonly Vector256<float>[] _colours;
        private readonly int _lanes;
        private readonly int _bits;
        private readonly float _reach;
        private readonly Cell?[]? _array;
        private readonly ConcurrentDictionary<int, Cell>? _table;
        private readonly Dictionary<int, Cell>? _local;
        private readonly ConcurrentQueue<int> _filled = new();

        // All the colours a lane at a time, as a cell keeps its own.
        private readonly float[] _channels;

        public NearestColour(Vector256<float>[] colours, int lanes, int bits, bool shared = true)
        {
            _colours = colours;
            _lanes = lanes;
            _bits = bits;
            float half = (256 >> bits) / 2f;
            _reach = (2 * half * MathF.Sqrt(lanes)) + 0.01f;
            if (lanes * bits <= 18)
            {
                _array = new Cell?[1 << (lanes * bits)];
            }
            else
            {
                // One thread (mapping) needs no locks: a plain table is a third faster to read.
                if (shared)
                {
                    _table = new ConcurrentDictionary<int, Cell>();
                }
                else
                {
                    _local = new Dictionary<int, Cell>();
                }
            }

            _channels = new float[Padded(colours.Length) * lanes];
            Load();
        }

        // Forgets the cells filled so far, for colours that have moved (the array is the same, its values changed).
        public void Reset()
        {
            if (_array is not null)
            {
                while (_filled.TryDequeue(out int key))
                {
                    _array[key] = null;
                }
            }
            else
            {
                _table?.Clear();
                _local?.Clear();
            }

            Load();
        }

        /// <summary>The grid cell's key for a point: its level in each lane in use, packed.</summary>
        public int KeyOf(Vector256<float> p)
        {
            Vector256<int> level = Vector256.Min(Vector256.Max(Vector256.ConvertToInt32(p) >> (8 - _bits), Vector256<int>.Zero), Vector256.Create((1 << _bits) - 1));
            int key = 0;
            for (int lane = 0; lane < _lanes; lane++)
            {
                key = (key << _bits) | level.GetElement(lane);
            }

            return key;
        }

        public int Find(Vector256<float> p)
        {
            int key = KeyOf(p);
            Cell cell = _array is not null ? _array[key] ?? FillArray(key)
                : _local is not null ? (_local.TryGetValue(key, out Cell? mine) ? mine : _local[key] = Fill(key))
                : _table!.TryGetValue(key, out Cell? found) ? found : FillTable(key);
            int[] index = cell.Index;
            if (index.Length == 1)
            {
                return index[0];
            }

            // One pass: each lane keeps the least distance it has seen and where, the first of equals; then the lowest
            // place among the lanes holding the overall least, which is the lowest colour index among equals.
            float[] channels = cell.Channels;
            int stride = channels.Length / _lanes;
            Vector256<float> least = Vector256.Create(float.MaxValue);
            Vector256<int> at = Vector256<int>.Zero;
            for (int k = 0; k < stride; k += Vector256<float>.Count)
            {
                Vector256<float> d = Distances(channels, stride, k, p, _lanes);
                Vector256<float> closer = Vector256.LessThan(d, least);
                least = Vector256.ConditionalSelect(closer, d, least);
                at = Vector256.ConditionalSelect(closer.AsInt32(), Vector256.Create(k) + Vector256<int>.Indices, at);
            }

            Vector256<int> places = Vector256.ConditionalSelect(Vector256.Equals(least, Vector256.Create(Least(least))).AsInt32(), at, Vector256.Create(int.MaxValue));
            Vector128<int> m = Vector128.Min(places.GetLower(), places.GetUpper());
            m = Vector128.Min(m, Vector128.Shuffle(m, Vector128.Create(2, 3, 0, 1)));
            m = Vector128.Min(m, Vector128.Shuffle(m, Vector128.Create(1, 0, 3, 2)));
            return index[m.ToScalar()];
        }

        private Cell FillArray(int key)
        {
            Cell cell = Fill(key);
            _array![key] = cell;
            _filled.Enqueue(key);
            return cell;
        }

        private Cell FillTable(int key) => _table!.GetOrAdd(key, Fill(key));

        // The cell's candidates, in index order.
        private Cell Fill(int key)
        {
            int width = 256 >> _bits;
            Span<float> centreLanes = stackalloc float[Vector256<float>.Count];
            for (int lane = _lanes - 1; lane >= 0; lane--)
            {
                centreLanes[lane] = ((key & ((1 << _bits) - 1)) * width) + (width / 2f);
                key >>= _bits;
            }

            Vector256<float> centre = Vector256.Create<float>(centreLanes);
            int stride = _channels.Length / _lanes;
            Span<float> distances = stackalloc float[stride];
            Vector256<float> least = Vector256.Create(float.MaxValue);
            for (int k = 0; k < stride; k += Vector256<float>.Count)
            {
                Vector256<float> d = Distances(_channels, stride, k, centre, _lanes);
                d.CopyTo(distances[k..]);
                least = Vector256.Min(least, d);
            }

            float radius = MathF.Sqrt(Least(least)) + _reach;
            Vector256<float> limit = Vector256.Create(radius * radius);
            Span<uint> masks = stackalloc uint[stride / Vector256<float>.Count];
            int count = 0;
            for (int m = 0; m < masks.Length; m++)
            {
                masks[m] = Vector256.LessThanOrEqual(Vector256.Create<float>(distances[(m * Vector256<float>.Count)..]), limit).ExtractMostSignificantBits();
                count += BitOperations.PopCount(masks[m]);
            }

            // Padding is never within the radius, so every bit set is a colour.
            int[] index = new int[count];
            count = 0;
            for (int m = 0; m < masks.Length; m++)
            {
                for (uint bits = masks[m]; bits != 0; bits &= bits - 1)
                {
                    index[count++] = (m * Vector256<float>.Count) + BitOperations.TrailingZeroCount(bits);
                }
            }

            int padded = Padded(count);
            float[] channels = new float[padded * _lanes];
            Array.Fill(channels, Padding);
            for (int i = 0; i < count; i++)
            {
                for (int lane = 0; lane < _lanes; lane++)
                {
                    channels[(lane * padded) + i] = _channels[(lane * stride) + index[i]];
                }
            }

            return new Cell(index, channels);
        }

        private void Load()
        {
            int stride = _channels.Length / _lanes;
            Array.Fill(_channels, Padding);
            for (int k = 0; k < _colours.Length; k++)
            {
                for (int lane = 0; lane < _lanes; lane++)
                {
                    _channels[(lane * stride) + k] = _colours[k].GetElement(lane);
                }
            }
        }

        // A whole number of vectors, at least one.
        private static int Padded(int count) => Math.Max((count + Vector256<float>.Count - 1) / Vector256<float>.Count, 1) * Vector256<float>.Count;

        private static float Least(Vector256<float> v)
        {
            Vector128<float> m = Vector128.Min(v.GetLower(), v.GetUpper());
            m = Vector128.Min(m, Vector128.Shuffle(m, Vector128.Create(2, 3, 0, 1)));
            m = Vector128.Min(m, Vector128.Shuffle(m, Vector128.Create(1, 0, 3, 2)));
            return m.ToScalar();
        }

        // The colours of a cell, its candidates and their lanes (each padded to a whole number of vectors).
        private sealed class Cell(int[] index, float[] channels)
        {
            public int[] Index { get; } = index;

            public float[] Channels { get; } = channels;
        }
    }

    // A colour far from every other, filling vectors past the last colour.
    private const float Padding = 1e6f;

    // The squared distances from p to eight colours, held a lane at a time from k (a lane every stride). The sums run in
    // a fixed order, so equal colours tie exactly.
    internal static Vector256<float> Distances(float[] channels, int stride, int k, Vector256<float> p, int lanes)
    {
        Vector256<float> d = Vector256.Create(channels, k) - Vector256.Create(p.GetElement(0));
        Vector256<float> sum = d * d;
        for (int lane = 1; lane < lanes; lane++)
        {
            d = Vector256.Create(channels, (lane * stride) + k) - Vector256.Create(p.GetElement(lane));
            sum += d * d;
        }

        return sum;
    }

    // Each pixel's palette index: its exact nearest colour (the lowest index of equals), with Floyd–Steinberg error
    // diffusion (alternate rows in opposite directions, the error carried in premultiplied sRGB) when dither is above 0,
    // passing on 60% of the error at full strength. Fully transparent pixels take the transparent index. Without
    // dithering, a pixel's index depends on its colour alone, so recent colours' indices are kept by a hash of the colour.
    private static byte[] Map(uint[] argb, int width, int height, Vector256<float>[] search, Vector128<float>[] colours, int transparentIndex, double dither, Space space)
    {
        var nearest = new NearestColour(search, space.Lanes, space.Lanes * NearestColour.FineBits <= 18 ? NearestColour.FineBits : NearestColour.CoarseBits, shared: false);
        byte[] indices = new byte[argb.Length];
        float strength = (float)(dither * DitherShare);
        if (strength == 0)
        {
            // A key of 0 is an empty place; a pixel that isn't fully transparent is never 0.
            const int Bits = 16;
            uint[] keys = new uint[1 << Bits];
            byte[] values = new byte[1 << Bits];
            for (int i = 0; i < argb.Length; i++)
            {
                uint p = argb[i];
                if (p < 0x01000000u && transparentIndex >= 0)
                {
                    indices[i] = (byte)transparentIndex;
                    continue;
                }

                int at = (int)((p * 0x9E3779B1u) >> (32 - Bits));
                if (keys[at] != p)
                {
                    (keys[at], values[at]) = (p, (byte)nearest.Find(space.FromArgb(p)));
                }

                indices[i] = values[at];
            }

            return indices;
        }

        var current = new Vector128<float>[width + 2];
        var next = new Vector128<float>[width + 2];
        Vector128<float> lastWanted = Vector128.Create(-1f);
        int lastIndex = 0;
        for (int y = 0; y < height; y++)
        {
            bool forward = (y & 1) == 0;
            int step = forward ? 1 : -1;
            for (int n = 0; n < width; n++)
            {
                int x = forward ? n : width - 1 - n;
                int i = (y * width) + x;
                uint p = argb[i];
                if (p < 0x01000000u && transparentIndex >= 0)
                {
                    indices[i] = (byte)transparentIndex;
                    continue;
                }

                Vector128<float> wanted = Vector128.Min(Vector128.Max(Premultiply(p) + current[x + 1], Vector128<float>.Zero), Vector128.Create(255f));

                // Premultiplied colour can't exceed its alpha.
                float a = wanted.GetElement(0);
                wanted = Vector128.Min(wanted, Vector128.Create(255f, a, a, a));

                // A flat area in a palette colour carries no error, so the same colour comes again and again.
                int index = Vector128.EqualsAll(wanted, lastWanted) ? lastIndex : nearest.Find(space.FromPremultiplied(wanted));
                (lastWanted, lastIndex) = (wanted, index);
                indices[i] = (byte)index;
                Vector128<float> error = (wanted - colours[index]) * strength;
                current[x + 1 + step] += error * (7f / 16);
                next[x + 1 - step] += error * (3f / 16);
                next[x + 1] += error * (5f / 16);
                next[x + 1 + step] += error * (1f / 16);
            }

            (current, next) = (next, current);
            Array.Clear(next);
        }

        return indices;
    }

    /// <summary>The occupied cells: count, sums of lanes and of squares, and whether every pixel is opaque.</summary>
    private sealed class Cells
    {
        /// <summary>Doubles a cell keeps: a sum for each of up to eight lanes, then the sum of squares.</summary>
        public const int Stride = 9;

        private double[] _sums = [];

        public long[] Weight { get; private set; } = [];

        public bool[] Opaque { get; private set; } = [];

        public int Length { get; private set; }

        // With exactCells, each distinct colour is a cell of its own, converted once; else colours share cells of 64
        // levels a lane, each converted as met (through a small cache of recent colours).
        public static Cells Count(uint[] argb, bool exactCells, Space space)
        {
            var map = new Dictionary<long, int>();
            var colourOf = new List<uint>();
            var weight = new List<long>();
            var opaque = new List<bool>();
            var sums = new List<double>();
            var converted = new LaneCache(space);
            uint last = 0;
            int lastCell = -1;
            Vector256<float> lastLanes = default;
            for (int i = 0; i < argb.Length;)
            {
                // A run of the same colour is added at once.
                uint p = argb[i];
                int run = 1;
                while (i + run < argb.Length && argb[i + run] == p)
                {
                    run++;
                }

                i += run;
                if (p < 0x01000000u)
                {
                    continue;
                }

                if (p != last || lastCell < 0)
                {
                    long key = p;
                    if (!exactCells)
                    {
                        lastLanes = converted.FromArgb(p);
                        key = 0;
                        for (int lane = 0; lane < space.Lanes; lane++)
                        {
                            key = (key << 6) | (uint)Math.Clamp((int)lastLanes.GetElement(lane) >> 2, 0, 63);
                        }
                    }

                    if (!map.TryGetValue(key, out int found))
                    {
                        found = map.Count;
                        map[key] = found;
                        colourOf.Add(p);
                        weight.Add(0);
                        opaque.Add(true);
                        for (int s = 0; s < Stride; s++)
                        {
                            sums.Add(0);
                        }
                    }

                    (last, lastCell) = (p, found);
                }

                weight[lastCell] += run;
                opaque[lastCell] &= p >= 0xFF000000u;
                if (!exactCells)
                {
                    Add(sums, lastCell, lastLanes, run);
                }
            }

            if (exactCells)
            {
                for (int cell = 0; cell < colourOf.Count; cell++)
                {
                    Add(sums, cell, space.FromArgb(colourOf[cell]), weight[cell]);
                }
            }

            return new Cells { _sums = [.. sums], Weight = [.. weight], Opaque = [.. opaque], Length = weight.Count };

            static void Add(List<double> sums, int cell, Vector256<float> lanes, long count)
            {
                int at = cell * Stride;
                double squares = 0;
                for (int lane = 0; lane < Vector256<float>.Count; lane++)
                {
                    double v = lanes.GetElement(lane);
                    sums[at + lane] += count * v;
                    squares += v * v;
                }

                sums[at + 8] += count * squares;
            }
        }

        public static Vector256<float> MeanOf(ReadOnlySpan<double> sum, long weight, int lanes)
        {
            Span<float> mean = stackalloc float[Vector256<float>.Count];
            for (int lane = 0; lane < lanes; lane++)
            {
                mean[lane] = (float)(sum[lane] / weight);
            }

            return Vector256.Create<float>(mean);
        }

        public void AddSums(int cell, Span<double> into)
        {
            ReadOnlySpan<double> from = _sums.AsSpan(cell * Stride, Stride);
            for (int s = 0; s < Stride; s++)
            {
                into[s] += from[s];
            }
        }

        public Vector256<float> Mean(int cell) => MeanOf(_sums.AsSpan(cell * Stride, Stride), Weight[cell], 8);

        // The squared error of the cells' pixels about their mean.
        public double Error(int[] order, int start, int end)
        {
            Span<double> sum = stackalloc double[Stride];
            double w = 0;
            for (int i = start; i < end; i++)
            {
                AddSums(order[i], sum);
                w += Weight[order[i]];
            }

            double mean = 0;
            for (int s = 0; s < 8; s++)
            {
                mean += sum[s] * sum[s];
            }

            return sum[8] - (mean / Math.Max(w, 1));
        }

        // The lane whose weighted variance across the cells is largest.
        public int WidestLane(int[] order, int start, int end)
        {
            Span<double> sum = stackalloc double[8];
            Span<double> squares = stackalloc double[8];
            double w = 0;
            for (int i = start; i < end; i++)
            {
                Vector256<float> mean = Mean(order[i]);
                long weight = Weight[order[i]];
                for (int s = 0; s < 8; s++)
                {
                    double m = mean.GetElement(s);
                    sum[s] += weight * m;
                    squares[s] += weight * m * m;
                }

                w += weight;
            }

            int widest = 0;
            double most = -1;
            for (int s = 0; s < 8; s++)
            {
                double variance = squares[s] - (sum[s] * sum[s] / w);
                if (variance > most)
                {
                    (most, widest) = (variance, s);
                }
            }

            return widest;
        }
    }

    /// <summary>Recent conversions to lanes, 4,096 places addressed by a hash of the colour: pictures repeat colours often, far apart.</summary>
    private sealed class LaneCache(Space space)
    {
        private const int Bits = 12;
        private readonly uint[] _keys = new uint[1 << Bits];
        private readonly Vector256<float>[] _values = new Vector256<float>[1 << Bits];

        public Vector256<float> FromArgb(uint argb)
        {
            // A key of 0 is an empty place; a colour that isn't fully transparent is never 0.
            int at = (int)((argb * 0x9E3779B1u) >> (32 - Bits));
            if (_keys[at] == argb)
            {
                return _values[at];
            }

            Vector256<float> lanes = space.FromArgb(argb);
            (_keys[at], _values[at]) = (argb, lanes);
            return lanes;
        }
    }
}
