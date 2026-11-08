using System;
using System.Runtime.Intrinsics;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Zorg.Imaging.Codecs.Webp;

/// <summary>Writes a VP8L (WebP lossless) bit stream, RFC 9649.</summary>
/// <remarks>
/// <para>
/// Images of up to 256 colours become a colour table and indexes (section 4.4), packed several to a pixel when the
/// table is small; above effort 0 they are also coded as below and the smaller kept. Other images get subtract-green
/// (4.3) where it helps, the predictor (4.1), each tile's mode chosen for the fewest bits by the statistics of the
/// residuals chosen so far, and from effort 1 the cross-colour transform (4.2).
/// </para>
/// <para>
/// The pixels are then coded with backward references (5.2.2) and a colour cache (5.2.3) whose size is chosen by the
/// entropy it gives. From effort 1 the main image gets an entropy image (5.2.1): tiles clustered into groups, each with
/// its own five prefix codes. From effort 2, each pixel's longest match is found and the cheapest path through
/// literals, cache hits and copies taken. Effort (0 to 6) also sets how far the hash chains are searched. The tuning
/// was measured against cwebp; see docs/performance.md.
/// </para>
/// </remarks>
internal static class Vp8lEncoder
{
    public const int MaxSide = 16384;
    private const int Signature = 0x2F;
    private const int GreenAlphabet = 256 + 24;
    private const int DistanceAlphabet = 40;
    private const int MaxCopy = 4096;
    private const int MinCopy = 3;
    private const int MaxCacheBits = 10;

    // The largest distance a distance code can carry (prefix 39 with all extra bits set), less the 120 plane codes.
    private const int MaxDistance = (1 << 20) - 120;

    // (dy, dx + 8) to the plane code (1 to 120) for the short distances of section 4.2.2; 0 where there's none.
    private static readonly byte[] PlaneCodes = BuildPlaneCodes();

    /// <summary>Encodes ARGB pixels (alpha in the top byte) as a VP8L stream, header included.</summary>
    /// <remarks>
    /// An image of 256 colours or fewer is also coded with the predictor, above effort 0, and the smaller kept: a colour
    /// table suits graphics, but smooth grey or few-colour gradients predict better.
    /// </remarks>
    public static byte[] Encode(uint[] argb, int width, int height, bool alpha, int effort)
    {
        // The header is 40 bits, so the rest starts on a byte and can be coded separately.
        byte[] header = new byte[5];
        ulong fields = Signature | ((ulong)(width - 1) << 8) | ((ulong)(height - 1) << 22) | ((alpha ? 1UL : 0) << 36);
        for (int i = 0; i < 5; i++)
        {
            header[i] = (byte)(fields >> (8 * i));
        }

        uint[]? palette = Palette(argb);
        ReadOnlySpan<byte> body = palette is null || effort > 0 ? Predicted(argb, width, height, effort) : [];
        if (palette is not null)
        {
            ReadOnlySpan<byte> indexed = Indexed(argb, width, height, palette, effort);
            if (body.IsEmpty || indexed.Length < body.Length)
            {
                body = indexed;
            }
        }

        return [.. header, .. body];
    }

    // Section 4.4 then the main image: the colour table, delta coded, and the indexes.
    private static ReadOnlySpan<byte> Indexed(uint[] argb, int width, int height, uint[] palette, int effort)
    {
        var bits = new Vp8lBitWriter((argb.Length / 4) + 1024);
        bits.Write(1, 1);
        bits.Write((uint)Vp8lDecoder.TransformType.ColorIndexing, 2);
        bits.Write((uint)(palette.Length - 1), 8);
        uint[] deltas = new uint[palette.Length];
        deltas[0] = palette[0];
        for (int i = 1; i < palette.Length; i++)
        {
            deltas[i] = Subtract(palette[i], palette[i - 1]);
        }

        EncodeImage(bits, deltas, palette.Length, effort, main: false);
        (uint[] pixels, int codedWidth) = Index(argb, width, height, palette);
        bits.Write(0, 1);
        EncodeImage(bits, pixels, codedWidth, effort, main: true);
        return bits.Finish();
    }

    // Sections 4.1 to 4.3 then the main image: subtract-green where the analysis favours it, the predictor (on tiles of
    // 32 at effort 0 and 1, 16 above, which saves about 0.3% on photos), then from effort 1 cross-colour.
    private static ReadOnlySpan<byte> Predicted(uint[] argb, int width, int height, int effort)
    {
        var bits = new Vp8lBitWriter((argb.Length / 2) + 1024);
        uint[] pixels = (uint[])argb.Clone();
        if (SubtractGreenHelps(pixels, width))
        {
            bits.Write(1, 1);
            bits.Write((uint)Vp8lDecoder.TransformType.SubtractGreen, 2);
            SubtractGreen(pixels);
        }

        int tileBits = effort <= 1 ? 5 : 4;
        bits.Write(1, 1);
        bits.Write((uint)Vp8lDecoder.TransformType.Predictor, 2);
        bits.Write((uint)(tileBits - 2), 3);
        (pixels, uint[] modes) = Predict(pixels, width, height, tileBits, effort);
        EncodeImage(bits, modes, DivideUp(width, tileBits), effort, main: false);
        if (effort >= 1)
        {
            int colourBits = 5;
            bits.Write(1, 1);
            bits.Write((uint)Vp8lDecoder.TransformType.CrossColor, 2);
            bits.Write((uint)(colourBits - 2), 3);
            uint[] multipliers = CrossColour(pixels, width, height, colourBits, effort);
            EncodeImage(bits, multipliers, DivideUp(width, colourBits), effort, main: false);
        }

        bits.Write(0, 1);
        EncodeImage(bits, pixels, width, effort, main: true);
        return bits.Finish();
    }

    // Whether subtracting green makes the image cheaper, judged as libwebp's analysis does: the entropy of each pixel's
    // difference from its left neighbour, channel by channel, with and without green taken from red and blue. Photos,
    // whose channels move together, gain; flat graphics often lose.
    internal static bool SubtractGreenHelps(uint[] pixels, int width)
    {
        int[] plain = new int[4 * 256];
        int[] subtracted = new int[4 * 256];
        uint previous = 0;
        uint previousSubtracted = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            uint p = pixels[i];
            uint g = (p >> 8) & 0xFF;
            uint sub = (p & 0xFF00FF00u) | ((((p >> 16) - g) & 0xFF) << 16) | ((p - g) & 0xFF);
            if (i % width != 0)
            {
                uint d = Subtract(p, previous);
                uint ds = Subtract(sub, previousSubtracted);
                plain[(int)(d >> 24)]++;
                plain[256 + (int)((d >> 16) & 0xFF)]++;
                plain[512 + (int)((d >> 8) & 0xFF)]++;
                plain[768 + (int)(d & 0xFF)]++;
                subtracted[(int)(ds >> 24)]++;
                subtracted[256 + (int)((ds >> 16) & 0xFF)]++;
                subtracted[512 + (int)((ds >> 8) & 0xFF)]++;
                subtracted[768 + (int)(ds & 0xFF)]++;
            }

            previous = p;
            previousSubtracted = sub;
        }

        return Bits(subtracted) < Bits(plain);

