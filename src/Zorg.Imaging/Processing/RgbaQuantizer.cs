using System;
using System.Collections.Generic;
using System.Runtime.Intrinsics;

namespace Zorg.Imaging.Processing;

/// <summary>
/// Reduces straight-alpha ARGB pixels to at most 256 colours with full alpha, for palette PNGs (a <c>tRNS</c> entry per
/// colour), the way pngquant-style tools do: colours are compared premultiplied, so a nearly transparent pixel's colour
/// matters little.
/// </summary>
/// <remarks>
/// <para>
/// The pixels are counted into cells, one for each distinct colour when there are at most 65,536 (icons and graphics with
/// antialiased edges gain 4 to 5 dB over grouping their colours), else of 64 levels a channel (alpha, and premultiplied
/// red, green and blue), each cell
/// keeping its count and sums. Median cut splits the cells into as many boxes as there are colours, always the box with
/// the largest squared error, across its widest channel at the weighted median; then a few rounds of k-means move each
/// colour to the mean of the cells nearest it. A colour whose pixels are all opaque stays exactly opaque. Fully
/// transparent pixels share one transparent entry. When any pixel is translucent, colours are compared by how they
/// differ over both black and white, so an error in alpha counts as much as a page shows it.
/// </para>
/// <para>
/// Pixels are matched to their exact nearest colour through a cache of the colours that can be nearest in each cell, with
/// optional Floyd–Steinberg dithering of the premultiplied values. Everything runs in a fixed order, so the result
/// doesn't depend on the number of threads.
/// </para>
/// </remarks>
internal static class RgbaQuantizer
{
    private const int Rounds = 4;

    /// <summary>The palette as straight ARGB, and an index per pixel.</summary>
    public readonly record struct Result(uint[] Palette, byte[] Indices);

    public static Result Run(uint[] argb, int width, int height, int maxColours, double dither)
    {
        bool anyTransparent = false;
        bool anyAlpha = false;
        var exact = new HashSet<uint>();
        foreach (uint p in argb)
        {
            uint key = p < 0x01000000u ? 0u : p;
            anyTransparent |= key == 0;
            anyAlpha |= p < 0xFF000000u;
            if (exact.Count <= maxColours)
            {
                exact.Add(key);
            }
        }

        if (exact.Count <= maxColours)
        {
            return Exact(argb, [.. exact]);
        }

        int colours = maxColours - (anyTransparent ? 1 : 0);
        Cells cells = Cells.Count(argb, exactCells: FewColours(argb));
        Vector128<float>[] means = MedianCut(cells, Math.Max(colours, 1));
        bool[] opaque = Refine(cells, means, anyAlpha);

        var palette = new List<uint>(means.Length + 1);
        var premultiplied = new List<Vector128<float>>(means.Length + 1);
        for (int k = 0; k < means.Length; k++)
        {
            uint colour = Straight(means[k], opaque[k]);
            palette.Add(colour);
            premultiplied.Add(Premultiply(colour));
        }

        int transparentIndex = -1;
        if (anyTransparent)
        {
            transparentIndex = palette.Count;
            palette.Add(0);
            premultiplied.Add(Vector128<float>.Zero);
        }

        byte[] indices = Map(argb, width, height, [.. premultiplied], transparentIndex, dither, anyAlpha);
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
            indices[i] = lookup[argb[i] < 0x01000000u ? 0u : argb[i]];
        }

