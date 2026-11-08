using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Zorg.Imaging.Processing;

/// <summary>
/// Reduces ARGB pixels to at most 256 colours and an index per pixel, for GIF: exactly when the image has few enough
/// colours, otherwise with Wu's colour quantiser and optional Floyd–Steinberg dithering.
/// </summary>
/// <remarks>
/// <para>
/// Alpha is one bit: a pixel with alpha below 128 takes the transparent index, any other is opaque at its colour.
/// </para>
/// <para>
/// Wu's quantiser (Xiaolin Wu, "Efficient Statistical Computations for Optimal Color Quantization", Graphics Gems II,
/// 1991) counts the colours in a 32 x 32 x 32 histogram, keeps cumulative moments so any box's count, sum and sum of
/// squares come from eight lookups, and splits the box of largest variance where the two halves' variance falls
/// most, until there are enough boxes. Each box's mean is a palette colour.
/// </para>
/// <para>
/// Pixels are matched to the nearest palette colour through a cache of 64 levels a channel, filled as cells are first
/// met with the colour nearest the cell's centre, so the result is the same on any number of threads.
/// </para>
/// </remarks>
internal static class Palettizer
{
    private const int Side = 33;

    /// <summary>The palette, opaque ARGB; an index per pixel; and the transparent index, or -1.</summary>
    public readonly record struct Result(uint[] Palette, byte[] Indices, int Transparent);

    public static Result Run(uint[] argb, int width, int height, int maxColours, double dither)
    {
        bool transparent = false;
        foreach (uint p in argb)
        {
            if (p < 0x80000000u)
            {
                transparent = true;
                break;
            }
        }

        int colours = maxColours - (transparent ? 1 : 0);
        uint[]? exact = Distinct(argb, colours);
        uint[] palette = exact ?? Wu(argb, Math.Max(colours, 1));
        int transparentIndex = transparent ? palette.Length : -1;
        byte[] indices = new byte[argb.Length];
        if (exact is not null)
        {
            MapExact(argb, exact, transparentIndex, indices, width, height);
        }
        else
        {
            var nearest = new Nearest(palette);
            if (argb.Length >= 1 << 18 && Parallelism.Degree > 1)
            {
                // As many pixels as cells: worth filling every cell at once, on every core, rather than one by one.
                nearest.FillAll();
            }

            if (dither > 0)
            {
                Dither(argb, width, height, nearest, palette, transparentIndex, dither, indices);
            }
            else
            {
                MapNearest(argb, width, height, nearest, transparentIndex, indices);
            }
        }

        if (transparent)
        {
            Array.Resize(ref palette, palette.Length + 1);
        }

        return new Result(palette, indices, transparentIndex);
    }

    // The opaque colours, sorted, when there are at most `limit`; otherwise null.
    private static uint[]? Distinct(uint[] argb, int limit)
    {
        var set = new ColourSet(limit);
        uint last = 0xFFFFFFFFu;
        bool any = false;
        foreach (uint p in argb)
        {
            if (p < 0x80000000u)
            {
                continue;
            }

            uint rgb = p & 0xFFFFFFu;
            if (any && rgb == last)
            {
                continue;
            }

            (last, any) = (rgb, true);
            if (!set.Add(rgb))
            {
                return null;
            }
        }

        uint[] colours = set.ToArray();
        Array.Sort(colours);
        for (int i = 0; i < colours.Length; i++)
        {
            colours[i] |= 0xFF000000u;
        }

        return colours;
    }

    private static void MapExact(uint[] argb, uint[] palette, int transparentIndex, byte[] indices, int width, int height)
    {
        var set = new ColourSet(palette.Length);
        for (int i = 0; i < palette.Length; i++)
        {
            set.Add(palette[i] & 0xFFFFFFu, i);
        }

        int bands = Parallelism.Bands((long)width * height, height);
        Parallelism.For(bands, band =>
        {
            int from = (int)((long)height * band / bands);
            int to = (int)((long)height * (band + 1) / bands);
            for (int i = from * width; i < to * width; i++)
            {
                uint p = argb[i];
                indices[i] = p < 0x80000000u ? (byte)transparentIndex : (byte)set.IndexOf(p & 0xFFFFFFu);
            }
        });
    }

