using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Numerics;
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

        Transform(image, options.Background, components, mcusWide, mcusHigh, factor, Reciprocals(luminance), Reciprocals(chrominance));

        // Count the symbols each table will code, then build tables that code them in the fewest bits.
        long[][] counts = [new long[256], new long[256], new long[256], new long[256]];
        Walk(components, mcusWide, mcusHigh, factor, (component, block, previous) =>
        {
            int table = component == 0 ? 0 : 2;
            CountBlock(block, previous, counts[table], counts[table + 1]);
        });
        JpegHuffmanEncoder dcLuma = JpegHuffmanEncoder.Optimal(counts[0]);
        JpegHuffmanEncoder acLuma = JpegHuffmanEncoder.Optimal(counts[1]);
        JpegHuffmanEncoder? dcChroma = grey ? null : JpegHuffmanEncoder.Optimal(counts[2]);
        JpegHuffmanEncoder? acChroma = grey ? null : JpegHuffmanEncoder.Optimal(counts[3]);

        var output = new ArrayBufferWriter<byte>(4096 + profile.Length);
        (byte[]? exif, byte[]? xmp) = Metadata.MetadataWriter.Prepare(image, options.Metadata);
        WriteHeaders(output, image, profile, exif, xmp, grey, factor, luminance, chrominance, dcLuma, acLuma, dcChroma, acChroma);

        var bits = new BitWriter(Math.Max(4096, image.Width * image.Height / 4));
        Walk(components, mcusWide, mcusHigh, factor, (component, block, previous) =>
        {
            bool luma = component == 0;
            EncodeBlock(bits, block, previous, luma ? dcLuma : dcChroma!, luma ? acLuma : acChroma!);
        });
        bits.Flush();

        byte[] file = new byte[output.WrittenCount + bits.Length + 2];
        output.WrittenSpan.CopyTo(file);
        bits.Data.CopyTo(file.AsSpan(output.WrittenCount));
        file[^2] = 0xFF;
        file[^1] = 0xD9;
        return file;
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

    // Converts, centres, transforms and quantises every block, a band of MCU rows per thread.
    private static void Transform(Image image, Rgba32 background, Plane[] planes, int mcusWide, int mcusHigh, int factor, float[] lumaQ, float[] chromaQ)
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
                        JpegFdct.Transform(y.AsSpan((by * 8 * width) + (bx * 8)), width, lumaQ, planes[0].Block(bx, (row * factor) + by));
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
                    JpegFdct.Transform(cbHalf.AsSpan(mx * 8), halfWidth, chromaQ, planes[1].Block(mx, row));
                    JpegFdct.Transform(crHalf.AsSpan(mx * 8), halfWidth, chromaQ, planes[2].Block(mx, row));
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

    // Visits blocks in interleaved MCU order, giving each component's previous DC for the difference.
    private static void Walk(Plane[] planes, int mcusWide, int mcusHigh, int factor, BlockVisitor visit)
    {
        int[] previous = new int[planes.Length];
        for (int my = 0; my < mcusHigh; my++)
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

    private static void CountBlock(ReadOnlySpan<short> block, int previous, long[] dc, long[] ac)
    {
        dc[Category(block[0] - previous)]++;
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

    private static void EncodeBlock(BitWriter bits, ReadOnlySpan<short> block, int previous, JpegHuffmanEncoder dc, JpegHuffmanEncoder ac)
    {
        int difference = block[0] - previous;
        int size = Category(difference);

        // A code and its value bits go out together: at most 16 + 11 bits.
        bits.Write((dc.Code(size) << size) | Magnitude(difference, size), dc.Length(size) + size);
        ulong mask = NonZero(block) & ~1UL;
        int last = 0;
        while (mask != 0)
        {
            int k = BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;
            int run = k - last - 1;
            while (run > 15)
            {
                bits.Write(ac.Code(0xF0), ac.Length(0xF0));
                run -= 16;
            }

            int value = block[k];
            size = Category(value);
            int symbol = (run << 4) | size;
            bits.Write((ac.Code(symbol) << size) | Magnitude(value, size), ac.Length(symbol) + size);
            last = k;
        }

        if (last < 63)
        {
            bits.Write(ac.Code(0x00), ac.Length(0x00));
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
        JpegHuffmanEncoder dcLuma,
        JpegHuffmanEncoder acLuma,
        JpegHuffmanEncoder? dcChroma,
        JpegHuffmanEncoder? acChroma)
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
        Segment(output, 0xC0, sof);

        // DHT: DC then AC, luma (table 0) and chroma (table 1).
        var dht = new ArrayBufferWriter<byte>();
        AddTable(dht, 0x00, dcLuma);
        AddTable(dht, 0x10, acLuma);
        if (dcChroma is not null && acChroma is not null)
        {
            AddTable(dht, 0x01, dcChroma);
            AddTable(dht, 0x11, acChroma);
        }

        Segment(output, 0xC4, dht.WrittenSpan);

        // SOS: every component, full spectrum, no approximation.
        Segment(output, 0xDA, grey ? [1, 1, 0x00, 0, 63, 0] : [3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0]);
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

    /// <summary>Writes Huffman-coded bits, most significant first, stuffing a zero after every 0xFF byte (F.1.2.3).</summary>
    private sealed class BitWriter(int capacity)
    {
        private byte[] _data = new byte[capacity];
        private ulong _buffer;
        private int _count;

        public int Length { get; private set; }

        public ReadOnlySpan<byte> Data => _data.AsSpan(0, Length);

        public void Write(int value, int length)
        {
            if (length == 0)
            {
                return;
            }

            _buffer = (_buffer << length) | (uint)(value & ((1 << length) - 1));
            _count += length;
            if (_count < 32)
            {
                return;
            }

            // Four or more bytes are ready: at most eight come out, each perhaps with a stuffed zero.
            if (Length + 16 > _data.Length)
            {
                Array.Resize(ref _data, (int)Math.Min((long)_data.Length * 2, Array.MaxLength));
            }

            // Usually none of the next four bytes is 0xFF, and they go out in one store.
            uint four = (uint)(_buffer >> (_count - 32));
            if (((four & 0x80808080u) & ~(four + 0x01010101u)) == 0 || !HasFF(four))
            {
                BinaryPrimitives.WriteUInt32BigEndian(_data.AsSpan(Length, 4), four);
                Length += 4;
                _count -= 32;
                return;
            }

            while (_count >= 8)
            {
                _count -= 8;
                byte b = (byte)(_buffer >> _count);
                _data[Length++] = b;
                if (b == 0xFF)
                {
                    _data[Length++] = 0;
                }
            }
        }

        // Pads the last byte with ones (F.1.2.3), after writing out whole bytes still held.
        public void Flush()
        {
            if (Length + 16 > _data.Length)
            {
                Array.Resize(ref _data, _data.Length + 16);
            }

            if (_count % 8 != 0)
            {
                int pad = 8 - (_count % 8);
                _buffer = (_buffer << pad) | (uint)((1 << pad) - 1);
                _count += pad;
            }

            while (_count >= 8)
            {
                _count -= 8;
                byte b = (byte)(_buffer >> _count);
                _data[Length++] = b;
                if (b == 0xFF)
                {
                    _data[Length++] = 0;
                }
            }
        }

        private static bool HasFF(uint four) =>
            (four & 0xFF000000u) == 0xFF000000u || (four & 0x00FF0000u) == 0x00FF0000u
            || (four & 0x0000FF00u) == 0x0000FF00u || (four & 0x000000FFu) == 0x000000FFu;
    }
}
