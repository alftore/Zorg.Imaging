using System;
using System.Collections.Generic;
using System.Globalization;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Codecs.Webp;

/// <summary>Decodes a WebP lossless (VP8L) bit stream into ARGB pixels (RFC 9649).</summary>
/// <remarks>
/// Pixels are 32-bit ARGB, alpha in the top byte, as the format defines them. The four transforms are undone in the
/// reverse of the order they were read; backward references copy pixels already decoded (LZ77 with the 120 short 2-D
/// distances first); the colour cache holds recently used colours by hash.
/// </remarks>
internal static class Vp8lDecoder
{
    private const int Signature = 0x2F;
    private const int GreenAlphabet = 256 + 24;
    private const int DistanceAlphabet = 40;

    // Section 4.2.2: the distance codes 1 to 120 as (x, y) offsets, nearest first.
    private static ReadOnlySpan<sbyte> DistanceMap =>
    [
        0, 1, 1, 0, 1, 1, -1, 1, 0, 2, 2, 0, 1, 2, -1, 2,
        2, 1, -2, 1, 2, 2, -2, 2, 0, 3, 3, 0, 1, 3, -1, 3,
        3, 1, -3, 1, 2, 3, -2, 3, 3, 2, -3, 2, 0, 4, 4, 0,
        1, 4, -1, 4, 4, 1, -4, 1, 3, 3, -3, 3, 2, 4, -2, 4,
        4, 2, -4, 2, 0, 5, 3, 4, -3, 4, 4, 3, -4, 3, 5, 0,
        1, 5, -1, 5, 5, 1, -5, 1, 2, 5, -2, 5, 5, 2, -5, 2,
        4, 4, -4, 4, 3, 5, -3, 5, 5, 3, -5, 3, 0, 6, 6, 0,
        1, 6, -1, 6, 6, 1, -6, 1, 2, 6, -2, 6, 6, 2, -6, 2,
        4, 5, -4, 5, 5, 4, -5, 4, 3, 6, -3, 6, 6, 3, -6, 3,
        0, 7, 7, 0, 1, 7, -1, 7, 5, 5, -5, 5, 7, 1, -7, 1,
        4, 6, -4, 6, 6, 4, -6, 4, 2, 7, -2, 7, 7, 2, -7, 2,
        3, 7, -3, 7, 7, 3, -7, 3, 5, 6, -5, 6, 6, 5, -6, 5,
        8, 0, 4, 7, -4, 7, 7, 4, -7, 4, 8, 1, 8, 2, 6, 6,
        -6, 6, 8, 3, 5, 7, -5, 7, 7, 5, -7, 5, 8, 4, 6, 7,
        -6, 7, 7, 6, -7, 6, 8, 5, 7, 7, -7, 7, 8, 6, 8, 7,
    ];

    private enum TransformType
    {
        Predictor = 0,
        CrossColor = 1,
        SubtractGreen = 2,
        ColorIndexing = 3,
    }