    private static void MapNearest(uint[] argb, int width, int height, Nearest nearest, int transparentIndex, byte[] indices)
    {
        int bands = Parallelism.Bands((long)width * height, height);
        Parallelism.For(bands, band =>
        {
            int from = (int)((long)height * band / bands);
            int to = (int)((long)height * (band + 1) / bands);
            for (int i = from * width; i < to * width; i++)
            {
                uint p = argb[i];
                indices[i] = p < 0x80000000u ? (byte)transparentIndex : nearest.Find((int)(p >> 16) & 0xFF, (int)(p >> 8) & 0xFF, (int)p & 0xFF);
            }
        });
    }

    // Floyd–Steinberg error diffusion, alternate rows in opposite directions so the error doesn't drift one way. The
    // error is in 1/4096ths, scaled by the strength; transparent pixels take none. Each row's error is kept per pixel
    // as three ints, with a pixel of room at each end so the edges need no tests.
    private static void Dither(uint[] argb, int width, int height, Nearest nearest, uint[] palette, int transparentIndex, double strength, byte[] indices)
    {
        int scale = (int)Math.Round(strength * 256);
        int[] currentRow = new int[(width + 2) * 3];
        int[] nextRow = new int[(width + 2) * 3];
        ref uint colours = ref MemoryMarshal.GetArrayDataReference(palette);
        for (int y = 0; y < height; y++)
        {
            bool forward = (y & 1) == 0;
            int step = forward ? 3 : -3;
            ref uint source = ref argb[y * width];
            ref byte target = ref indices[y * width];
            ref int current = ref MemoryMarshal.GetArrayDataReference(currentRow);
            ref int next = ref MemoryMarshal.GetArrayDataReference(nextRow);
            for (int n = 0; n < width; n++)
            {
                int x = forward ? n : width - 1 - n;
                uint p = Unsafe.Add(ref source, x);
                int at = (x + 1) * 3;
                if (p < 0x80000000u)
                {
                    Unsafe.Add(ref target, x) = (byte)transparentIndex;
                    continue;
                }

                int r = Math.Clamp((int)((p >> 16) & 0xFF) + ((Unsafe.Add(ref current, at) + 2048) >> 12), 0, 255);
                int g = Math.Clamp((int)((p >> 8) & 0xFF) + ((Unsafe.Add(ref current, at + 1) + 2048) >> 12), 0, 255);
                int b = Math.Clamp((int)(p & 0xFF) + ((Unsafe.Add(ref current, at + 2) + 2048) >> 12), 0, 255);
                byte index = nearest.Find(r, g, b);
                Unsafe.Add(ref target, x) = index;
                uint chosen = Unsafe.Add(ref colours, index);
                int er = (r - (int)((chosen >> 16) & 0xFF)) * scale;
                int eg = (g - (int)((chosen >> 8) & 0xFF)) * scale;
                int eb = (b - (int)(chosen & 0xFF)) * scale;

                // 7/16 ahead; on the next row 3/16 behind, 5/16 below and 1/16 ahead.
                ref int ahead = ref Unsafe.Add(ref current, at + step);
                ahead += er * 7;
                Unsafe.Add(ref ahead, 1) += eg * 7;
                Unsafe.Add(ref ahead, 2) += eb * 7;
                ref int below = ref Unsafe.Add(ref next, at);
                below += er * 5;
                Unsafe.Add(ref below, 1) += eg * 5;
                Unsafe.Add(ref below, 2) += eb * 5;
                ref int behind = ref Unsafe.Add(ref next, at - step);
                behind += er * 3;
                Unsafe.Add(ref behind, 1) += eg * 3;
                Unsafe.Add(ref behind, 2) += eb * 3;
                ref int diagonal = ref Unsafe.Add(ref next, at + step);
                diagonal += er;
                Unsafe.Add(ref diagonal, 1) += eg;
                Unsafe.Add(ref diagonal, 2) += eb;
            }

            (currentRow, nextRow) = (nextRow, currentRow);
            Array.Clear(nextRow);
        }
    }