        return new Result(palette, indices);
    }

    // A pixel as premultiplied (A, R, G, B) floats from 0 to 255.
    internal static Vector128<float> Premultiply(uint p)
    {
        float a = p >> 24;
        float scale = a / 255;
        return Vector128.Create(a, ((p >> 16) & 0xFF) * scale, ((p >> 8) & 0xFF) * scale, (p & 0xFF) * scale);
    }

    private static uint Straight(Vector128<float> mean, bool opaque)
    {
        float a = opaque ? 255 : Math.Clamp(MathF.Round(mean.GetElement(0)), 1, 255);
        uint Channel(int lane) => (uint)Math.Clamp(MathF.Round(mean.GetElement(lane) * 255 / Math.Max(mean.GetElement(0), 1e-3f)), 0, 255);
        return ((uint)a << 24) | (Channel(1) << 16) | (Channel(2) << 8) | Channel(3);
    }

    // The squared distance between premultiplied colours. With overPage, the difference as it shows over black plus as it
    // shows over white: over white a premultiplied colour adds 255 less its alpha to each channel, so the difference there
    // is the colour difference less the alpha difference. An error in alpha then counts as much as the page shows it,
    // where the plain distance counted it once whatever the colour. It is still a Euclidean distance (in six
    // dimensions), so means and the candidate bounds below hold.
    private static float Distance(Vector128<float> a, Vector128<float> b, bool overPage)
    {
        Vector128<float> d = a - b;
        if (!overPage)
        {
            return Vector128.Sum(d * d);
        }

        Vector128<float> colour = d.WithElement(0, 0f);
        Vector128<float> onWhite = colour - Vector128.Create(0f, d.GetElement(0), d.GetElement(0), d.GetElement(0));
        return Vector128.Sum(colour * colour) + Vector128.Sum(onWhite * onWhite);
    }

    // The boxes, split until there are enough or none can be split: each step takes the box with the largest squared
    // error and cuts its widest channel at the weighted median.
    private static Vector128<float>[] MedianCut(Cells cells, int colours)
    {
        int[] order = new int[cells.Length];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        // Means are 0 or more, so their float bits order as the values do.
        var cellMeans = new Vector128<float>[cells.Length];
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

        var means = new Vector128<float>[boxes.Count];
        for (int b = 0; b < boxes.Count; b++)
        {
            Vector128<double> sum = Vector128<double>.Zero;
            Vector128<double> sumHigh = Vector128<double>.Zero;
            long weight = 0;
            for (int i = boxes[b].Start; i < boxes[b].End; i++)
            {
                (Vector128<double> low, Vector128<double> high) = cells.Sum(order[i]);
                sum += low;
                sumHigh += high;
                weight += cells.Weight[order[i]];
            }

            means[b] = Narrow(sum / weight, sumHigh / weight);
        }

        return means;
    }

    // Rounds of k-means over the cells: each cell goes to its nearest colour, then each colour to the mean of its cells.
    // Returns, per colour, whether all its pixels are opaque.
    private static bool[] Refine(Cells cells, Vector128<float>[] means, bool overPage)
    {
        int[] nearest = new int[cells.Length];
        bool[] opaque = new bool[means.Length];
        var finder = new NearestColour(means, overPage);
        for (int round = 0; round <= Rounds; round++)
        {
            finder.Reset();
            Parallelism.For(Parallelism.Bands((long)cells.Length * means.Length / 16, 64), band =>
            {
                int bands = Parallelism.Bands((long)cells.Length * means.Length / 16, 64);
                for (int c = (int)((long)cells.Length * band / bands); c < (int)((long)cells.Length * (band + 1) / bands); c++)
                {
                    nearest[c] = finder.Find(cells.Mean(c));
                }
            });

            var sums = new Vector128<double>[means.Length * 2];
            long[] weights = new long[means.Length];
            Array.Fill(opaque, true);
            for (int c = 0; c < cells.Length; c++)
            {
                int k = nearest[c];
                (Vector128<double> low, Vector128<double> high) = cells.Sum(c);
                sums[2 * k] += low;
                sums[(2 * k) + 1] += high;
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
                    means[k] = Narrow(sums[2 * k] / weights[k], sums[(2 * k) + 1] / weights[k]);
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

    // The share of each pixel's error that full-strength dithering passes on: all of it left photos grainy, about 4 dB
    // further from the source, for hardly less banding. (Dithering less where the picture is flat scored better on PSNR
    // and SSIM but left visible bands in smooth gradients, so every pixel passes on the same share.)
    private const double DitherShare = 0.6;

    // The exact nearest colour (the lowest index of equals) through a cache of 32 levels a channel, which keeps for each
    // cell the colours that can be nearest to some value in it; cells are filled as first met, by any thread, each the
    // same whoever fills it.
    internal sealed class NearestColour(Vector128<float>[] colours, bool overPage)
    {
        private readonly int[]?[] _cache = new int[]?[1 << 20];
        private readonly System.Collections.Concurrent.ConcurrentQueue<int> _filled = new();

        // Forgets the cells filled so far, for colours that have moved (the array is the same, its values changed).
        public void Reset()
        {
            while (_filled.TryDequeue(out int key))
            {
                _cache[key] = null;
            }
        }

        public int Find(Vector128<float> p)
        {
            Vector128<int> level = Vector128.Min(Vector128.Max(Vector128.ConvertToInt32(p) >> 3, Vector128<int>.Zero), Vector128.Create(31));
            int key = (level.GetElement(0) << 15) | (level.GetElement(1) << 10) | (level.GetElement(2) << 5) | level.GetElement(3);
            int[]? candidates = _cache[key];
            if (candidates is null)
            {
                candidates = Candidates(colours, Vector128.ConvertToSingle((level << 3) + Vector128.Create(4)), overPage);
                _cache[key] = candidates;
                _filled.Enqueue(key);
            }

            if (candidates.Length == 1)
            {
                return candidates[0];
            }

            int best = candidates[0];
            float least = float.MaxValue;
            foreach (int k in candidates)
            {
                float d = Distance(colours[k], p, overPage);
                if (d < least)
                {
                    (least, best) = (d, k);
                }
            }

            return best;
        }
    }

    // The colours, in index order, that can be nearest to some value within 4 a channel of the centre.
    private static int[] Candidates(Vector128<float>[] palette, Vector128<float> centre, bool overPage)
    {
        float nearest = Distance(palette[Nearest(palette, centre, overPage)], centre, overPage);
        float radius = MathF.Sqrt(nearest) + (overPage ? Reach : 16.01f);
        var list = new List<int>(4);
        for (int k = 0; k < palette.Length; k++)
        {
            if (Distance(palette[k], centre, overPage) <= radius * radius)
            {
                list.Add(k);
            }
        }

        return [.. list];
    }

    // Twice the farthest a value in a cell lies from its centre under the over-the-page distance: up to 4 in each channel
    // and in alpha makes up to 4 over black and 8 over white in each of three channels, sqrt(3 x 4^2 + 3 x 8^2), about 15.5.
    private const float Reach = 31.01f;

    private static int Nearest(Vector128<float>[] palette, Vector128<float> colour, bool overPage)
    {
        int best = 0;
        float least = float.MaxValue;
        for (int k = 0; k < palette.Length; k++)
        {
            float d = Distance(palette[k], colour, overPage);
            if (d < least)
            {
                (least, best) = (d, k);
            }
        }

        return best;
    }

    private static Vector128<float> Narrow(Vector128<double> low, Vector128<double> high) =>
        Vector128.Create((float)low.GetElement(0), (float)low.GetElement(1), (float)high.GetElement(0), (float)high.GetElement(1));

    // Each pixel's palette index: its exact nearest premultiplied colour (the lowest index of equals), with
    // Floyd–Steinberg error diffusion (alternate rows in opposite directions) when dither is above 0, passing on 60% of
    // the error at full strength. Fully transparent pixels take the transparent index. A cache of 32 levels a channel
    // keeps, for each cell, the colours that can be nearest to some value in it: those within the distance of the colour
    // nearest the cell's centre plus twice the farthest a value lies from the centre (4 a channel, so 8 in all).
    private static byte[] Map(uint[] argb, int width, int height, Vector128<float>[] palette, int transparentIndex, double dither, bool overPage)
    {
        Vector128<float>[] colours = transparentIndex >= 0 ? palette[..transparentIndex] : palette;
        var nearest = new NearestColour(colours, overPage);
        int Find(Vector128<float> p) => nearest.Find(p);

        byte[] indices = new byte[argb.Length];
        float strength = (float)(dither * DitherShare);
        var current = new Vector128<float>[width + 2];
        var next = new Vector128<float>[width + 2];
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

                Vector128<float> wanted = Premultiply(p);
                if (strength > 0)
                {
                    wanted = Vector128.Min(Vector128.Max(wanted + current[x + 1], Vector128<float>.Zero), Vector128.Create(255f));

                    // Premultiplied colour can't exceed its alpha.
                    float a = wanted.GetElement(0);
                    wanted = Vector128.Min(wanted, Vector128.Create(255f, a, a, a));
                }

                int index = Find(wanted);
                indices[i] = (byte)index;
                if (strength > 0)
                {
                    Vector128<float> error = (wanted - colours[index]) * strength;
                    current[x + 1 + step] += error * (7f / 16);
                    next[x + 1 - step] += error * (3f / 16);
                    next[x + 1] += error * (5f / 16);
                    next[x + 1 + step] += error * (1f / 16);
                }
            }

            (current, next) = (next, current);
            Array.Clear(next);
        }

        return indices;
    }

    /// <summary>The occupied cells: count, premultiplied sums and sums of squares, and whether every pixel is opaque.</summary>
    private sealed class Cells
    {
        private readonly List<double> _sums = [];

        public long[] Weight { get; private set; } = [];

        public bool[] Opaque { get; private set; } = [];

        public int Length { get; private set; }

        // With exactCells, each distinct colour is a cell of its own; else colours share cells of 64 levels a channel.
        public static Cells Count(uint[] argb, bool exactCells)
        {
            var cells = new Cells();
            var map = new Dictionary<int, int>();
            var weight = new List<long>();
            var opaque = new List<bool>();
            uint last = 0;
            int lastCell = -1;
            foreach (uint p in argb)
            {
                if (p < 0x01000000u)
                {
                    continue;
                }

                int cell;
                if (p == last && lastCell >= 0)
                {
                    cell = lastCell;
                }
                else
                {
                    int key;
                    if (exactCells)
                    {
                        key = (int)p;
                    }
                    else
                    {
                        Vector128<float> v = Premultiply(p);
                        Vector128<int> level = Vector128.ConvertToInt32(v) >> 2;
                        key = (level.GetElement(0) << 18) | (level.GetElement(1) << 12) | (level.GetElement(2) << 6) | level.GetElement(3);
                    }

                    if (!map.TryGetValue(key, out cell))
                    {
                        cell = map.Count;
                        map[key] = cell;
                        weight.Add(0);
                        opaque.Add(true);
                        for (int s = 0; s < 5; s++)
                        {
                            cells._sums.Add(0);
                        }
                    }

                    (last, lastCell) = (p, cell);
                }

                Vector128<float> c = Premultiply(p);
                weight[cell]++;
                opaque[cell] &= p >= 0xFF000000u;
                int at = cell * 5;
                cells._sums[at] += c.GetElement(0);
                cells._sums[at + 1] += c.GetElement(1);
                cells._sums[at + 2] += c.GetElement(2);
                cells._sums[at + 3] += c.GetElement(3);
                cells._sums[at + 4] += Vector128.Sum(c * c);
            }

            cells.Weight = [.. weight];
            cells.Opaque = [.. opaque];
            cells.Length = weight.Count;
            return cells;
        }

        public (Vector128<double> Low, Vector128<double> High) Sum(int cell)
        {
            int at = cell * 5;
            return (Vector128.Create(_sums[at], _sums[at + 1]), Vector128.Create(_sums[at + 2], _sums[at + 3]));
        }

        public Vector128<float> Mean(int cell)
        {
            int at = cell * 5;
            double w = Weight[cell];
            return Vector128.Create((float)(_sums[at] / w), (float)(_sums[at + 1] / w), (float)(_sums[at + 2] / w), (float)(_sums[at + 3] / w));
        }

        // The squared error of the cells' pixels about their mean.
        public double Error(int[] order, int start, int end)
        {
            double squares = 0;
            double w = 0;
            Span<double> sum = stackalloc double[4];
            for (int i = start; i < end; i++)
            {
                int at = order[i] * 5;
                for (int s = 0; s < 4; s++)
                {
                    sum[s] += _sums[at + s];
                }

                squares += _sums[at + 4];
                w += Weight[order[i]];
            }

            double mean = 0;
            for (int s = 0; s < 4; s++)
            {
                mean += sum[s] * sum[s];
            }

            return squares - (mean / Math.Max(w, 1));
        }

        // The channel whose weighted variance across the cells is largest.
        public int WidestLane(int[] order, int start, int end)
        {
            Span<double> sum = stackalloc double[4];
            Span<double> squares = stackalloc double[4];
            double w = 0;
            for (int i = start; i < end; i++)
            {
                Vector128<float> mean = Mean(order[i]);
                long weight = Weight[order[i]];
                for (int s = 0; s < 4; s++)
                {
                    double m = mean.GetElement(s);
                    sum[s] += weight * m;
                    squares[s] += weight * m * m;
                }

                w += weight;
            }

            int widest = 0;
            double most = -1;
            for (int s = 0; s < 4; s++)
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
}