        static double Bits(int[] counts)
        {
            double total = 0;
            for (int channel = 0; channel < 4; channel++)
            {
                total += Entropy(counts.AsSpan(channel * 256, 256).ToArray());
            }

            return total;
        }
    }

    private static int DivideUp(int value, int bits) => (value + (1 << bits) - 1) >> bits;

    /// <summary>The image's colours, sorted, when there are at most 256; else <see langword="null"/>.</summary>
    private static uint[]? Palette(uint[] argb)
    {
        var colours = new HashSet<uint>();
        uint last = ~argb[0];
        foreach (uint pixel in argb)
        {
            if (pixel == last)
            {
                continue;
            }

            last = pixel;
            if (colours.Add(pixel) && colours.Count > 256)
            {
                return null;
            }
        }

        uint[] palette = [.. colours];
        Array.Sort(palette);
        return palette;
    }

    // Section 4.4: each pixel becomes its index, in the green channel; with 16 colours or fewer, 2, 4 or 8 indexes share
    // a pixel, the first in the lowest bits.
    private static (uint[] Pixels, int Width) Index(uint[] argb, int width, int height, uint[] palette)
    {
        var lookup = new Dictionary<uint, int>(palette.Length);
        for (int i = 0; i < palette.Length; i++)
        {
            lookup[palette[i]] = i;
        }

        int sizeBits = palette.Length <= 2 ? 3 : palette.Length <= 4 ? 2 : palette.Length <= 16 ? 1 : 0;
        int bitsPerIndex = 8 >> sizeBits;
        int packedWidth = DivideUp(width, sizeBits);
        uint[] packed = new uint[(long)packedWidth * height];
        uint last = argb[0];
        int lastIndex = lookup[last];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int packedRow = y * packedWidth;
            for (int x = 0; x < width; x++)
            {
                uint pixel = argb[row + x];
                if (pixel != last)
                {
                    last = pixel;
                    lastIndex = lookup[pixel];
                }

                packed[packedRow + (x >> sizeBits)] |= (uint)lastIndex << (8 + ((x & ((1 << sizeBits) - 1)) * bitsPerIndex));
            }

            for (int x = 0; x < packedWidth; x++)
            {
                packed[packedRow + x] |= 0xFF000000u;
            }
        }

        return (packed, packedWidth);
    }

    private static void SubtractGreen(uint[] pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            uint argb = pixels[i];
            uint green = (argb >> 8) & 0xFF;
            uint redBlue = ((argb | 0xFF00FF00u) - ((green << 16) | green)) & 0x00FF00FFu; // The filled bytes stop borrows.
            pixels[i] = (argb & 0xFF00FF00u) | redBlue;
        }
    }

    /// <summary>Each channel of <paramref name="a"/> less that of <paramref name="b"/>, modulo 256.</summary>
    private static uint Subtract(uint a, uint b)
    {
        uint alphaGreen = 0x00FF00FFu + (a & 0xFF00FF00u) - (b & 0xFF00FF00u);
        uint redBlue = 0xFF00FF00u + (a & 0x00FF00FFu) - (b & 0x00FF00FFu);
        return (alphaGreen & 0xFF00FF00u) | (redBlue & 0x00FF00FFu);
    }

    // Section 4.1. The residual of each pixel from its prediction, with the mode for each tile chosen by the bits its
    // residuals would take if coded with the statistics of all residuals chosen so far. The statistics are refreshed
    // once per row of tiles, so the tiles of a row are independent and run in parallel, with the same result.
    internal static (uint[] Residuals, uint[] Modes) Predict(uint[] p, int width, int height, int tileBits, int effort)
    {
        int tilesWide = DivideUp(width, tileBits);
        int tilesHigh = DivideUp(height, tileBits);
        int tile = 1 << tileBits;
        uint[] residuals = new uint[p.Length];
        uint[] modes = new uint[tilesWide * tilesHigh];

        // The statistics start from a prior that small residuals are common, so the first tiles aren't chosen blind.
        int[] seen = new int[4 * 256];
        for (int v = 0; v < 256; v++)
        {
            int size = Math.Min(v, 256 - v);
            int prior = Math.Max(256 >> Math.Min(size, 8), 1) * 4;
            seen[v] = seen[256 + v] = seen[512 + v] = seen[768 + v] = prior;
        }

        float[] cost = new float[4 * 256];
        int firstMode = effort == 0 ? 1 : 0;
        int lastMode = effort == 0 ? 2 : 13;

        // The first row predicts from the left (the first pixel from opaque black), the first column from above.
        residuals[0] = Subtract(p[0], 0xFF000000u);
        for (int x = 1; x < width; x++)
        {
            residuals[x] = Subtract(p[x], p[x - 1]);
        }

        for (int y = 1; y < height; y++)
        {
            residuals[y * width] = Subtract(p[y * width], p[(y - 1) * width]);
        }

        int bands = Processing.Parallelism.Bands((long)width * tile * (lastMode - firstMode + 1), tilesWide);
        int[][] counts = new int[bands][];
        for (int b = 0; b < bands; b++)
        {
            counts[b] = new int[4 * 256];
        }

        for (int ty = 0; ty < tilesHigh; ty++)
        {
            Costs(seen, cost);
            int top = Math.Max(ty * tile, 1);
            int bottom = Math.Min((ty + 1) * tile, height);
            int row = ty;
            Processing.Parallelism.For(bands, band =>
            {
                int[] local = counts[band];
                for (int tx = tilesWide * band / bands; tx < tilesWide * (band + 1) / bands; tx++)
                {
                    int left = Math.Max(tx * tile, 1);
                    int right = Math.Min((tx + 1) * tile, width);
                    int best = ChooseMode(p, width, top, bottom, left, right, firstMode, lastMode, cost);
                    modes[(row * tilesWide) + tx] = 0xFF000000u | ((uint)best << 8);
                    for (int y = top; y < bottom; y++)
                    {
                        int start = (y * width) + left;
                        Span<uint> line = residuals.AsSpan(start, right - left);
                        Residuals(best, p, width, start, line);
                        foreach (uint residual in line)
                        {
                            local[(int)(residual >> 24)]++;
                            local[256 + (int)((residual >> 16) & 0xFF)]++;
                            local[512 + (int)((residual >> 8) & 0xFF)]++;
                            local[768 + (int)(residual & 0xFF)]++;
                        }
                    }
                }
            });

            foreach (int[] local in counts)
            {
                for (int v = 0; v < local.Length; v++)
                {
                    seen[v] += local[v];
                }

                Array.Clear(local);
            }
        }

        return (residuals, modes);
    }

    // The mode whose residuals over the tile cost fewest bits; the lowest mode wins ties.
    private static int ChooseMode(uint[] p, int width, int top, int bottom, int left, int right, int firstMode, int lastMode, float[] cost)
    {
        int best = 1;
        float bestCost = float.MaxValue;
        Span<uint> line = stackalloc uint[right - left];
        for (int mode = firstMode; mode <= lastMode; mode++)
        {
            float total = 0;
            for (int y = top; y < bottom && total < bestCost; y++)
            {
                Residuals(mode, p, width, (y * width) + left, line);
                foreach (uint residual in line)
                {
                    total += cost[(int)(residual >> 24)] + cost[256 + (int)((residual >> 16) & 0xFF)]
                        + cost[512 + (int)((residual >> 8) & 0xFF)] + cost[768 + (int)(residual & 0xFF)];
                }
            }

            if (total < bestCost)
            {
                bestCost = total;
                best = mode;
            }
        }

        return best;
    }

    // The residuals of pixels start to start + output.Length (none in the first row or column) under a predictor mode:
    // four pixels to a 128-bit vector, each byte as Vp8lDecoder.Transform.Predict and Subtract compute it, then any
    // remainder one by one.
    internal static void Residuals(int mode, uint[] p, int width, int start, Span<uint> output)
    {
        int n = output.Length;
        int x = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ReadOnlySpan<uint> pixels = p;
            for (; x + 4 <= n; x += 4)
            {
                int i = start + x;
                Vector128<byte> here = Vector128.Create(pixels.Slice(i, 4)).AsByte();
                Vector128<byte> prediction = PredictVector(mode, pixels, i, width);
                (here - prediction).AsUInt32().CopyTo(output.Slice(x, 4));
            }
        }

        for (; x < n; x++)
        {
            int i = start + x;
            output[x] = Subtract(p[i], Vp8lDecoder.Transform.Predict(mode, p[i - 1], p[i - width], p[i - width + 1], p[i - width - 1]));
        }
    }

    private static Vector128<byte> PredictVector(int mode, ReadOnlySpan<uint> p, int i, int width)
    {
        static Vector128<byte> Load(ReadOnlySpan<uint> pixels, int at) => Vector128.Create(pixels.Slice(at, 4)).AsByte();
        switch (mode)
        {
            case 0:
                return Vector128.Create(0xFF000000u).AsByte();
            case 1:
                return Load(p, i - 1);
            case 2:
                return Load(p, i - width);
            case 3:
                return Load(p, i - width + 1);
            case 4:
                return Load(p, i - width - 1);
            case 5:
                return Average(Average(Load(p, i - 1), Load(p, i - width + 1)), Load(p, i - width));
            case 6:
                return Average(Load(p, i - 1), Load(p, i - width - 1));
            case 7:
                return Average(Load(p, i - 1), Load(p, i - width));
            case 8:
                return Average(Load(p, i - width - 1), Load(p, i - width));
            case 9:
                return Average(Load(p, i - width), Load(p, i - width + 1));
            case 10:
                return Average(Average(Load(p, i - 1), Load(p, i - width - 1)), Average(Load(p, i - width), Load(p, i - width + 1)));
            case 11:
                return Select(Load(p, i - 1), Load(p, i - width), Load(p, i - width - 1));
            case 12:
                return ClampAddSubtractFull(Load(p, i - 1), Load(p, i - width), Load(p, i - width - 1));
            default:
                return ClampAddSubtractHalf(Average(Load(p, i - 1), Load(p, i - width)), Load(p, i - width - 1));
        }
    }

    // The byte-wise floor average, as the scalar (((a ^ b) & 0xFE...) >> 1) + (a & b).
    private static Vector128<byte> Average(Vector128<byte> a, Vector128<byte> b) =>
        (Vector128.ShiftRightLogical((a ^ b).AsUInt32() & Vector128.Create(0xFEFEFEFEu), 1) + (a & b).AsUInt32()).AsByte();

    // Left or top, whichever the summed channel distances to left + top - top-left favour; top on a tie.
    private static Vector128<byte> Select(Vector128<byte> left, Vector128<byte> top, Vector128<byte> topLeft)
    {
        Vector128<uint> toLeft = ChannelSum(Vector128.Max(top, topLeft) - Vector128.Min(top, topLeft));
        Vector128<uint> toTop = ChannelSum(Vector128.Max(left, topLeft) - Vector128.Min(left, topLeft));
        return Vector128.ConditionalSelect(Vector128.LessThan(toLeft, toTop).AsByte(), left, top);
    }

    private static Vector128<uint> ChannelSum(Vector128<byte> bytes)
    {
        Vector128<uint> v = bytes.AsUInt32();
        Vector128<uint> low = Vector128.Create(0xFFu);
        return (v & low) + (Vector128.ShiftRightLogical(v, 8) & low) + (Vector128.ShiftRightLogical(v, 16) & low) + Vector128.ShiftRightLogical(v, 24);
    }

    // a + b - c in each channel, clamped to 0 to 255.
    private static Vector128<byte> ClampAddSubtractFull(Vector128<byte> a, Vector128<byte> b, Vector128<byte> c)
    {
        Vector128<short> low = (Vector128.WidenLower(a) + Vector128.WidenLower(b) - Vector128.WidenLower(c)).AsInt16();
        Vector128<short> high = (Vector128.WidenUpper(a) + Vector128.WidenUpper(b) - Vector128.WidenUpper(c)).AsInt16();
        return Narrow(low, high);
    }

    // a + (a - b) / 2 in each channel, the division rounding toward zero as C#'s does, clamped to 0 to 255.
    private static Vector128<byte> ClampAddSubtractHalf(Vector128<byte> a, Vector128<byte> b)
    {
        Vector128<short> HalfStep(Vector128<ushort> wa, Vector128<ushort> wb)
        {
            Vector128<short> d = (wa - wb).AsInt16();
            Vector128<short> half = Vector128.ShiftRightArithmetic(d - Vector128.ShiftRightArithmetic(d, 15), 1);
            return wa.AsInt16() + half;
        }

        return Narrow(HalfStep(Vector128.WidenLower(a), Vector128.WidenLower(b)), HalfStep(Vector128.WidenUpper(a), Vector128.WidenUpper(b)));
    }

    private static Vector128<byte> Narrow(Vector128<short> low, Vector128<short> high)
    {
        Vector128<short> zero = Vector128<short>.Zero;
        Vector128<short> top = Vector128.Create((short)255);
        return Vector128.Narrow(Vector128.Min(Vector128.Max(low, zero), top).AsUInt16(), Vector128.Min(Vector128.Max(high, zero), top).AsUInt16());
    }

    // Section 4.2: red and blue less what green (and blue less what red) predicts of them, with multipliers chosen per
    // tile to make the residuals cheapest by the statistics so far (refreshed per row of tiles, so a row's tiles run in
    // parallel): a coarse search, then a fine one around the best. The transform comes after the predictor, so it's
    // undone first.
    internal static uint[] CrossColour(uint[] p, int width, int height, int tileBits, int effort = 4)
    {
        int tilesWide = DivideUp(width, tileBits);
        int tilesHigh = DivideUp(height, tileBits);
        int tile = 1 << tileBits;
        uint[] multipliers = new uint[tilesWide * tilesHigh];
        int[] seen = new int[2 * 256];
        for (int v = 0; v < 256; v++)
        {
            int size = Math.Min(v, 256 - v);
            seen[v] = seen[256 + v] = Math.Max(256 >> Math.Min(size, 8), 1) * 4;
        }

        float[] redCost = new float[256];
        float[] blueCost = new float[256];
        int bands = Processing.Parallelism.Bands((long)width * tile * 48, tilesWide);
        var work = new ColourWork[bands];
        for (int b = 0; b < bands; b++)
        {
            work[b] = new ColourWork(tile * tile);
        }

        for (int ty = 0; ty < tilesHigh; ty++)
        {
            ChannelCosts(seen.AsSpan(0, 256), redCost);
            ChannelCosts(seen.AsSpan(256, 256), blueCost);
            int top = ty * tile;
            int bottom = Math.Min(top + tile, height);
            int row = ty;
            Processing.Parallelism.For(bands, band =>
            {
                ColourWork w = work[band];
                for (int tx = tilesWide * band / bands; tx < tilesWide * (band + 1) / bands; tx++)
                {
                    int left = tx * tile;
                    int right = Math.Min(left + tile, width);
                    int count = 0;
                    for (int y = top; y < bottom; y++)
                    {
                        for (int x = left; x < right; x++, count++)
                        {
                            uint argb = p[(y * width) + x];
                            w.Greens[count] = (sbyte)(argb >> 8);
                            w.Reds[count] = (int)((argb >> 16) & 0xFF);
                            w.Blues[count] = (int)(argb & 0xFF);
                        }
                    }

                    int reach = effort >= 5 ? 2 : effort >= 3 ? 1 : 0;
                    int greenToRed = Search(Slope(w.Reds, w.Greens, count), reach, t => Cost(w.Reds, w.Greens, count, t, redCost));
                    int greenToBlue = Search(Slope(w.Blues, w.Greens, count), reach, t => Cost(w.Blues, w.Greens, count, t, blueCost));
                    for (int k = 0; k < count; k++)
                    {
                        w.Rest[k] = w.Blues[k] - Delta(greenToBlue, w.Greens[k]);
                        w.SignedReds[k] = (sbyte)w.Reds[k];
                    }

                    int redToBlue = Search(Slope(w.Rest, w.SignedReds, count), reach, t => Cost(w.Rest, w.SignedReds, count, t, blueCost));
                    multipliers[(row * tilesWide) + tx] = 0xFF000000u | ((uint)(byte)redToBlue << 16) | ((uint)(byte)greenToBlue << 8) | (byte)greenToRed;

                    for (int y = top; y < bottom; y++)
                    {
                        for (int x = left; x < right; x++)
                        {
                            int i = (y * width) + x;
                            uint argb = p[i];
                            int green = (sbyte)(argb >> 8);
                            int red = (int)((argb >> 16) & 0xFF);
                            int newRed = (red - Delta(greenToRed, green)) & 0xFF;
                            int newBlue = ((int)(argb & 0xFF) - Delta(greenToBlue, green) - Delta(redToBlue, (sbyte)red)) & 0xFF;
                            p[i] = (argb & 0xFF00FF00u) | ((uint)newRed << 16) | (uint)newBlue;
                            w.Counts[newRed]++;
                            w.Counts[256 + newBlue]++;
                        }
                    }
                }
            });

            foreach (ColourWork w in work)
            {
                for (int v = 0; v < seen.Length; v++)
                {
                    seen[v] += w.Counts[v];
                }

                Array.Clear(w.Counts);
            }
        }

        return multipliers;
    }

    // One thread's buffers for the cross-colour search.
    private sealed class ColourWork(int pixels)
    {
        public int[] Greens { get; } = new int[pixels];

        public int[] Reds { get; } = new int[pixels];

        public int[] Blues { get; } = new int[pixels];

        public int[] Counts { get; } = new int[2 * 256];

        public int[] Rest { get; } = new int[pixels];

        public int[] SignedReds { get; } = new int[pixels];
    }

    private static int Delta(int multiplier, int colour) => (multiplier * colour) >> 5;

    // The multiplier, -128 to 127, with the lowest cost: 0, the least-squares slope and the coarse points beside it, then
    // every 2nd near the best, then the neighbours. Starting from the slope finds nearly the same minimum as a scan of
    // every 16th, for a third of the trials (files about 0.1% larger). Reach 0 (effort 0 to 2) tries the slope and
    // every 2nd within 4 of the best, as good on photos for half the trials; reach 1 (effort 3 and 4) also the slope
    // plus and minus 16, then within 8; reach 2 (effort 5 and up) scans every 16th, then within 14. 0 wins ties, as it
    // changes nothing.
    private static int Search(int slope, int reach, Func<int, float> cost)
    {
        int best = 0;
        float bestCost = cost(0);
        void Try(int t)
        {
            if (t is < -128 or > 127 || t == best)
            {
                return;
            }

            float c = cost(t);
            if (c < bestCost)
            {
                bestCost = c;
                best = t;
            }
        }

        if (reach == 2)
        {
            for (int t = -128; t <= 112; t += 16)
            {
                Try(t);
            }
        }
        else
        {
            Try(slope);
            if (reach == 1)
            {
                Try(slope - 16);
                Try(slope + 16);
            }
        }

        int near = reach switch { 2 => 14, 1 => 8, _ => 4 };
        foreach ((int from, int to, int step) in new[] { (-near, near, 2), (-1, 1, 1) })
        {
            int centre = best;
            for (int t = centre + from; t <= centre + to; t += step)
            {
                Try(t);
            }
        }

        return best;
    }

    // The multiplier t that makes values - (t * from) / 32 least correlated with from: 32 times the regression slope,
    // with the values as signed bytes, as the transform sees them.
    private static int Slope(int[] values, int[] from, int count)
    {
        long sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
        for (int i = 0; i < count; i++)
        {
            int x = from[i];
            int y = (sbyte)values[i];
            sumX += x;
            sumY += y;
            sumXX += x * x;
            sumXY += x * y;
        }

        double variance = (count * (double)sumXX) - ((double)sumX * sumX);
        if (variance <= 0)
        {
            return 0;
        }

        double slope = ((count * (double)sumXY) - ((double)sumX * sumY)) / variance;
        return (int)Math.Clamp(Math.Round(slope * 32), -128, 127);
    }

    // The cost of a tile's channel less another channel times a multiplier.
    private static float Cost(int[] values, int[] from, int count, int multiplier, float[] costs)
    {
        float total = 0;
        for (int i = 0; i < count; i++)
        {
            total += costs[(values[i] - ((multiplier * from[i]) >> 5)) & 0xFF];
        }

        return total;
    }

    private static void ChannelCosts(ReadOnlySpan<int> seen, float[] cost)
    {
        long total = 0;
        foreach (int c in seen)
        {
            total += c;
        }

        float log = MathF.Log2(total);
        for (int v = 0; v < 256; v++)
        {
            cost[v] = log - MathF.Log2(seen[v]);
        }
    }

    // Bits per value of each channel by what's been seen, with one extra count for each value so none is free.
    private static void Costs(int[] seen, float[] cost)
    {
        for (int channel = 0; channel < 4; channel++)
        {
            long total = 256;
            for (int v = 0; v < 256; v++)
            {
                total += seen[(channel * 256) + v];
            }

            float log = MathF.Log2(total);
            for (int v = 0; v < 256; v++)
            {
                cost[(channel * 256) + v] = log - MathF.Log2(seen[(channel * 256) + v] + 1);
            }
        }
    }

    // Section 5: one entropy-coded image, as backward references, cache hits and literals under groups of five prefix
    // codes. The main image of a large enough picture gets an entropy image (5.2.1): a group for each cluster of tiles
    // with alike statistics.
    private static void EncodeImage(Vp8lBitWriter bits, uint[] pixels, int width, int effort, bool main)
    {
        (int[] lengths, int[] distances, int count, int cacheBits) = Tokens(pixels, width, effort);
        if (cacheBits > 0)
        {
            bits.Write(1, 1);
            bits.Write((uint)cacheBits, 4);
        }
        else
        {
            bits.Write(0, 1);
        }

        int[]? tokenGroups = null;
        int groupCount = 1;
        if (main)
        {
            int height = pixels.Length / width;
            int prefixBits = EntropyBits(width, height, effort);
            if (prefixBits == 0)
            {
                bits.Write(0, 1);
            }
            else
            {
                int[] tokenTiles = TokenTiles(lengths, count, width, prefixBits);
                Histogram[] tiles = Histogram.CountGroups(pixels, lengths, distances, count, width, cacheBits, tokenTiles, DivideUp(width, prefixBits) * DivideUp(height, prefixBits), out _);
                int[] tileGroups = Cluster(tiles, effort >= 5 ? 256 : 64, out groupCount);
                bits.Write(1, 1);
                bits.Write((uint)(prefixBits - 2), 3);
                uint[] entropyImage = new uint[tileGroups.Length];
                for (int t = 0; t < tileGroups.Length; t++)
                {
                    entropyImage[t] = 0xFF000000u | ((uint)tileGroups[t] << 8);
                }

                EncodeImage(bits, entropyImage, DivideUp(width, prefixBits), effort, main: false);
                tokenGroups = new int[count];
                for (int k = 0; k < count; k++)
                {
                    tokenGroups[k] = tileGroups[tokenTiles[k]];
                }
            }
        }

        Histogram[] groups = Histogram.CountGroups(pixels, lengths, distances, count, width, cacheBits, tokenGroups, groupCount, out int[] cacheHits);
        var codes = new Vp8lHuffman[groupCount][];
        for (int g = 0; g < groupCount; g++)
        {
            Histogram h = groups[g];
            codes[g] =
            [
                Vp8lHuffman.WriteCode(bits, h.Green),
                Vp8lHuffman.WriteCode(bits, h.Red),
                Vp8lHuffman.WriteCode(bits, h.Blue),
                Vp8lHuffman.WriteCode(bits, h.Alpha),
                Vp8lHuffman.WriteCode(bits, h.Distance),
            ];
        }

        int at = 0;
        for (int k = 0; k < count; k++)
        {
            Vp8lHuffman[] code = codes[tokenGroups is null ? 0 : tokenGroups[k]];
            if (lengths[k] == 0)
            {
                if (cacheHits[k] >= 0)
                {
                    code[0].Write(bits, GreenAlphabet + cacheHits[k]);
                }
                else
                {
                    uint pixel = pixels[at];
                    code[0].Write(bits, (int)((pixel >> 8) & 0xFF));
                    code[1].Write(bits, (int)((pixel >> 16) & 0xFF));
                    code[2].Write(bits, (int)(pixel & 0xFF));
                    code[3].Write(bits, (int)(pixel >> 24));
                }

                at++;
                continue;
            }

            (int lengthPrefix, int lengthBits, int lengthExtra) = Prefix(lengths[k]);
            code[0].Write(bits, 256 + lengthPrefix);
            bits.Write((uint)lengthExtra, lengthBits);
            (int distancePrefix, int distanceBits, int distanceExtra) = Prefix(DistanceCode(distances[k], width));
            code[4].Write(bits, distancePrefix);
            bits.Write((uint)distanceExtra, distanceBits);
            at += lengths[k];
        }
    }

    // The entropy image's tile size: none below effort 3 or for small pictures; else tiles of 2^bits, as few as keeps
    // about 2,000 tiles (more at higher effort), at least 8 x 8.
    private static int EntropyBits(int width, int height, int effort)
    {
        long pixels = (long)width * height;
        if (effort < 1 || pixels < 4096)
        {
            return 0;
        }

        long target = effort >= 5 ? 4096 : 2048;
        int bits = 3;
        while (bits < 9 && (long)DivideUp(width, bits) * DivideUp(height, bits) > target)
        {
            bits++;
        }

        return bits;
    }

    // The tile each token starts in, which is the tile whose group codes it (section 5.2.1).
    private static int[] TokenTiles(int[] lengths, int count, int width, int prefixBits)
    {
        int[] tiles = new int[count];
        int tilesWide = DivideUp(width, prefixBits);
        int x = 0;
        int y = 0;
        for (int k = 0; k < count; k++)
        {
            tiles[k] = ((y >> prefixBits) * tilesWide) + (x >> prefixBits);
            x += Math.Max(lengths[k], 1);
            while (x >= width)
            {
                x -= width;
                y++;
            }
        }

        return tiles;
    }

    // Tiles into at most `most` groups: tiles first binned by how many bits per pixel their green, red and blue take,
    // then the pair of groups whose merge saves most bits merged, while a merge saves any, then each tile moved to the
    // group that codes it in the fewest bits.
    private static int[] Cluster(Histogram[] tiles, int most, out int groupCount)
    {
        int n = tiles.Length;
        double[][] perPixel = new double[3][];
        for (int c = 0; c < 3; c++)
        {
            perPixel[c] = new double[n];
        }

        for (int t = 0; t < n; t++)
        {
            Histogram h = tiles[t];
            long symbols = Math.Max(Sum(h.Green), 1);
            perPixel[0][t] = Entropy(h.Green) / symbols;
            perPixel[1][t] = Entropy(h.Red) / Math.Max(Sum(h.Red), 1);
            perPixel[2][t] = Entropy(h.Blue) / Math.Max(Sum(h.Blue), 1);
        }

        // Up to 4 x 4 x 4 bins between each channel's least and most.
        int[] bin = new int[n];
        for (int c = 0; c < 3; c++)
        {
            double low = double.MaxValue;
            double high = double.MinValue;
            foreach (double v in perPixel[c])
            {
                low = Math.Min(low, v);
                high = Math.Max(high, v);
            }

            double range = Math.Max(high - low, 1e-9);
            for (int t = 0; t < n; t++)
            {
                bin[t] = (bin[t] * 4) + Math.Min((int)((perPixel[c][t] - low) / range * 4), 3);
            }
        }

        var clusters = new List<Histogram>();
        int[] binCluster = new int[64];
        Array.Fill(binCluster, -1);
        int[] tileCluster = new int[n];
        for (int t = 0; t < n; t++)
        {
            if (Sum(tiles[t].Green) == 0)
            {
                tileCluster[t] = -1;
                continue;
            }

            if (binCluster[bin[t]] < 0)
            {
                binCluster[bin[t]] = clusters.Count;
                clusters.Add(new Histogram(tiles[t].CacheBits));
            }

            tileCluster[t] = binCluster[bin[t]];
            clusters[tileCluster[t]].Add(tiles[t]);
        }

        // Greedy merging, with each pair's saving kept and only the merged group's pairs worked out again.
        int m = clusters.Count;
        bool[] alive = new bool[m];
        double[] cost = new double[m];
        for (int a = 0; a < m; a++)
        {
            alive[a] = true;
            cost[a] = clusters[a].Bits();
        }

        double[] saving = new double[m * m];
        for (int a = 0; a < m; a++)
        {
            for (int b = a + 1; b < m; b++)
            {
                saving[(a * m) + b] = cost[a] + cost[b] - Histogram.MergedBits(clusters[a], clusters[b]);
            }
        }

        int living = m;
        while (living > 1)
        {
            int bestA = -1;
            int bestB = -1;
            double best = 0;
            for (int a = 0; a < m; a++)
            {
                if (!alive[a])
                {
                    continue;
                }

                for (int b = a + 1; b < m; b++)
                {
                    if (alive[b] && saving[(a * m) + b] > best)
                    {
                        best = saving[(a * m) + b];
                        bestA = a;
                        bestB = b;
                    }
                }
            }

            if (bestA < 0 && living <= most)
            {
                break;
            }

            if (bestA < 0)
            {
                // More groups than allowed and no merge saves bits: merge the pair that costs least.
                double least = double.MaxValue;
                for (int a = 0; a < m; a++)
                {
                    for (int b = a + 1; b < m; b++)
                    {
                        if (alive[a] && alive[b] && -saving[(a * m) + b] < least)
                        {
                            least = -saving[(a * m) + b];
                            bestA = a;
                            bestB = b;
                        }
                    }
                }
            }

            clusters[bestA].Add(clusters[bestB]);
            alive[bestB] = false;
            living--;
            cost[bestA] = clusters[bestA].Bits();
            for (int other = 0; other < m; other++)
            {
                if (other != bestA && alive[other])
                {
                    int lo = Math.Min(other, bestA);
                    int hi = Math.Max(other, bestA);
                    saving[(lo * m) + hi] = cost[lo] + cost[hi] - Histogram.MergedBits(clusters[lo], clusters[hi]);
                }
            }

            for (int t = 0; t < n; t++)
            {
                if (tileCluster[t] == bestB)
                {
                    tileCluster[t] = bestA;
                }
            }
        }

        // Number the groups left, then move each tile to the group whose codes would take fewest bits for it.
        int[] number = new int[m];
        groupCount = 0;
        var final = new List<Histogram>();
        for (int a = 0; a < m; a++)
        {
            number[a] = alive[a] ? groupCount++ : -1;
            if (alive[a])
            {
                final.Add(clusters[a]);
            }
        }

        groupCount = Math.Max(groupCount, 1);
        float[][][] costs = new float[final.Count][][];
        for (int g = 0; g < final.Count; g++)
        {
            costs[g] = final[g].SymbolCosts();
        }

        int[] tileGroups = new int[n];
        for (int t = 0; t < n; t++)
        {
            if (tileCluster[t] < 0)
            {
                continue;
            }

            int bestGroup = number[tileCluster[t]];
            double bestBits = double.MaxValue;
            for (int g = 0; g < final.Count; g++)
            {
                double bitsHere = tiles[t].BitsUnder(costs[g]);
                if (bitsHere < bestBits)
                {
                    bestBits = bitsHere;
                    bestGroup = g;
                }
            }

            tileGroups[t] = bestGroup;
        }

        return tileGroups;
    }

    private static long Sum(int[] counts)
    {
        long total = 0;
        foreach (int c in counts)
        {
            total += c;
        }

        return total;
    }

    /// <summary>The symbol counts of one parse, or of the part of it in one group, as the decoder will see the cache.</summary>
    private sealed class Histogram(int cacheBits)
    {
        // A rough cost of writing one code's lengths in the header, per symbol used.
        private const double HeaderBitsPerSymbol = 4;

        public int CacheBits { get; } = cacheBits;

        public int[] Green { get; } = new int[GreenAlphabet + (cacheBits == 0 ? 0 : 1 << cacheBits)];

        public int[] Red { get; } = new int[256];

        public int[] Blue { get; } = new int[256];

        public int[] Alpha { get; } = new int[256];

        public int[] Distance { get; } = new int[DistanceAlphabet];

        private IEnumerable<int[]> Arrays => [Green, Red, Blue, Alpha, Distance];

        /// <summary>
        /// Counts the tokens into one histogram per group (all into the first when <paramref name="tokenGroups"/> is
        /// <see langword="null"/>); <paramref name="cacheHits"/> is each literal's cache index when it's a hit, else -1.
        /// </summary>
        public static Histogram[] CountGroups(uint[] pixels, int[] lengths, int[] distances, int count, int width, int cacheBits, int[]? tokenGroups, int groupCount, out int[] cacheHits)
        {
            var groups = new Histogram[groupCount];
            for (int g = 0; g < groupCount; g++)
            {
                groups[g] = new Histogram(cacheBits);
            }

            int[] hits = new int[count];
            uint[] cache = new uint[cacheBits == 0 ? 1 : 1 << cacheBits];
            int shift = 32 - cacheBits;
            int at = 0;
            for (int k = 0; k < count; k++)
            {
                Histogram h = groups[tokenGroups is null ? 0 : tokenGroups[k]];
                if (lengths[k] == 0)
                {
                    uint pixel = pixels[at++];
                    hits[k] = -1;
                    if (cacheBits > 0)
                    {
                        int key = (int)((0x1E35A7BDu * pixel) >> shift);
                        if (cache[key] == pixel)
                        {
                            hits[k] = key;
                            h.Green[GreenAlphabet + key]++;
                            continue;
                        }

                        cache[key] = pixel;
                    }

                    h.Green[(pixel >> 8) & 0xFF]++;
                    h.Red[(pixel >> 16) & 0xFF]++;
                    h.Blue[pixel & 0xFF]++;
                    h.Alpha[pixel >> 24]++;
                    continue;
                }

                h.Green[256 + Prefix(lengths[k]).Prefix]++;
                h.Distance[Prefix(DistanceCode(distances[k], width)).Prefix]++;
                if (cacheBits > 0)
                {
                    for (int i = at; i < at + lengths[k]; i++)
                    {
                        cache[(int)((0x1E35A7BDu * pixels[i]) >> shift)] = pixels[i];
                    }
                }

                at += lengths[k];
            }

            cacheHits = hits;
            return groups;
        }

        /// <summary>The bits of merging two histograms, without merging them.</summary>
        public static double MergedBits(Histogram a, Histogram b)
        {
            double total = 0;
            using IEnumerator<int[]> left = a.Arrays.GetEnumerator();
            using IEnumerator<int[]> right = b.Arrays.GetEnumerator();
            while (left.MoveNext() && right.MoveNext())
            {
                total += MergedArrayBits(left.Current, right.Current);
            }

            return total;
        }

        public void Add(Histogram other)
        {
            using IEnumerator<int[]> mine = Arrays.GetEnumerator();
            using IEnumerator<int[]> theirs = other.Arrays.GetEnumerator();
            while (mine.MoveNext() && theirs.MoveNext())
            {
                for (int i = 0; i < mine.Current.Length; i++)
                {
                    mine.Current[i] += theirs.Current[i];
                }
            }
        }

        /// <summary>The bits the five codes take, symbols and a rough header.</summary>
        public double Bits()
        {
            double total = 0;
            foreach (int[] counts in Arrays)
            {
                total += ArrayBits(counts);
            }

            return total;
        }

        /// <summary>Each array's bits per symbol under codes built for this histogram.</summary>
        public float[][] SymbolCosts() => [.. Arrays.Select(Costs)];

        /// <summary>The bits this histogram's symbols take under other codes' costs.</summary>
        public double BitsUnder(float[][] costs)
        {
            double total = 0;
            int a = 0;
            foreach (int[] counts in Arrays)
            {
                float[] cost = costs[a++];
                for (int i = 0; i < counts.Length; i++)
                {
                    total += counts[i] * (double)cost[i];
                }
            }

            return total;
        }

        private static double ArrayBits(int[] counts)
        {
            long total = 0;
            double sum = 0;
            int used = 0;
            foreach (int c in counts)
            {
                if (c > 0)
                {
                    total += c;
                    sum += c * Math.Log2(c);
                    used++;
                }
            }

            return used <= 1 ? 4 : (total * Math.Log2(total)) - sum + (used * HeaderBitsPerSymbol);
        }

        private static double MergedArrayBits(int[] a, int[] b)
        {
            long total = 0;
            double sum = 0;
            int used = 0;
            for (int i = 0; i < a.Length; i++)
            {
                int c = a[i] + b[i];
                if (c > 0)
                {
                    total += c;
                    sum += c * Math.Log2(c);
                    used++;
                }
            }

            return used <= 1 ? 4 : (total * Math.Log2(total)) - sum + (used * HeaderBitsPerSymbol);
        }
    }

    // Section 5.2.2: a value of 1 or more as a prefix symbol and extra bits.
    private static (int Prefix, int ExtraBits, int Extra) Prefix(int value)
    {
        int v = value - 1;
        if (v < 4)
        {
            return (v, 0, 0);
        }

        int high = 31 - BitOperations.LeadingZeroCount((uint)v);
        int second = (v >> (high - 1)) & 1;
        int extraBits = high - 1;
        return ((2 * high) + second, extraBits, v & ((1 << extraBits) - 1));
    }

    // Section 4.2.2: the shortest code for a distance; a plane code when the distance is a near neighbour.
    private static int DistanceCode(int distance, int width)
    {
        int dy = distance / width;
        int dx = distance - (dy * width);
        int best = distance + 120;
        if (dx <= 8 && dy < 8)
        {
            int code = PlaneCodes[(dy * 17) + dx + 8];
            if (code != 0)
            {
                best = Math.Min(best, code);
            }
        }

        if (width - dx <= 8 && dy + 1 < 8)
        {
            int code = PlaneCodes[((dy + 1) * 17) + dx - width + 8];
            if (code != 0)
            {
                best = Math.Min(best, code);
            }
        }

        return best;
    }

    private static byte[] BuildPlaneCodes()
    {
        byte[] codes = new byte[8 * 17];
        ReadOnlySpan<sbyte> map = Vp8lDecoder.DistanceMap;
        for (int code = 1; code <= 120; code++)
        {
            int dx = map[(code - 1) * 2];
            int dy = map[((code - 1) * 2) + 1];
            codes[(dy * 17) + dx + 8] = (byte)code;
        }

        return codes;
    }

    // The tokens and the cache size. Effort 0 and 1: greedy, the longest match at each token. Above that: the longest
    // match at every pixel, a greedy parse of those to choose the cache size and price the symbols, then the cheapest
    // path through literals, cache hits and copies (twice at effort 5 and 6, priced the second time by the first).
    // Lengths of 0 are literals.
    private static (int[] Lengths, int[] Distances, int Count, int CacheBits) Tokens(uint[] p, int width, int effort)
    {
        var finder = new MatchFinder(p, width, effort);
        int n = p.Length;
        int[] lengths = new int[n];
        int[] distances = new int[n];
        int count = 0;
        if (effort <= 1)
        {
            for (int i = 0; i < n;)
            {
                (int length, int distance) = finder.Find(i);
                finder.Extend(i, ref length, ref distance);
                if (length >= MinCopy)
                {
                    lengths[count] = length;
                    distances[count] = distance;
                    finder.Used(i, length, distance);
                }

                count++;
                i += length >= MinCopy ? length : 1;
            }

            return (lengths, distances, count, effort == 0 ? 0 : ChooseCacheBits(p, lengths, count));
        }

        // Every pixel's longest match, in chunks of fixed size that run in parallel (fixed, so the result doesn't depend
        // on the number of threads). Within a chunk, the match one pixel into a long copy is the same copy one shorter,
        // so the search is skipped there.
        int[] matchLength = new int[n];
        int[] matchDistance = new int[n];
        bool[] inherited = new bool[n];
        const int Chunk = 1 << 16;
        int chunks = (n + Chunk - 1) / Chunk;
        Processing.Parallelism.For(Processing.Parallelism.Bands(n, chunks), band =>
        {
            int bands = Processing.Parallelism.Bands(n, chunks);
            for (int c = chunks * band / bands; c < chunks * (band + 1) / bands; c++)
            {
                int end = Math.Min((c + 1) * Chunk, n);
                for (int i = c * Chunk; i < end; i++)
                {
                    if (i > c * Chunk && matchLength[i - 1] > 32)
                    {
                        matchLength[i] = matchLength[i - 1] - 1;
                        matchDistance[i] = matchDistance[i - 1];
                        inherited[i] = true;
                        continue;
                    }

                    (matchLength[i], matchDistance[i]) = finder.Find(i);
                }
            }
        });

        // Then a greedy parse, which tries the distances it has used where its tokens start, as the greedy parse above
        // does: they find the long copies in patterns. Its tokens choose the cache size and price the symbols.
        for (int i = 0; i < n; count++)
        {
            int length = matchLength[i];
            int distance = matchDistance[i];
            if (inherited[i])
            {
                (length, distance) = finder.Find(i);
            }

            finder.Extend(i, ref length, ref distance);
            if (length > matchLength[i])
            {
                matchLength[i] = length;
                matchDistance[i] = distance;
            }

            bool copy = matchLength[i] >= MinCopy;
            lengths[count] = copy ? matchLength[i] : 0;
            distances[count] = copy ? matchDistance[i] : 0;
            if (copy)
            {
                finder.Used(i, matchLength[i], matchDistance[i]);
            }

            i += copy ? matchLength[i] : 1;
        }

        int cacheBits = ChooseCacheBits(p, lengths, count);
        for (int pass = effort >= 5 ? 2 : 1; pass > 0; pass--)
        {
            Histogram histogram = Histogram.CountGroups(p, lengths, distances, count, width, cacheBits, null, 1, out _)[0];
            count = Cheapest(p, width, matchLength, matchDistance, histogram, lengths, distances);
        }

        return (lengths, distances, count, cacheBits);
    }

    // The cheapest path, by symbol costs from a histogram: cost[i] is the fewest bits for the first i pixels, step[i]
    // the step that reached it (1 for a literal, -k for a copy of k). Whether a pixel is a cache hit doesn't depend on
    // the path, since every pixel enters the cache in order, so hits are priced exactly.
    private static int Cheapest(uint[] p, int width, int[] matchLength, int[] matchDistance, Histogram histogram, int[] lengths, int[] distances)
    {
        int n = p.Length;
        float[] greenCost = Costs(histogram.Green);
        float[] redCost = Costs(histogram.Red);
        float[] blueCost = Costs(histogram.Blue);
        float[] alphaCost = Costs(histogram.Alpha);
        float[] distanceCost = Costs(histogram.Distance);
        int cacheBits = histogram.CacheBits;
        uint[] cache = new uint[cacheBits == 0 ? 1 : 1 << cacheBits];

        float[] cost = new float[n + 1];
        int[] step = new int[n + 1];
        Array.Fill(cost, float.MaxValue);
        cost[0] = 0;
        for (int i = 0; i < n; i++)
        {
            uint pixel = p[i];
            float literal = greenCost[(pixel >> 8) & 0xFF] + redCost[(pixel >> 16) & 0xFF] + blueCost[pixel & 0xFF] + alphaCost[pixel >> 24];
            if (cacheBits > 0)
            {
                int key = (int)((0x1E35A7BDu * pixel) >> (32 - cacheBits));
                if (cache[key] == pixel)
                {
                    literal = greenCost[GreenAlphabet + key];
                }

                cache[key] = pixel;
            }

            if (cost[i] + literal < cost[i + 1])
            {
                cost[i + 1] = cost[i] + literal;
                step[i + 1] = 1;
            }

            int longest = matchLength[i];
            if (longest < 1)
            {
                continue;
            }

            (int distancePrefix, int distanceBits, _) = Prefix(DistanceCode(matchDistance[i], width));
            float copy = cost[i] + distanceCost[distancePrefix] + distanceBits;

            // Every length up to 16, and the longest; longer ones are reached from the pixels inside the copy.
            for (int k = 1; ; k = k < 16 ? k + 1 : longest)
            {
                (int lengthPrefix, int lengthBits, _) = Prefix(k);
                float total = copy + greenCost[256 + lengthPrefix] + lengthBits;
                if (total < cost[i + k])
                {
                    cost[i + k] = total;
                    step[i + k] = -k;
                }

                if (k >= longest)
                {
                    break;
                }
            }
        }

        // Back from the end, then written forwards as tokens.
        int count = 0;
        for (int i = n; i > 0; count++)
        {
            i -= Math.Abs(step[i]);
        }

        for (int i = n, k = count - 1; i > 0; k--)
        {
            int length = Math.Abs(step[i]);
            bool isCopy = step[i] < 0;
            i -= length;
            lengths[k] = isCopy ? length : 0;
            distances[k] = isCopy ? matchDistance[i] : 0;
        }

        return count;
    }

    // Bits for each symbol by its share of the counts; unseen symbols cost a little more than the rarest seen.
    private static float[] Costs(int[] counts)
    {
        long total = 0;
        foreach (int c in counts)
        {
            total += c;
        }

        float[] cost = new float[counts.Length];
        float log = MathF.Log2(Math.Max(total, 1));
        for (int i = 0; i < counts.Length; i++)
        {
            cost[i] = counts[i] == 0 ? log + 2 : log - MathF.Log2(counts[i]);
        }

        return cost;
    }

    /// <summary>
    /// Finds the longest earlier match for a pixel: the pixel to the left and the rows above first, then hash chains over
    /// pairs of pixels, nearest first. The chains are built up front, each pixel linking only to earlier ones, so
    /// <see cref="Find"/> may run on many pixels at once. <see cref="Extend"/> tries the distances a parse has used,
    /// which follow the parse and so run in order.
    /// </summary>
    private sealed class MatchFinder
    {
        private readonly uint[] _p;
        private readonly int _width;
        private readonly int _depth;
        private readonly bool _quick;
        private readonly int[] _chain;
        private readonly int[] _used;
        private int _last = 1;
        private int _beforeLast;

        public MatchFinder(uint[] p, int width, int effort)
        {
            _p = p;
            _width = width;
            _beforeLast = width;
            _depth = effort switch { 0 => 0, 1 => 4, 2 => 8, 3 => 16, 4 => 32, 5 => 64, _ => 256 };
            _quick = effort == 0;
            _chain = new int[p.Length];
            _used = new int[p.Length];

            // About one bucket per pixel, 2^10 to 2^22 of them: collisions make chains longer and the walk a run of
            // cache misses. The last pixel has no pair and links to nothing.
            int hashBits = Math.Clamp(BitOperations.Log2((uint)Math.Max(p.Length, 1)) + 1, 10, 22);
            int shift = 32 - hashBits;
            int[] head = new int[1 << hashBits];
            Array.Fill(head, -1);
            for (int i = 0; i + 1 < p.Length; i++)
            {
                int key = Hash(p[i], p[i + 1], shift);
                _chain[i] = head[key];
                head[key] = i;
            }

            if (p.Length > 0)
            {
                _chain[^1] = -1;
            }
        }

        public (int Length, int Distance) Find(int i)
        {
            uint[] p = _p;
            int most = Math.Min(MaxCopy, p.Length - i);
            int bestLength = 0;
            int bestDistance = 0;
            ReadOnlySpan<int> candidates = [1, _width, 2 * _width, 3 * _width];
            for (int c = 0; c < (_quick ? 2 : candidates.Length); c++)
            {
                int distance = candidates[c];

                // Only a candidate that matches one pixel past the best so far can beat it: most fail at once in a
                // photo, so the pixel is compared before the full match is measured.
                if (distance > i || (c == 1 && distance == 1) || bestLength >= most || p[i + bestLength] != p[i - distance + bestLength])
                {
                    continue;
                }

                int length = Match(p, i, i - distance, most);
                if (length > bestLength)
                {
                    bestLength = length;
                    bestDistance = distance;
                }
            }

            if (bestLength < most)
            {
                int candidate = _chain[i];
                for (int tries = _depth; tries > 0 && candidate >= 0 && i - candidate <= MaxDistance; tries--)
                {
                    if (p[candidate + bestLength] == p[i + bestLength])
                    {
                        int length = Match(p, i, candidate, most);
                        if (length > bestLength)
                        {
                            bestLength = length;
                            bestDistance = i - candidate;
                            if (length == most)
                            {
                                break;
                            }
                        }
                    }

                    candidate = _chain[candidate];
                }
            }

            return (bestLength, bestDistance);
        }

        /// <summary>Tries the last two distances used and the one used for the pixel above, keeping any longer match.</summary>
        public void Extend(int i, ref int length, ref int distance)
        {
            if (_quick)
            {
                return;
            }

            uint[] p = _p;
            int most = Math.Min(MaxCopy, p.Length - i);
            ReadOnlySpan<int> candidates = [_last, _beforeLast, i >= _width ? _used[i - _width] : 0];
            foreach (int d in candidates)
            {
                if (d == 0 || d > i || d > MaxDistance || d == distance || length >= most || p[i + length] != p[i - d + length])
                {
                    continue;
                }

                int found = Match(p, i, i - d, most);
                if (found > length)
                {
                    length = found;
                    distance = d;
                }
            }
        }

        /// <summary>Records a copy, for the distance candidates of the pixels after and below it.</summary>
        public void Used(int i, int length, int distance)
        {
            if (distance != _last)
            {
                _beforeLast = _last;
                _last = distance;
            }

            _used.AsSpan(i, length).Fill(distance);
        }
    }

    private static int Hash(uint a, uint b, int shift) => (int)(((a * 0x9E3779B1u) + (b * 0x85EBCA77u)) >> shift);

    private static int Match(uint[] p, int at, int from, int most)
    {
        ReadOnlySpan<uint> current = p.AsSpan(at, most);
        ReadOnlySpan<uint> earlier = p.AsSpan(from, most);
        int length = current.CommonPrefixLength(earlier);
        return length;
    }

    // Section 5.2.3: the cache size whose symbol statistics give the fewest bits, by Shannon entropy. Each size is a walk
    // over the tokens of its own, so the sizes are tried in parallel.
    private static int ChooseCacheBits(uint[] pixels, int[] lengths, int count)
    {
        // A large image is judged from four strips spread down it, each a sixteenth of the tokens and starting with an
        // empty cache: the choice is an estimate either way, and this is a quarter of the work.
        var strips = new List<(int FirstToken, int Tokens, int FirstPixel)>();
        if (pixels.Length >= 1 << 20)
        {
            int strip = count / 16;
            int at = 0;
            int token = 0;
            for (int s = 0; s < 4; s++)
            {
                int first = (count * ((4 * s) + 2)) / 16;
                for (; token < first; token++)
                {
                    at += Math.Max(lengths[token], 1);
                }

                strips.Add((first, strip, at));
            }
        }
        else
        {
            strips.Add((0, count, 0));
        }

        double[] bits = new double[MaxCacheBits + 1];
        int bands = Processing.Parallelism.Bands((long)pixels.Length * (MaxCacheBits + 1), MaxCacheBits + 1);
        Processing.Parallelism.For(bands, band =>
        {
            for (int b = (MaxCacheBits + 1) * band / bands; b < (MaxCacheBits + 1) * (band + 1) / bands; b++)
            {
                foreach ((int firstToken, int tokens, int firstPixel) in strips)
                {
                    bits[b] += CacheBitsCost(pixels, lengths, firstToken, tokens, firstPixel, b);
                }
            }
        });

        int best = 0;
        for (int b = 1; b <= MaxCacheBits; b++)
        {
            if (bits[b] < bits[best])
            {
                best = b;
            }
        }

        return best;
    }

    // The literals' bits with a cache of 2^cacheBits (none for 0), and about 4 header bits for each cache symbol.
    private static double CacheBitsCost(uint[] pixels, int[] lengths, int firstToken, int tokens, int firstPixel, int cacheBits)
    {
        uint[] cache = new uint[1 << cacheBits];
        int[] green = new int[256 + (1 << cacheBits)];
        int[] red = new int[256];
        int[] blue = new int[256];
        int[] alpha = new int[256];
        int shift = 32 - cacheBits;
        int at = firstPixel;
        for (int k = firstToken; k < firstToken + tokens; k++)
        {
            if (lengths[k] != 0)
            {
                if (cacheBits > 0)
                {
                    for (int end = at + lengths[k]; at < end; at++)
                    {
                        cache[(int)((0x1E35A7BDu * pixels[at]) >> shift)] = pixels[at];
                    }
                }
                else
                {
                    at += lengths[k];
                }

                continue;
            }

            uint pixel = pixels[at++];
            if (cacheBits > 0)
            {
                int index = (int)((0x1E35A7BDu * pixel) >> shift);
                if (cache[index] == pixel)
                {
                    green[256 + index]++;
                    continue;
                }

                cache[index] = pixel;
            }

            green[(pixel >> 8) & 0xFF]++;
            red[(pixel >> 16) & 0xFF]++;
            blue[pixel & 0xFF]++;
            alpha[pixel >> 24]++;
        }

        return Entropy(green) + Entropy(red) + Entropy(blue) + Entropy(alpha) + (cacheBits == 0 ? 0 : 4 << cacheBits);
    }

    private static double Entropy(int[] counts)
    {
        long total = 0;
        double sum = 0;
        foreach (int c in counts)
        {
            if (c > 0)
            {
                total += c;
                sum += c * Math.Log2(c);
            }
        }

        return total == 0 ? 0 : (total * Math.Log2(total)) - sum;
    }
}