    private static uint[] Wu(uint[] argb, int colours)
    {
        var moments = new Moments();
        moments.Count(argb);
        moments.Accumulate();

        var boxes = new Box[colours];
        double[] variance = new double[colours];
        boxes[0] = new Box(0, Side - 1, 0, Side - 1, 0, Side - 1);
        variance[0] = moments.Variance(boxes[0]);
        int count = 1;
        while (count < colours)
        {
            int widest = -1;
            double most = 0;
            for (int k = 0; k < count; k++)
            {
                if (variance[k] > most)
                {
                    (most, widest) = (variance[k], k);
                }
            }

            if (widest < 0 || !moments.Cut(ref boxes[widest], out boxes[count]))
            {
                // Nothing left worth cutting: mark it so and look again, or stop when nothing can be cut.
                if (widest < 0)
                {
                    break;
                }

                variance[widest] = 0;
                continue;
            }

            variance[widest] = moments.Variance(boxes[widest]);
            variance[count] = moments.Variance(boxes[count]);
            count++;
        }

        var palette = new uint[count];
        int used = 0;
        for (int k = 0; k < count; k++)
        {
            long weight = Moments.Volume(boxes[k], moments.Weight);
            if (weight == 0)
            {
                continue;
            }

            uint r = (uint)((Moments.Volume(boxes[k], moments.Red) + (weight / 2)) / weight);
            uint g = (uint)((Moments.Volume(boxes[k], moments.Green) + (weight / 2)) / weight);
            uint b = (uint)((Moments.Volume(boxes[k], moments.Blue) + (weight / 2)) / weight);
            palette[used++] = 0xFF000000u | (r << 16) | (g << 8) | b;
        }

        return palette[..Math.Max(used, 1)];
    }

    // A box in the histogram: the cells with r0 < r <= r1, and so on.
    private record struct Box(int R0, int R1, int G0, int G1, int B0, int B1)
    {
        public readonly int Cells => (R1 - R0) * (G1 - G0) * (B1 - B0);
    }

    // Histogram counts and colour sums per cell, then made cumulative so a box's totals take eight lookups.
    private sealed class Moments
    {
        public readonly long[] Weight = new long[Side * Side * Side];
        public readonly long[] Red = new long[Side * Side * Side];
        public readonly long[] Green = new long[Side * Side * Side];
        public readonly long[] Blue = new long[Side * Side * Side];
        public readonly double[] Squares = new double[Side * Side * Side];

        private static int At(int r, int g, int b) => (((r * Side) + g) * Side) + b;

        public void Count(uint[] argb)
        {
            int bands = Parallelism.Bands(argb.Length, 16);
            var partial = new long[bands][];
            Parallelism.For(bands, band =>
            {
                // Per cell: count, then the three sums and the sum of squares, gathered apart and merged after.
                long[] mine = new long[Side * Side * Side * 5];
                int from = (int)((long)argb.Length * band / bands);
                int to = (int)((long)argb.Length * (band + 1) / bands);
                for (int i = from; i < to; i++)
                {
                    uint p = argb[i];
                    if (p < 0x80000000u)
                    {
                        continue;
                    }

                    int r = (int)((p >> 16) & 0xFF);
                    int g = (int)((p >> 8) & 0xFF);
                    int b = (int)(p & 0xFF);
                    int cell = At((r >> 3) + 1, (g >> 3) + 1, (b >> 3) + 1) * 5;
                    mine[cell]++;
                    mine[cell + 1] += r;
                    mine[cell + 2] += g;
                    mine[cell + 3] += b;
                    mine[cell + 4] += (r * r) + (g * g) + (b * b);
                }

                partial[band] = mine;
            });

            foreach (long[] mine in partial)
            {
                for (int cell = 0; cell < Weight.Length; cell++)
                {
                    Weight[cell] += mine[cell * 5];
                    Red[cell] += mine[(cell * 5) + 1];
                    Green[cell] += mine[(cell * 5) + 2];
                    Blue[cell] += mine[(cell * 5) + 3];
                    Squares[cell] += mine[(cell * 5) + 4];
                }
            }
        }

        public void Accumulate()
        {
            Accumulate(Weight);
            Accumulate(Red);
            Accumulate(Green);
            Accumulate(Blue);
            for (int axis = 0; axis < 3; axis++)
            {
                for (int r = 1; r < Side; r++)
                {
                    for (int g = 1; g < Side; g++)
                    {
                        for (int b = 1; b < Side; b++)
                        {
                            int here = At(r, g, b);
                            Squares[here] += Squares[axis == 0 ? At(r - 1, g, b) : axis == 1 ? At(r, g - 1, b) : At(r, g, b - 1)];
                        }
                    }
                }
            }
        }

