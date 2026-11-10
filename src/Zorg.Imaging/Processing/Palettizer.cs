using System;

namespace Zorg.Imaging.Processing;

/// <summary>
/// Reduces ARGB pixels to at most 256 colours and an index per pixel, for GIF: exactly when the image has few enough
/// colours, otherwise with the quantiser of lossy PNG (<see cref="RgbaQuantizer"/>) on alpha cut to one bit.
/// </summary>
/// <remarks>
/// Alpha is one bit: a pixel with alpha below 128 takes the transparent index, any other is opaque at its colour. GIF
/// had its own quantiser (Wu's, refined by k-means); the RGBA one was as good or better on every test image and much
/// better on screenshots and icons (5 dB on one game screenshot), so both formats now share it.
/// </remarks>
internal static class Palettizer
{
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
        if (exact is null)
        {
            uint[] cut = new uint[argb.Length];
            for (int i = 0; i < argb.Length; i++)
            {
                cut[i] = argb[i] < 0x80000000u ? 0u : argb[i] | 0xFF000000u;
            }

            RgbaQuantizer.Result reduced = RgbaQuantizer.Run(cut, width, height, maxColours, dither);
            return new Result(reduced.Palette, reduced.Indices, Array.FindIndex(reduced.Palette, c => c < 0x01000000u));
        }

        int transparentIndex = transparent ? exact.Length : -1;
        byte[] indices = new byte[argb.Length];
        MapExact(argb, exact, transparentIndex, indices, width, height);
        uint[] palette = exact;
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
