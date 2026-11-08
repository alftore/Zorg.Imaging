using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Metadata;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Codecs.Jpeg;

/// <summary>Writes a baseline JPEG (ITU-T T.81, sequential DCT, Huffman coding) in a JFIF file.</summary>
/// <remarks>
/// <para>
/// Grey images become one-component files; colour becomes YCbCr (JFIF, T.871), with chroma averaged over 2 x 2 for 4:2:0.
/// Quantisation is the standard's example tables (T.81, Annex K.1) scaled as libjpeg scales them for a quality, so
/// quality numbers mean what they mean in other tools. The Huffman tables are built for each image (Annex K.2), which
/// makes files a few percent smaller than the example tables would.
/// </para>
/// <para>
/// Two passes over the coefficients: the transform runs once, in parallel over rows of MCUs, and its output is kept
/// (2 bytes a coefficient) to count symbols and then to write them. Transparency is drawn over the background colour;
/// 16-bit samples are rounded to 8 bits. The colour space is written as an ICC profile unless it is sRGB.
/// </para>
/// </remarks>
internal static class JpegEncoder
{
    /// <summary>The largest side a JPEG frame header can hold.</summary>
    public const int MaxSide = 65535;

    // T.81 Table K.1 and K.2, the example luminance and chrominance quantisation tables, in natural order.
    private static ReadOnlySpan<byte> LuminanceTable =>
    [
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static ReadOnlySpan<byte> ChrominanceTable =>
    [
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
    ];

    public static async ValueTask EncodeAsync(Image image, Stream stream, JpegEncoderOptions options, bool sync, CancellationToken cancellationToken)
    {
        if (image.Width > MaxSide || image.Height > MaxSide)
        {
            throw new NotSupportedException(string.Create(
                CultureInfo.InvariantCulture,
                $"JPEG can't hold a {image.Width} x {image.Height} image: each side must be at most {MaxSide} pixels."));
        }

        ReadOnlyMemory<byte> profile = IccProfileWriter.For(image.ColorSpace, "JPEG");
        byte[] file;
        using (Parallelism.Use(options.MaxDegreeOfParallelism))
        {
            file = Encode(image, options, profile.Span);
        }

        if (sync)
        {
            stream.Write(file);
        }
        else
        {
            await stream.WriteAsync(file, cancellationToken).ConfigureAwait(false);
        }
    }

    private static byte[] Encode(Image image, JpegEncoderOptions options, ReadOnlySpan<byte> profile)
    {
        bool grey = image.PixelFormat is PixelFormat.L8 or PixelFormat.L16;
        bool subsample = !grey && options.ChromaSubsampling == JpegChromaSubsampling.Yuv420;
        int factor = subsample ? 2 : 1;
        int mcuSide = 8 * factor;
        int mcusWide = (image.Width + mcuSide - 1) / mcuSide;
        int mcusHigh = (image.Height + mcuSide - 1) / mcuSide;

        byte[] luminance = Scale(LuminanceTable, options.Quality);
        byte[] chrominance = Scale(ChrominanceTable, options.Quality);
        var components = grey
            ? new[] { new Plane(mcusWide, mcusHigh, 1) }
            : new[] { new Plane(mcusWide * factor, mcusHigh * factor, factor), new Plane(mcusWide, mcusHigh, 1), new Plane(mcusWide, mcusHigh, 1) };

        // The AC symbols are counted as the blocks are made, while they are in cache; the DC differences, which cross
        // the bands, afterwards. Then tables are built that code them in the fewest bits.
        long[][]? counts = options.Progressive ? null : [new long[256], new long[256], new long[256], new long[256]];
        Transform(image, options.Background, components, mcusWide, mcusHigh, factor, Reciprocals(luminance), Reciprocals(chrominance), counts);

        if (counts is null)
        {
            return EncodeProgressive(image, options, profile, grey, factor, mcusWide, mcusHigh, luminance, chrominance, components);
        }

        Walk(components, mcusWide, factor, 0, mcusHigh, new int[components.Length], (component, block, previous) =>
            counts[component == 0 ? 0 : 2][Category(block[0] - previous)]++);
        JpegHuffmanEncoder dcLuma = JpegHuffmanEncoder.Optimal(counts[0]);
        JpegHuffmanEncoder acLuma = JpegHuffmanEncoder.Optimal(counts[1]);
        JpegHuffmanEncoder? dcChroma = grey ? null : JpegHuffmanEncoder.Optimal(counts[2]);
        JpegHuffmanEncoder? acChroma = grey ? null : JpegHuffmanEncoder.Optimal(counts[3]);

        var output = new ArrayBufferWriter<byte>(4096 + profile.Length + (int)Math.Min((long)image.Width * image.Height / 4, Array.MaxLength / 2));
        (byte[]? exif, byte[]? xmp) = Metadata.MetadataWriter.Prepare(image, options.Metadata);
        WriteHeaders(output, image, profile, exif, xmp, grey, factor, luminance, chrominance, progressive: false, options.Metadata);
        WriteTables(output, dcLuma, acLuma, dcChroma, acChroma);

        // SOS: every component, full spectrum, no approximation.
        Segment(output, 0xDA, grey ? [1, 1, 0x00, 0, 63, 0] : [3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0]);

        // The scan in bands of MCU rows, coded in parallel as plain bits (each band starts from the DC values of the
        // band before, which are all known), then joined with the stuffed zeros added: the same bytes as one pass.
        int bands = Parallelism.Bands((long)image.Width * image.Height, mcusHigh);
        var parts = new BitWriter[bands];
        Parallelism.For(bands, band =>
        {
            int first = (int)((long)mcusHigh * band / bands);
            int end = (int)((long)mcusHigh * (band + 1) / bands);
            int[] start = new int[components.Length];
            for (int c = 0; c < components.Length && first > 0; c++)
            {
                int side = components[c].Factor;
                start[c] = components[c].Block((mcusWide * side) - 1, (first * side) - 1)[0];
            }

            var bits = new BitWriter(Math.Max(4096, (int)((long)image.Width * image.Height / 4 / bands)));
            Walk(components, mcusWide, factor, first, end, start, (component, block, previous) =>
            {
                bool luma = component == 0;
                bits.Block(block, previous, luma ? dcLuma : dcChroma!, luma ? acLuma : acChroma!);
            });
            parts[band] = bits;
        });

        var scan = new StuffingWriter(output);
        foreach (BitWriter part in parts)
        {
            part.CopyTo(scan);
        }

        scan.Finish();
        output.Write<byte>([0xFF, 0xD9]);
        return output.WrittenSpan.ToArray();
    }

    // libjpeg's quality scaling (jcparam.c, from the IJG's description): 50 is the table itself.
    private static byte[] Scale(ReadOnlySpan<byte> table, int quality)
    {
        int scale = quality < 50 ? 5000 / quality : 200 - (2 * quality);
        byte[] scaled = new byte[64];
        for (int i = 0; i < 64; i++)
        {
            scaled[i] = (byte)Math.Clamp(((table[i] * scale) + 50) / 100, 1, 255);
        }

        return scaled;
    }

    private static float[] Reciprocals(byte[] table)
    {
        float[] reciprocals = new float[64];
        for (int i = 0; i < 64; i++)
        {
            reciprocals[i] = 1f / table[i];
        }

        return reciprocals;
    }

    // Converts, centres, transforms and quantises every block, a band of MCU rows per thread, and adds the AC symbols to
    // counts[1] (luma) and counts[3] (chroma) when given.
    private static void Transform(Image image, Rgba32 background, Plane[] planes, int mcusWide, int mcusHigh, int factor, float[] lumaQ, float[] chromaQ, long[][]? counts)
    {
        bool grey = planes.Length == 1;
        int mcuSide = 8 * factor;
        int bands = Parallelism.Bands((long)image.Width * image.Height, mcusHigh / 2);
        Parallelism.For(bands, band =>
        {
            int width = mcusWide * mcuSide;
            float[] y = new float[width * mcuSide];
            float[] cb = grey ? [] : new float[width * mcuSide];
            float[] cr = grey ? [] : new float[width * mcuSide];
            int halfWidth = width / factor;
            float[] cbHalf = grey || factor == 1 ? cb : new float[halfWidth * 8];
            float[] crHalf = grey || factor == 1 ? cr : new float[halfWidth * 8];
            long[] lumaAc = new long[256];
            long[] chromaAc = new long[256];
            int end = (int)((long)mcusHigh * (band + 1) / bands);
            for (int row = (int)((long)mcusHigh * band / bands); row < end; row++)
            {
                for (int line = 0; line < mcuSide; line++)
                {
                    int source = Math.Min((row * mcuSide) + line, image.Height - 1);
                    ConvertRow(image, source, background, y.AsSpan(line * width, width), grey ? default : cb.AsSpan(line * width, width), grey ? default : cr.AsSpan(line * width, width));
                }

                // Luma: every block of the MCU row, read in place.
                for (int by = 0; by < factor; by++)
                {
                    for (int bx = 0; bx < mcusWide * factor; bx++)
                    {
                        Span<short> block = planes[0].Block(bx, (row * factor) + by);
                        JpegFdct.Transform(y.AsSpan((by * 8 * width) + (bx * 8)), width, lumaQ, block);
                        if (counts is not null)
                        {
                            CountAc(block, lumaAc);
                        }
                    }
                }

                if (grey)
                {
                    continue;
                }

                // Chroma: averaged over 2 x 2 once for the whole MCU row when subsampled, then one block per MCU.
                if (factor == 2)
                {
                    Halve(cb, width, cbHalf);
                    Halve(cr, width, crHalf);
                }

                for (int mx = 0; mx < mcusWide; mx++)
                {
                    Span<short> blue = planes[1].Block(mx, row);
                    Span<short> red = planes[2].Block(mx, row);
                    JpegFdct.Transform(cbHalf.AsSpan(mx * 8), halfWidth, chromaQ, blue);
                    JpegFdct.Transform(crHalf.AsSpan(mx * 8), halfWidth, chromaQ, red);
                    if (counts is not null)
                    {
                        CountAc(blue, chromaAc);
                        CountAc(red, chromaAc);
                    }
                }
            }

            if (counts is not null)
            {
                lock (counts)
                {
                    for (int k = 0; k < 256; k++)
                    {
                        counts[1][k] += lumaAc[k];
                        counts[3][k] += chromaAc[k];
                    }
                }
            }
        });
    }

    // The mean of each 2 x 2 square of a 16-row plane: 8 rows of half the width.
    private static void Halve(float[] plane, int width, float[] half)
    {
        int halfWidth = width / 2;
        for (int y = 0; y < 8; y++)
        {
            ReadOnlySpan<float> top = plane.AsSpan(2 * y * width, width);
            ReadOnlySpan<float> bottom = plane.AsSpan(((2 * y) + 1) * width, width);
            Span<float> output = half.AsSpan(y * halfWidth, halfWidth);
            for (int x = 0; x < halfWidth; x++)
            {
                output[x] = (top[2 * x] + top[(2 * x) + 1] + bottom[2 * x] + bottom[(2 * x) + 1]) * 0.25f;
            }
        }
    }

    // One source row as Y, Cb and Cr (JFIF), centred on 0, padded to the MCU width by repeating the last pixel.
    private static void ConvertRow(Image image, int y, Rgba32 background, Span<float> luma, Span<float> cb, Span<float> cr)
    {
        ReadOnlySpan<byte> row = image.GetRowBytes(y);
        ReadOnlySpan<ushort> wide = image.PixelFormat is PixelFormat.L16 or PixelFormat.Rgb48 or PixelFormat.Rgba64
            ? MemoryMarshal.Cast<byte, ushort>(row)
            : default;
        int width = image.Width;
        int start = 0;
        if (image.PixelFormat == PixelFormat.Rgb24 && !cb.IsEmpty)
        {
            start = JpegColour.Rgb24ToYcc(row, luma, cb, cr, width);
            for (int x = start; x < luma.Length; x++)
            {
                int at = 3 * Math.Min(x, width - 1);
                float r = row[at];
                float g = row[at + 1];
                float b = row[at + 2];
                luma[x] = (0.299f * r) + (0.587f * g) + (0.114f * b) - 128;
                cb[x] = (-0.168736f * r) - (0.331264f * g) + (0.5f * b);
                cr[x] = (0.5f * r) - (0.418688f * g) - (0.081312f * b);
            }

            return;
        }

        for (int x = 0; x < luma.Length; x++)
        {
            int s = Math.Min(x, width - 1);
            (int r, int g, int b) = image.PixelFormat switch
            {
                PixelFormat.L8 => (row[s], row[s], row[s]),
                PixelFormat.L16 => (To8(wide[s]), 0, 0),
                PixelFormat.Rgb24 => (row[3 * s], row[(3 * s) + 1], row[(3 * s) + 2]),
                PixelFormat.Rgb48 => (To8(wide[3 * s]), To8(wide[(3 * s) + 1]), To8(wide[(3 * s) + 2])),
                PixelFormat.Rgba32 => Over(row[4 * s], row[(4 * s) + 1], row[(4 * s) + 2], row[(4 * s) + 3], background),
                PixelFormat.Bgra32 => Over(row[(4 * s) + 2], row[(4 * s) + 1], row[4 * s], row[(4 * s) + 3], background),
                _ => Over(To8(wide[4 * s]), To8(wide[(4 * s) + 1]), To8(wide[(4 * s) + 2]), To8(wide[(4 * s) + 3]), background),
            };

            if (cb.IsEmpty)
            {
                luma[x] = r - 128;
                continue;
            }

            luma[x] = (0.299f * r) + (0.587f * g) + (0.114f * b) - 128;
            cb[x] = (-0.168736f * r) - (0.331264f * g) + (0.5f * b);
            cr[x] = (0.5f * r) - (0.418688f * g) - (0.081312f * b);
        }
    }

    private static int To8(ushort value) => (value + 128) / 257;

    private static (int R, int G, int B) Over(int r, int g, int b, int a, Rgba32 background) =>
        a == 255 ? (r, g, b) : (Blend(r, background.R, a), Blend(g, background.G, a), Blend(b, background.B, a));

    private static int Blend(int colour, int background, int alpha) => ((colour * alpha) + (background * (255 - alpha)) + 127) / 255;

    private delegate void BlockVisitor(int component, ReadOnlySpan<short> block, int previous);

    // Visits the blocks of MCU rows first to end in interleaved MCU order, giving each component's previous DC for the
    // difference, starting from the values given.
    private static void Walk(Plane[] planes, int mcusWide, int factor, int first, int end, int[] previous, BlockVisitor visit)
    {
        for (int my = first; my < end; my++)
        {
            for (int mx = 0; mx < mcusWide; mx++)
            {
                for (int c = 0; c < planes.Length; c++)
                {
                    int side = c == 0 ? factor : 1;
                    for (int by = 0; by < side; by++)
                    {
                        for (int bx = 0; bx < side; bx++)
                        {
                            ReadOnlySpan<short> block = planes[c].Block((mx * side) + bx, (my * side) + by);
                            visit(c, block, previous[c]);
                            previous[c] = block[0];
                        }
                    }
                }
            }
        }
    }

    private static int Category(int value)
    {
        int magnitude = Math.Abs(value);
        return magnitude == 0 ? 0 : 32 - int.LeadingZeroCount(magnitude);
    }

    // Bit k set when coefficient k isn't zero: eight 128-bit compares, so the coding loops jump between the few
    // coefficients that matter instead of testing all 64.
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

    private static void CountAc(ReadOnlySpan<short> block, long[] ac)
    {
        ulong mask = NonZero(block) & ~1UL;
        int last = 0;
        while (mask != 0)
        {
            int k = BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;
            int run = k - last - 1;
            while (run > 15)
            {
                ac[0xF0]++;
                run -= 16;
            }

            ac[(run << 4) | Category(block[k])]++;
            last = k;
        }

        if (last < 63)
        {
            ac[0x00]++;
        }
    }

    // T.81 F.1.2.1: a negative value is sent as its one's complement in `size` bits.
    private static int Magnitude(int value, int size) => (value < 0 ? value - 1 : value) & ((1 << size) - 1);

    private static void WriteHeaders(
        ArrayBufferWriter<byte> output,
        Image image,
        ReadOnlySpan<byte> profile,
        byte[]? exif,
        byte[]? xmp,
        bool grey,
        int factor,
        byte[] luminance,
        byte[] chrominance,
        bool progressive,
        MetadataOptions options)
    {
        output.Write<byte>([0xFF, 0xD8]);

        // JFIF APP0: version 1.01, no units, aspect ratio 1:1, no thumbnail.
        Segment(output, 0xE0, [.. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0]);

        // EXIF and XMP in APP1, each in one segment; one too large for a segment is left out.
        if (exif is not null && exif.Length + 6 <= 65533)
        {
            Segment(output, 0xE1, [.. "Exif\0\0"u8, .. exif]);
        }

        ReadOnlySpan<byte> xmpPrefix = "http://ns.adobe.com/xap/1.0/\0"u8;
        if (xmp is not null && xmp.Length + xmpPrefix.Length <= 65533)
        {
            Segment(output, 0xE1, [.. xmpPrefix, .. xmp]);
        }

        // IPTC in a Photoshop APP13 segment, when kept and small enough for one.
        if (Metadata.MetadataWriter.PrepareIptc(image, options) is { } iptc && iptc.Length + 40 <= 65533)
        {
            Segment(output, 0xED, Iptc.ToPhotoshop(iptc));
        }

        // The ICC profile in APP2 chunks of at most 65,519 bytes, numbered from 1 (ICC.1, B.4).
        const int Chunk = 65535 - 2 - 14;
        int chunks = (profile.Length + Chunk - 1) / Chunk;
        if (chunks > 255)
        {
            throw new NotSupportedException("The ICC profile is too large to store in a JPEG file (more than 255 segments).");
        }

        for (int i = 0; i < chunks; i++)
        {
            ReadOnlySpan<byte> part = profile.Slice(i * Chunk, Math.Min(Chunk, profile.Length - (i * Chunk)));
            Segment(output, 0xE2, [.. "ICC_PROFILE\0"u8, (byte)(i + 1), (byte)chunks, .. part]);
        }

        // DQT, both tables in zigzag order.
        ReadOnlySpan<byte> zigZag = JpegIdct.ZigZag;
        byte[] dqt = new byte[grey ? 65 : 130];
        for (int t = 0; t < (grey ? 1 : 2); t++)
        {
            byte[] table = t == 0 ? luminance : chrominance;
            dqt[t * 65] = (byte)t;
            for (int k = 0; k < 64; k++)
            {
                dqt[(t * 65) + 1 + k] = table[zigZag[k]];
            }
        }

        Segment(output, 0xDB, dqt);

        // SOF0: 8-bit samples, the size, and the components.
        byte sampling = (byte)((factor << 4) | factor);
        byte[] sof = grey
            ? [8, (byte)(image.Height >> 8), (byte)image.Height, (byte)(image.Width >> 8), (byte)image.Width, 1, 1, 0x11, 0]
            : [8, (byte)(image.Height >> 8), (byte)image.Height, (byte)(image.Width >> 8), (byte)image.Width, 3, 1, sampling, 0, 2, 0x11, 1, 3, 0x11, 1];
        Segment(output, progressive ? (byte)0xC2 : (byte)0xC0, sof);
    }

    // One DHT segment with the tables given: DC and AC in slot 0 (luma), DC and AC in slot 1 (chroma); nulls are left out.
    private static void WriteTables(ArrayBufferWriter<byte> output, JpegHuffmanEncoder? dc0, JpegHuffmanEncoder? ac0, JpegHuffmanEncoder? dc1, JpegHuffmanEncoder? ac1)
    {
        var dht = new ArrayBufferWriter<byte>();
        ReadOnlySpan<byte> slots = [0x00, 0x10, 0x01, 0x11];
        JpegHuffmanEncoder?[] tables = [dc0, ac0, dc1, ac1];
        for (int i = 0; i < 4; i++)
        {
            if (tables[i] is { } table)
            {
                AddTable(dht, slots[i], table);
            }
        }

        if (dht.WrittenCount > 0)
        {
            Segment(output, 0xC4, dht.WrittenSpan);
        }
    }

    private static byte[] EncodeProgressive(
        Image image,
        JpegEncoderOptions options,
        ReadOnlySpan<byte> profile,
        bool grey,
        int factor,
        int mcusWide,
        int mcusHigh,
        byte[] luminance,
        byte[] chrominance,
        Plane[] planes)
    {
        var output = new ArrayBufferWriter<byte>(4096 + profile.Length);
        (byte[]? exif, byte[]? xmp) = Metadata.MetadataWriter.Prepare(image, options.Metadata);
        WriteHeaders(output, image, profile, exif, xmp, grey, factor, luminance, chrominance, progressive: true, options.Metadata);

        // The blocks with samples in each component, for scans of one component (A.2.2), which skip MCU padding.
        int[] usedWide = new int[planes.Length];
        int[] usedHigh = new int[planes.Length];
        for (int c = 0; c < planes.Length; c++)
        {
            int divide = c == 0 ? 1 : factor;
            usedWide[c] = (((image.Width + divide - 1) / divide) + 7) / 8;
            usedHigh[c] = (((image.Height + divide - 1) / divide) + 7) / 8;
        }

        // Each scan is counted, fitted with its own tables and coded on its own, so the scans run in parallel when
        // there are cores for them; they are written in order, so the file is the same either way.
        JpegProgressive.Scan[] script = JpegProgressive.Script(planes.Length);
        var coded = new (JpegHuffmanEncoder? DcLuma, JpegHuffmanEncoder? DcChroma, JpegHuffmanEncoder? Ac, BitWriter Bits)[script.Length];
        int bands = Parallelism.Bands((long)image.Width * image.Height, script.Length);
        Parallelism.For(bands, band =>
        {
            for (int s = band; s < script.Length; s += bands)
            {
                JpegProgressive.Scan scan = script[s];
                JpegProgressive.BlockOrder order = scan.Components.Length > 1
                    ? visit => Walk(planes, mcusWide, factor, 0, mcusHigh, new int[planes.Length], (component, block, _) => visit(component, block))
                    : visit =>
                    {
                        int c = scan.Components[0];
                        for (int y = 0; y < usedHigh[c]; y++)
                        {
                            for (int x = 0; x < usedWide[c]; x++)
                            {
                                visit(c, planes[c].Block(x, y));
                            }
                        }
                    };

                var counting = new CountSink();
                JpegProgressive.Encode(ref counting, scan, order);
                JpegHuffmanEncoder? dcLuma = counting.Used(0) ? JpegHuffmanEncoder.Optimal(counting.Counts[0]) : null;
                JpegHuffmanEncoder? dcChroma = counting.Used(1) ? JpegHuffmanEncoder.Optimal(counting.Counts[1]) : null;
                JpegHuffmanEncoder? ac = counting.Used(2) ? JpegHuffmanEncoder.Optimal(counting.Counts[2]) : null;
                var writing = new WriteSink(new BitWriter(4096), dcLuma, dcChroma, ac);
                JpegProgressive.Encode(ref writing, scan, order);
                coded[s] = (dcLuma, dcChroma, ac, writing.Writer);
            }
        });

        for (int s = 0; s < script.Length; s++)
        {
            JpegProgressive.Scan scan = script[s];
            (JpegHuffmanEncoder? dcLuma, JpegHuffmanEncoder? dcChroma, JpegHuffmanEncoder? ac, BitWriter bits) = coded[s];
            WriteTables(output, dcLuma, ac, dcChroma, null);

            byte[] sos = new byte[1 + (2 * scan.Components.Length) + 3];
            sos[0] = (byte)scan.Components.Length;
            for (int i = 0; i < scan.Components.Length; i++)
            {
                int c = scan.Components[i];
                sos[1 + (2 * i)] = (byte)(c + 1);
                sos[2 + (2 * i)] = (byte)(c == 0 ? 0x00 : 0x10);
            }

            sos[^3] = (byte)scan.Start;
            sos[^2] = (byte)scan.End;
            sos[^1] = (byte)((scan.High << 4) | scan.Low);
            Segment(output, 0xDA, sos);
            var stuffed = new StuffingWriter(output);
            bits.CopyTo(stuffed);
            stuffed.Finish();
        }

        output.Write<byte>([0xFF, 0xD9]);
        return output.WrittenSpan.ToArray();
    }

    private readonly struct CountSink() : JpegProgressive.ISink
    {
        public long[][] Counts { get; } = [new long[256], new long[256], new long[256]];

        public bool Used(int table) => Array.Exists(Counts[table], count => count != 0);

        public void Symbol(int table, int symbol) => Counts[table][symbol]++;

        public void Bits(int value, int length)
        {
        }
    }

    private readonly struct WriteSink(BitWriter writer, JpegHuffmanEncoder? dcLuma, JpegHuffmanEncoder? dcChroma, JpegHuffmanEncoder? ac) : JpegProgressive.ISink
    {
        public BitWriter Writer { get; } = writer;

        public void Symbol(int table, int symbol)
        {
            JpegHuffmanEncoder huffman = (table == 0 ? dcLuma : table == 1 ? dcChroma : ac)!;
            Writer.Write(huffman.Code(symbol), huffman.Length(symbol));
        }

        public void Bits(int value, int length) => Writer.Write(value, length);
    }

    private static void AddTable(ArrayBufferWriter<byte> dht, byte classAndSlot, JpegHuffmanEncoder table)
    {
        dht.Write<byte>([classAndSlot]);
        dht.Write<byte>(table.Counts);
        dht.Write<byte>(table.Values);
    }

    private static void Segment(ArrayBufferWriter<byte> output, byte marker, ReadOnlySpan<byte> data)
    {
        int length = data.Length + 2;
        output.Write<byte>([0xFF, marker, (byte)(length >> 8), (byte)length]);
        output.Write(data);
    }

    /// <summary>One component's quantised coefficients, every block in zigzag order.</summary>
    private sealed class Plane(int blocksWide, int blocksHigh, int factor)
    {
        private readonly short[] _coefficients = new short[(long)blocksWide * blocksHigh * 64];

        public int Factor { get; } = factor;

        public Span<short> Block(int x, int y) => _coefficients.AsSpan(((y * blocksWide) + x) * 64, 64);
    }

    /// <summary>Collects Huffman-coded bits, most significant first, in 32-bit words, with no stuffing yet.</summary>
    private sealed class BitWriter(int capacity)
    {
        private uint[] _words = new uint[Math.Max(capacity / 4, 16)];
        private int _length;
        private ulong _buffer;
        private int _count;

        public void Write(int value, int length)
        {
            _buffer = (_buffer << length) | (uint)(value & ((1 << length) - 1));
            _count += length;
            if (_count >= 32)
            {
                if (_length == _words.Length)
                {
                    Array.Resize(ref _words, (int)Math.Min((long)_words.Length * 2, Array.MaxLength));
                }

                _count -= 32;
                _words[_length++] = (uint)(_buffer >> _count);
            }
        }

        /// <summary>Codes a block: the DC difference, then runs of zeros and values, then an end of block if needed.</summary>
        public void Block(ReadOnlySpan<short> block, int previous, JpegHuffmanEncoder dc, JpegHuffmanEncoder ac)
        {
            // At most 64 codes of 16 + 11 bits, 54 words: room is made once, and the state is held in locals.
            if (_length + 64 > _words.Length)
            {
                Array.Resize(ref _words, (int)Math.Min(((long)_words.Length * 2) + 64, Array.MaxLength));
            }

            uint[] words = _words;
            int length = _length;
            ulong buffer = _buffer;
            int count = _count;
            ReadOnlySpan<ushort> codes = ac.Codes;
            ReadOnlySpan<byte> lengths = ac.Lengths;

            // A code and its value bits go out together.
            int difference = block[0] - previous;
            int size = Category(difference);
            Put(ref buffer, ref count, words, ref length, (dc.Code(size) << size) | Magnitude(difference, size), dc.Length(size) + size);
            ulong mask = NonZero(block) & ~1UL;
            int last = 0;
            while (mask != 0)
            {
                int k = BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                int run = k - last - 1;
                while (run > 15)
                {
                    Put(ref buffer, ref count, words, ref length, codes[0xF0], lengths[0xF0]);
                    run -= 16;
                }

                int value = block[k];
                size = Category(value);
                int symbol = (run << 4) | size;
                Put(ref buffer, ref count, words, ref length, (codes[symbol] << size) | Magnitude(value, size), lengths[symbol] + size);
                last = k;
            }

            if (last < 63)
            {
                Put(ref buffer, ref count, words, ref length, codes[0x00], lengths[0x00]);
            }

            _length = length;
            _buffer = buffer;
            _count = count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Put(ref ulong buffer, ref int count, uint[] words, ref int length, int value, int bits)
        {
            buffer = (buffer << bits) | (uint)value;
            count += bits;
            if (count >= 32)
            {
                count -= 32;
                words[length++] = (uint)(buffer >> count);
            }
        }

        /// <summary>Adds these bits to <paramref name="output"/>, the whole words then the rest.</summary>
        public void CopyTo(StuffingWriter output)
        {
            foreach (uint word in _words.AsSpan(0, _length))
            {
                output.Word(word);
            }

            if (_count > 0)
            {
                output.Bits((uint)_buffer & ((1u << _count) - 1), _count);
            }
        }
    }

    /// <summary>Writes bits as bytes, stuffing a zero after every 0xFF byte (F.1.2.3).</summary>
    private sealed class StuffingWriter(ArrayBufferWriter<byte> output)
    {
        private ulong _buffer;
        private int _count;

        public void Word(uint word)
        {
            _buffer = (_buffer << 32) | word;
            Span<byte> span = output.GetSpan(8);
            int written = 0;

            // Usually none of the four bytes is 0xFF, and they go out in one store.
            uint four = (uint)(_buffer >> _count);
            if (!HasFF(four))
            {
                BinaryPrimitives.WriteUInt32BigEndian(span, four);
                written = 4;
            }
            else
            {
                for (int shift = 24; shift >= 0; shift -= 8)
                {
                    byte b = (byte)(four >> shift);
                    span[written++] = b;
                    if (b == 0xFF)
                    {
                        span[written++] = 0;
                    }
                }
            }

            output.Advance(written);
        }

        public void Bits(uint value, int length)
        {
            _buffer = (_buffer << length) | value;
            _count += length;
            Span<byte> span = output.GetSpan(16);
            int written = 0;
            while (_count >= 8)
            {
                _count -= 8;
                byte b = (byte)(_buffer >> _count);
                span[written++] = b;
                if (b == 0xFF)
                {
                    span[written++] = 0;
                }
            }

            output.Advance(written);
        }

        // Pads the last byte with ones (F.1.2.3).
        public void Finish()
        {
            if (_count > 0)
            {
                int pad = 8 - _count;
                Bits((1u << pad) - 1, pad);
            }
        }

        // Whether any byte is 0xFF: whether ~four has a zero byte.
        private static bool HasFF(uint four) => ((~four - 0x01010101u) & four & 0x80808080u) != 0;
    }
}