    /// <summary>Reads the 5-byte header: the size and whether alpha is used.</summary>
    public static (int Width, int Height, bool HasAlpha) ReadHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5 || data[0] != Signature)
        {
            throw ImageErrors.Corrupt("The WebP lossless data doesn't start with its signature.");
        }

        uint bits = (uint)(data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24));
        int width = (int)(bits & 0x3FFF) + 1;
        int height = (int)((bits >> 14) & 0x3FFF) + 1;
        bool alpha = ((bits >> 28) & 1) != 0;
        if ((bits >> 29) != 0)
        {
            throw ImageErrors.Corrupt("The WebP lossless data has an unknown version.");
        }

        return (width, height, alpha);
    }

    /// <summary>Decodes a whole VP8L chunk, header included.</summary>
    public static uint[] Decode(ReadOnlyMemory<byte> data, int width, int height)
    {
        var bits = new Vp8lBitReader(data);
        bits.Read(8 + 14 + 14 + 1 + 3);
        return DecodeStream(bits, width, height);
    }

    /// <summary>Decodes a stream without the header, as an ALPH chunk holds one: transforms, then the main image.</summary>
    public static uint[] DecodeStream(Vp8lBitReader bits, int width, int height)
    {
        var transforms = new List<Transform>();
        int codedWidth = width;
        while (bits.ReadBit())
        {
            var type = (TransformType)bits.Read(2);
            if (transforms.Exists(t => t.Type == type))
            {
                throw ImageErrors.Corrupt("The WebP lossless data uses a transform twice.");
            }

            var transform = new Transform(type, codedWidth);
            switch (type)
            {
                case TransformType.Predictor or TransformType.CrossColor:
                    transform.SizeBits = (int)bits.Read(3) + 2;
                    transform.Data = DecodeImage(bits, DivideUp(codedWidth, transform.SizeBits), DivideUp(height, transform.SizeBits), main: false);
                    break;

                case TransformType.ColorIndexing:
                    int colours = (int)bits.Read(8) + 1;
                    uint[] table = DecodeImage(bits, colours, 1, main: false);
                    for (int i = 1; i < colours; i++)
                    {
                        table[i] = AddPixels(table[i], table[i - 1]);
                    }

                    transform.Data = table;
                    transform.SizeBits = colours <= 2 ? 3 : colours <= 4 ? 2 : colours <= 16 ? 1 : 0;
                    codedWidth = DivideUp(codedWidth, transform.SizeBits);
                    break;
            }

            transforms.Add(transform);
        }

        uint[] pixels = DecodeImage(bits, codedWidth, height, main: true);
        for (int i = transforms.Count - 1; i >= 0; i--)
        {
            pixels = transforms[i].Undo(pixels, height);
        }

        return pixels;
    }

    private static int DivideUp(int value, int bits) => (value + (1 << bits) - 1) >> bits;

    // Section 5: an entropy-coded image, with its colour cache and, for the main image, its groups of prefix codes.
    private static uint[] DecodeImage(Vp8lBitReader bits, int width, int height, bool main)
    {
        int cacheBits = 0;
        if (bits.ReadBit())
        {
            cacheBits = (int)bits.Read(4);
            if (cacheBits is < 1 or > 11)
            {
                throw ImageErrors.Corrupt("The WebP lossless colour cache size is invalid.");
            }
        }

        int prefixBits = 0;
        uint[]? entropy = null;
        int groupCount = 1;
        if (main && bits.ReadBit())
        {
            prefixBits = (int)bits.Read(3) + 2;
            entropy = DecodeImage(bits, DivideUp(width, prefixBits), DivideUp(height, prefixBits), main: false);
            int most = 0;
            for (int i = 0; i < entropy.Length; i++)
            {
                most = Math.Max(most, (int)((entropy[i] >> 8) & 0xFFFF));
            }

            groupCount = most + 1;
        }

        int cacheSize = cacheBits == 0 ? 0 : 1 << cacheBits;
        var groups = new Vp8lPrefixCode[groupCount][];
        for (int g = 0; g < groupCount; g++)
        {
            groups[g] =
            [
                Vp8lPrefixCode.Read(bits, GreenAlphabet + cacheSize),
                Vp8lPrefixCode.Read(bits, 256),
                Vp8lPrefixCode.Read(bits, 256),
                Vp8lPrefixCode.Read(bits, 256),
                Vp8lPrefixCode.Read(bits, DistanceAlphabet),
            ];
        }

        uint[] pixels = new uint[(long)width * height];
        uint[] cache = new uint[cacheSize];
        int entropyWidth = entropy is null ? 0 : DivideUp(width, prefixBits);
        int lastCached = 0;
        int at = 0;
        int x = 0;
        int y = 0;
        int block = -1;
        Vp8lPrefixCode[] codes = groups[0];
        while (at < pixels.Length)
        {
            // x and y follow `at`; the group is looked up again only when the entropy block under them changes.
            while (x >= width)
            {
                x -= width;
                y++;
            }

            if (entropy is not null)
            {
                int current = ((y >> prefixBits) * entropyWidth) + (x >> prefixBits);
                if (current != block)
                {
                    block = current;
                    codes = groups[(int)((entropy[current] >> 8) & 0xFFFF)];
                }
            }

            int before = at;
            int green = codes[0].Decode(bits);
            if (green < 256)
            {
                int red = codes[1].Decode(bits);
                int blue = codes[2].Decode(bits);
                int alpha = codes[3].Decode(bits);
                pixels[at++] = (uint)((alpha << 24) | (red << 16) | (green << 8) | blue);
            }
            else if (green < GreenAlphabet)
            {
                int length = PrefixValue(bits, green - 256);
                int distanceCode = PrefixValue(bits, codes[4].Decode(bits));
                int distance = Distance(distanceCode, width);
                if (distance > at || at + length > pixels.Length)
                {
                    throw ImageErrors.Corrupt("A WebP lossless backward reference reaches outside the image.");
                }

                // Overlapping copies are meant: a distance of 1 repeats a pixel.
                for (int i = 0; i < length; i++, at++)
                {
                    pixels[at] = pixels[at - distance];
                }
            }
            else
            {
                int index = green - GreenAlphabet;
                AddToCache(pixels, cache, cacheBits, ref lastCached, at);
                pixels[at++] = cache[index];
            }

            AddToCache(pixels, cache, cacheBits, ref lastCached, at);
            x += at - before;
        }

        return pixels;
    }

    // Every decoded pixel enters the cache in order; this brings it up to date before `upTo`.
    private static void AddToCache(uint[] pixels, uint[] cache, int cacheBits, ref int last, int upTo)
    {
        if (cacheBits == 0)
        {
            last = upTo;
            return;
        }

        for (; last < upTo; last++)
        {
            uint argb = pixels[last];
            cache[(int)((0x1E35A7BDu * argb) >> (32 - cacheBits))] = argb;
        }
    }

    // Section 4.2.2: lengths and distance codes are a prefix and some extra bits.
    private static int PrefixValue(Vp8lBitReader bits, int prefix)
    {
        if (prefix < 4)
        {
            return prefix + 1;
        }

        int extra = (prefix - 2) >> 1;
        int offset = (2 + (prefix & 1)) << extra;
        return offset + (int)bits.Read(extra) + 1;
    }

    private static int Distance(int code, int width)
    {
        if (code > 120)
        {
            return code - 120;
        }

        int dx = DistanceMap[(code - 1) * 2];
        int dy = DistanceMap[((code - 1) * 2) + 1];
        return Math.Max((dy * width) + dx, 1);
    }

    private static uint AddPixels(uint a, uint b)
    {
        uint alphaGreen = (a & 0xFF00FF00u) + (b & 0xFF00FF00u);
        uint redBlue = (a & 0x00FF00FFu) + (b & 0x00FF00FFu);
        return (alphaGreen & 0xFF00FF00u) | (redBlue & 0x00FF00FFu);
    }

    /// <summary>One transform as read, with what's needed to undo it.</summary>
    private sealed class Transform(TransformType type, int width)
    {
        public TransformType Type { get; } = type;

        /// <summary>The image width when the transform was read; colour indexing narrows it for those after.</summary>
        public int Width { get; } = width;

        public int SizeBits { get; set; }

        public uint[] Data { get; set; } = [];

        public uint[] Undo(uint[] pixels, int height) => Type switch
        {
            TransformType.Predictor => UndoPredictor(pixels, height),
            TransformType.CrossColor => UndoCrossColor(pixels),
            TransformType.SubtractGreen => UndoSubtractGreen(pixels),
            _ => UndoColorIndexing(pixels, height),
        };

        // Section 4.1: each pixel is a residual from a prediction by already-decoded neighbours.
        private uint[] UndoPredictor(uint[] p, int height)
        {
            int w = Width;
            int blocksWide = DivideUp(w, SizeBits);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int at = (y * w) + x;
                    uint prediction;
                    if (y == 0)
                    {
                        prediction = x == 0 ? 0xFF000000u : p[at - 1];
                    }
                    else if (x == 0)
                    {
                        prediction = p[at - w];
                    }
                    else
                    {
                        int mode = (int)((Data[((y >> SizeBits) * blocksWide) + (x >> SizeBits)] >> 8) & 0xF);

                        // On the rightmost column, the top-right pixel is the first pixel of this row, which is
                        // where the next pixel after the top one lies in memory.
                        prediction = Predict(mode, p[at - 1], p[at - w], p[at - w + 1], p[at - w - 1]);
                    }

                    p[at] = AddPixels(p[at], prediction);
                }
            }

            return p;
        }

        // Section 4.2: red and blue were decorrelated from green (and blue from red) with per-block multipliers.
        private uint[] UndoCrossColor(uint[] p)
        {
            int w = Width;
            int height = p.Length / w;
            int blocksWide = DivideUp(w, SizeBits);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    uint element = Data[((y >> SizeBits) * blocksWide) + (x >> SizeBits)];
                    sbyte greenToRed = (sbyte)(element & 0xFF);
                    sbyte greenToBlue = (sbyte)((element >> 8) & 0xFF);
                    sbyte redToBlue = (sbyte)((element >> 16) & 0xFF);
                    int at = (y * w) + x;
                    uint argb = p[at];
                    sbyte green = (sbyte)((argb >> 8) & 0xFF);
                    int red = (int)((argb >> 16) & 0xFF);
                    int blue = (int)(argb & 0xFF);
                    red = (red + Delta(greenToRed, green)) & 0xFF;
                    blue = (blue + Delta(greenToBlue, green)) & 0xFF;
                    blue = (blue + Delta(redToBlue, (sbyte)red)) & 0xFF;
                    p[at] = (argb & 0xFF00FF00u) | ((uint)red << 16) | (uint)blue;
                }
            }

            return p;
        }

        private static int Delta(sbyte transform, sbyte colour) => (transform * colour) >> 5;

        private static uint[] UndoSubtractGreen(uint[] p)
        {
            for (int i = 0; i < p.Length; i++)
            {
                uint argb = p[i];
                uint green = (argb >> 8) & 0xFF;
                uint redBlue = ((argb & 0x00FF00FFu) + ((green << 16) | green)) & 0x00FF00FFu;
                p[i] = (argb & 0xFF00FF00u) | redBlue;
            }

            return p;
        }

        // Section 4.4: pixels are indexes into the colour table, several packed into one when the table is small.
        private uint[] UndoColorIndexing(uint[] p, int height)
        {
            int w = Width;
            int packedWidth = DivideUp(w, SizeBits);
            int bitsPerIndex = 8 >> SizeBits;
            int mask = (1 << bitsPerIndex) - 1;
            uint[] result = new uint[(long)w * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    uint packed = p[(y * packedWidth) + (x >> SizeBits)];
                    int index = (int)((packed >> 8) >> ((x & ((1 << SizeBits) - 1)) * bitsPerIndex)) & mask;
                    result[(y * w) + x] = index < Data.Length ? Data[index] : 0;
                }
            }

            return result;
        }

        private static uint Predict(int mode, uint left, uint top, uint topRight, uint topLeft) => mode switch
        {
            0 => 0xFF000000u,
            1 => left,
            2 => top,
            3 => topRight,
            4 => topLeft,
            5 => Average(Average(left, topRight), top),
            6 => Average(left, topLeft),
            7 => Average(left, top),
            8 => Average(topLeft, top),
            9 => Average(top, topRight),
            10 => Average(Average(left, topLeft), Average(top, topRight)),
            11 => Select(left, top, topLeft),
            12 => ClampAddSubtractFull(left, top, topLeft),
            13 => ClampAddSubtractHalf(Average(left, top), topLeft),
            _ => throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The WebP lossless predictor mode {mode} doesn't exist.")),
        };

        private static uint Average(uint a, uint b) => (((a ^ b) & 0xFEFEFEFEu) >> 1) + (a & b);

        private static int Channel(uint argb, int shift) => (int)((argb >> shift) & 0xFF);

        // Returns whichever of left and top is closer, channel by channel summed, to left + top - top-left.
        private static uint Select(uint left, uint top, uint topLeft)
        {
            int toLeft = 0;
            int toTop = 0;
            for (int shift = 0; shift < 32; shift += 8)
            {
                int estimate = Channel(left, shift) + Channel(top, shift) - Channel(topLeft, shift);
                toLeft += Math.Abs(estimate - Channel(left, shift));
                toTop += Math.Abs(estimate - Channel(top, shift));
            }

            return toLeft < toTop ? left : top;
        }

        private static uint ClampAddSubtractFull(uint a, uint b, uint c)
        {
            uint result = 0;
            for (int shift = 0; shift < 32; shift += 8)
            {
                result |= (uint)Math.Clamp(Channel(a, shift) + Channel(b, shift) - Channel(c, shift), 0, 255) << shift;
            }

            return result;
        }

        private static uint ClampAddSubtractHalf(uint a, uint b)
        {
            uint result = 0;
            for (int shift = 0; shift < 32; shift += 8)
            {
                int ca = Channel(a, shift);
                result |= (uint)Math.Clamp(ca + ((ca - Channel(b, shift)) / 2), 0, 255) << shift;
            }

            return result;
        }
    }
}