        private static void Accumulate(long[] m)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                for (int r = 1; r < Side; r++)
                {
                    for (int g = 1; g < Side; g++)
                    {
                        for (int b = 1; b < Side; b++)
                        {
                            m[At(r, g, b)] += m[axis == 0 ? At(r - 1, g, b) : axis == 1 ? At(r, g - 1, b) : At(r, g, b - 1)];
                        }
                    }
                }
            }
        }

        public static long Volume(Box x, long[] m) =>
            m[At(x.R1, x.G1, x.B1)] - m[At(x.R1, x.G1, x.B0)] - m[At(x.R1, x.G0, x.B1)] + m[At(x.R1, x.G0, x.B0)]
            - m[At(x.R0, x.G1, x.B1)] + m[At(x.R0, x.G1, x.B0)] + m[At(x.R0, x.G0, x.B1)] - m[At(x.R0, x.G0, x.B0)];

        private static double Volume(Box x, double[] m) =>
            m[At(x.R1, x.G1, x.B1)] - m[At(x.R1, x.G1, x.B0)] - m[At(x.R1, x.G0, x.B1)] + m[At(x.R1, x.G0, x.B0)]
            - m[At(x.R0, x.G1, x.B1)] + m[At(x.R0, x.G1, x.B0)] + m[At(x.R0, x.G0, x.B1)] - m[At(x.R0, x.G0, x.B0)];

        // The sum of squared distances from the box's mean.
        public double Variance(Box x)
        {
            long weight = Volume(x, Weight);
            if (weight == 0 || x.Cells <= 1)
            {
                return 0;
            }

            double r = Volume(x, Red);
            double g = Volume(x, Green);
            double b = Volume(x, Blue);
            return Volume(x, Squares) - (((r * r) + (g * g) + (b * b)) / weight);
        }

        // Splits the box where the two halves' squared sums over their weights are largest, which is where the
        // variance left falls most. The box keeps the lower half and `upper` gets the rest.
        public bool Cut(ref Box box, out Box upper)
        {
            long weight = Volume(box, Weight);
            long red = Volume(box, Red);
            long green = Volume(box, Green);
            long blue = Volume(box, Blue);
            double best = -1;
            int bestAxis = -1;
            int bestAt = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                (int low, int high) = axis switch { 0 => (box.R0, box.R1), 1 => (box.G0, box.G1), _ => (box.B0, box.B1) };
                for (int at = low + 1; at < high; at++)
                {
                    Box half = axis switch { 0 => box with { R1 = at }, 1 => box with { G1 = at }, _ => box with { B1 = at } };
                    long w = Volume(half, Weight);
                    if (w == 0 || w == weight)
                    {
                        continue;
                    }

                    double r = Volume(half, Red);
                    double g = Volume(half, Green);
                    double b = Volume(half, Blue);
                    double score = (((r * r) + (g * g) + (b * b)) / w)
                        + ((((red - r) * (red - r)) + ((green - g) * (green - g)) + ((blue - b) * (blue - b))) / (weight - w));
                    if (score > best)
                    {
                        (best, bestAxis, bestAt) = (score, axis, at);
                    }
                }
            }

            if (bestAxis < 0)
            {
                upper = default;
                return false;
            }

            (box, upper) = bestAxis switch
            {
                0 => (box with { R1 = bestAt }, box with { R0 = bestAt }),
                1 => (box with { G1 = bestAt }, box with { G0 = bestAt }),
                _ => (box with { B1 = bestAt }, box with { B0 = bestAt }),
            };
            return true;
        }
    }

    // The nearest palette colour, cached per cell of 4 x 4 x 4 values, each filled with the colour nearest its centre
    // (the lowest index of equals). The search goes out both ways from the cell's green in the palette sorted by green,
    // and stops each way once green alone is further than the best so far.
    internal sealed class Nearest
    {
        private readonly ushort[] _cache = new ushort[1 << 18];
        private readonly int[] _byGreen;
        private readonly int[] _red;
        private readonly int[] _green;
        private readonly int[] _blue;

        public Nearest(uint[] palette)
        {
            int count = palette.Length;
            (_red, _green, _blue) = (new int[count], new int[count], new int[count]);
            long[] keys = new long[count];
            for (int i = 0; i < count; i++)
            {
                uint p = palette[i];
                keys[i] = ((long)((p >> 8) & 0xFF) << 16) | (uint)i;
            }

            Array.Sort(keys);
            _byGreen = new int[count];
            for (int k = 0; k < count; k++)
            {
                int i = (int)(keys[k] & 0xFFFF);
                uint p = palette[i];
                _byGreen[k] = i;
                (_red[k], _green[k], _blue[k]) = ((int)((p >> 16) & 0xFF), (int)((p >> 8) & 0xFF), (int)(p & 0xFF));
            }
        }

        public byte Find(int r, int g, int b)
        {
            int cell = ((r >> 2) << 12) | ((g >> 2) << 6) | (b >> 2);
            int known = Volatile.Read(ref _cache[cell]);
            return known != 0 ? (byte)(known - 1) : Fill(cell);
        }

        public void FillAll()
        {
            int bands = Parallelism.Bands(1 << 20, 64);
            Parallelism.For(bands, band =>
            {
                for (int cell = (1 << 18) * band / bands; cell < (1 << 18) * (band + 1) / bands; cell++)
                {
                    Fill(cell);
                }
            });
        }

        private byte Fill(int cell)
        {
            int cr = ((cell >> 12) << 2) + 2;
            int cg = (((cell >> 6) & 63) << 2) + 2;
            int cb = ((cell & 63) << 2) + 2;
            int count = _byGreen.Length;
            int up = 0;
            while (up < count && _green[up] < cg)
            {
                up++;
            }

            int down = up - 1;
            int best = int.MaxValue;
            int bestDistance = int.MaxValue;
            while (true)
            {
                bool any = false;
                if (up < count)
                {
                    int dg = _green[up] - cg;
                    if (dg * dg <= bestDistance)
                    {
                        Consider(up++, cr, cg, cb, ref best, ref bestDistance);
                        any = true;
                    }
                    else
                    {
                        up = count;
                    }
                }

                if (down >= 0)
                {
                    int dg = cg - _green[down];
                    if (dg * dg <= bestDistance)
                    {
                        Consider(down--, cr, cg, cb, ref best, ref bestDistance);
                        any = true;
                    }
                    else
                    {
                        down = -1;
                    }
                }

                if (!any)
                {
                    break;
                }
            }

            Volatile.Write(ref _cache[cell], (ushort)(best + 1));
            return (byte)best;
        }

        private void Consider(int k, int cr, int cg, int cb, ref int best, ref int bestDistance)
        {
            int dr = cr - _red[k];
            int dg = cg - _green[k];
            int db = cb - _blue[k];
            int distance = (dr * dr) + (dg * dg) + (db * db);
            int index = _byGreen[k];
            if (distance < bestDistance || (distance == bestDistance && index < best))
            {
                (bestDistance, best) = (distance, index);
            }
        }
    }

    // A small open-addressed set of 24-bit colours, each with an index, refusing more than its limit.
    private sealed class ColourSet
    {
        private readonly uint[] _keys;
        private readonly int[] _values;
        private readonly int _limit;
        private readonly int _mask;
        private int _count;

        public ColourSet(int limit)
        {
            _limit = limit;
            int size = 16;
            while (size < limit * 4)
            {
                size <<= 1;
            }

            _keys = new uint[size];
            _values = new int[size];
            _mask = size - 1;
            Array.Fill(_keys, 0xFFFFFFFFu);
        }

        public bool Add(uint rgb, int value = 0)
        {
            int at = Slot(rgb);
            if (_keys[at] == rgb)
            {
                return true;
            }

            if (_count == _limit)
            {
                return false;
            }

            _keys[at] = rgb;
            _values[at] = value;
            _count++;
            return true;
        }

        public int IndexOf(uint rgb) => _values[Slot(rgb)];

        public uint[] ToArray()
        {
            var result = new uint[_count];
            int n = 0;
            foreach (uint key in _keys)
            {
                if (key != 0xFFFFFFFFu)
                {
                    result[n++] = key;
                }
            }

            return result;
        }

        private int Slot(uint rgb)
        {
            int at = (int)((rgb * 0x9E3779B1u) >> 16) & _mask;
            while (_keys[at] != 0xFFFFFFFFu && _keys[at] != rgb)
            {
                at = (at + 1) & _mask;
            }

            return at;
        }
    }
}
