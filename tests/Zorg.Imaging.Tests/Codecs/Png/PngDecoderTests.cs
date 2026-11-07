using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Formats;
using static Zorg.Imaging.Tests.Codecs.Png.PngTestFiles;

namespace Zorg.Imaging.Tests.Codecs.Png;

public class PngDecoderTests
{
    private static readonly DecodeOptions AsRgba = new() { PixelFormat = PixelFormat.Rgba32 };

    public static TheoryData<int, int, bool> Kinds()
    {
        var data = new TheoryData<int, int, bool>();
        (int Colour, int[] Depths)[] allowed = [(0, [1, 2, 4, 8, 16]), (2, [8, 16]), (3, [1, 2, 4, 8]), (4, [8, 16]), (6, [8, 16])];
        foreach ((int colour, int[] depths) in allowed)
        {
            foreach (int depth in depths)
            {
                data.Add(colour, depth, false);
                data.Add(colour, depth, true);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_colour_type_and_bit_depth_decodes_to_the_right_pixels(int colourType, int depth, bool interlaced)
    {
        foreach ((int width, int height) in new[] { (13, 11), (1, 1), (3, 2), (9, 1), (1, 9) })
        {
            byte[]? palette = colourType == 3 ? RandomPalette(1 << depth, seed: depth) : null;
            int[] samples = RandomSamples(width, height, depth, colourType, seed: (width * 31) + depth, paletteSize: 1 << depth);
            (string, byte[])[] before = palette is null ? [] : [("PLTE", palette)];
            byte[] png = Build(width, height, depth, colourType, samples, interlaced, before);

            using Image rgba = Image.Decode(png, AsRgba);
            Assert.Equal(ExpectedRgba(width, height, depth, colourType, samples, palette), Pixels(rgba));

            byte[] rgba64 = ExpectedRgba64(width, height, depth, colourType, samples, palette);
            using Image wide = Image.Decode(png, new DecodeOptions { PixelFormat = PixelFormat.Rgba64 });
            Assert.Equal(rgba64, Pixels(wide));

            using Image natural = Image.Decode(png);
            int channels = colourType switch
            {
                0 => 1,
                2 or 3 => 3,
                _ => 4,
            };
            PixelFormat expected = (channels, depth == 16) switch
            {
                (1, false) => PixelFormat.L8,
                (3, false) => PixelFormat.Rgb24,
                (4, false) => PixelFormat.Rgba32,
                (1, true) => PixelFormat.L16,
                (3, true) => PixelFormat.Rgb48,
                _ => PixelFormat.Rgba64,
            };
            Assert.Equal(expected, natural.PixelFormat);
            Assert.Equal((width, height), (natural.Width, natural.Height));
            Assert.Equal(
                depth == 16 ? Narrow(rgba64, channels, 2) : Narrow(ExpectedRgba(width, height, depth, colourType, samples, palette), channels),
                Pixels(natural));
        }
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(0, 4)]
    [InlineData(2, 8)]
    [InlineData(2, 16)]
    public void A_tRNS_colour_becomes_transparent(int colourType, int depth)
    {
        int[] samples = RandomSamples(8, 8, depth, colourType, seed: 5);
        byte[] trns = new byte[colourType == 0 ? 2 : 6];
        for (int c = 0; c < trns.Length / 2; c++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(trns.AsSpan(c * 2), (ushort)samples[c]);
        }

        byte[] png = Build(8, 8, depth, colourType, samples, before: ("tRNS", trns));

        using Image image = Image.Decode(png);

        if (depth == 16)
        {
            Assert.Equal(PixelFormat.Rgba64, image.PixelFormat);
            Assert.Equal(ExpectedRgba64(8, 8, depth, colourType, samples, trns: trns), Pixels(image));
            Assert.Equal(0, image.GetRow<Rgba64>(0)[0].A);
        }
        else
        {
            Assert.Equal(PixelFormat.Rgba32, image.PixelFormat);
            Assert.Equal(ExpectedRgba(8, 8, depth, colourType, samples, trns: trns), Pixels(image));
            Assert.Equal(0, image.GetRow<Rgba32>(0)[0].A);
        }
    }

    [Fact]
    public void Palette_tRNS_sets_alpha_per_entry_and_indices_past_the_palette_are_opaque_black()
    {
        byte[] palette = RandomPalette(5, seed: 1);
        byte[] trns = [0, 128, 255];
        int[] samples = RandomSamples(16, 4, 8, 3, seed: 2, paletteSize: 9);

        byte[] png = Build(16, 4, 8, 3, samples, interlaced: true, ("PLTE", palette), ("tRNS", trns));
        using Image image = Image.Decode(png);

        Assert.Equal(PixelFormat.Rgba32, image.PixelFormat);
        Assert.Equal(ExpectedRgba(16, 4, 8, 3, samples, palette, trns), Pixels(image));
    }

    [Fact]
    public void Sixteen_bit_samples_round_to_the_nearest_8_bit_value()
    {
        // 254.5 x 257 = 65406.5, so 65406 is the last sample that rounds down to 254.
        int[] samples = [0, 128, 129, 385, 386, 32767, 32768, 65406, 65407, 65535];
        byte[] png = Build(samples.Length, 1, 16, 0, samples);

        using Image image = Image.Decode(png, AsRgba);

        Assert.Equal([0, 0, 1, 1, 2, 127, 128, 254, 255, 255], Narrow(Pixels(image), 1));
    }

    [Fact]
    public void Bgra32_can_be_asked_for()
    {
        int[] samples = RandomSamples(5, 3, 8, 6, seed: 9);
        byte[] png = Build(5, 3, 8, 6, samples);

        using Image image = Image.Decode(png, new DecodeOptions { PixelFormat = PixelFormat.Bgra32 });

        byte[] rgba = ExpectedRgba(5, 3, 8, 6, samples);
        Assert.Equal(PixelFormat.Bgra32, image.PixelFormat);
        Assert.Equal(new Bgra32(rgba[2], rgba[1], rgba[0], rgba[3]), image.GetRow<Bgra32>(0)[0]);
    }

    [Theory]
    [InlineData(PixelFormat.Rgb24)]
    [InlineData(PixelFormat.L8)]
    [InlineData((PixelFormat)42)]
    public void Formats_that_would_lose_alpha_or_colour_cant_be_asked_for(PixelFormat format) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodeOptions { PixelFormat = format });

    [Theory]
    [InlineData(1, 3, 2, new byte[] { 1, 2, 3, 4, 5, 6 })]
    [InlineData(2, 3, 2, new byte[] { 3, 2, 1, 6, 5, 4 })]
    [InlineData(3, 3, 2, new byte[] { 6, 5, 4, 3, 2, 1 })]
    [InlineData(4, 3, 2, new byte[] { 4, 5, 6, 1, 2, 3 })]
    [InlineData(5, 2, 3, new byte[] { 1, 4, 2, 5, 3, 6 })]
    [InlineData(6, 2, 3, new byte[] { 4, 1, 5, 2, 6, 3 })]
    [InlineData(7, 2, 3, new byte[] { 6, 3, 5, 2, 4, 1 })]
    [InlineData(8, 2, 3, new byte[] { 3, 6, 2, 5, 1, 4 })]
    public void Orientation_is_applied_so_the_image_is_upright(int orientation, int width, int height, byte[] expected)
    {
        // Stored 3 x 2:   1 2 3
        //                 4 5 6
        byte[] png = Build(3, 2, 8, 0, [1, 2, 3, 4, 5, 6], before: ("eXIf", ExifWithOrientation(orientation)));

        using Image upright = Image.Decode(png);
        using Image stored = Image.Decode(png, new DecodeOptions { ApplyOrientation = false });
        IdentifyResult identify = Image.Identify(png);

        Assert.Equal((width, height), (upright.Width, upright.Height));
        Assert.Equal(expected, Pixels(upright));
        Assert.Equal((identify.Info!.DisplayWidth, identify.Info.DisplayHeight), (upright.Width, upright.Height));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, Pixels(stored));
    }

    [Fact]
    public void Orientation_works_for_every_pixel_size()
    {
        int[] samples = RandomSamples(5, 3, 8, 6, seed: 4);
        byte[] png = Build(5, 3, 8, 6, samples, before: ("eXIf", ExifWithOrientation(6)));

        using Image rgba = Image.Decode(png);
        using Image stored = Image.Decode(png, new DecodeOptions { ApplyOrientation = false });

        // Turned 90° clockwise: the top row of the result is the left column of the stored image, bottom to top.
        Assert.Equal((3, 5), (rgba.Width, rgba.Height));
        for (int x = 0; x < 3; x++)
        {
            Assert.Equal(stored.GetRow<Rgba32>(2 - x)[0], rgba.GetRow<Rgba32>(0)[x]);
        }
    }

    [Fact]
    public void An_eXIf_after_the_image_data_is_ignored_as_Identify_ignores_it()
    {
        byte[] ihdr = Ihdr(3, 2, 8, 0);
        byte[] idat = Compress(ImageData(3, 2, 8, 0, [1, 2, 3, 4, 5, 6], interlaced: false));
        byte[] png = Assemble(ihdr, [], [idat], ("eXIf", ExifWithOrientation(6)));

        using Image image = Image.Decode(png);

        Assert.Equal((3, 2), (image.Width, image.Height));
    }

    [Fact]
    public void Image_data_split_into_many_chunks_decodes_the_same()
    {
        int[] samples = RandomSamples(20, 20, 8, 6, seed: 3);
        byte[] whole = Compress(ImageData(20, 20, 8, 6, samples, interlaced: true));
        byte[][] pieces = [.. whole.Chunk(7), [], []];
        byte[] png = Assemble(Ihdr(20, 20, 8, 6, interlaced: true), [], pieces);

        using Image image = Image.Decode(png);

        Assert.Equal(ExpectedRgba(20, 20, 8, 6, samples), Pixels(image));
    }

    [Fact]
    public void Unknown_ancillary_chunks_are_skipped_anywhere()
    {
        int[] samples = RandomSamples(4, 4, 8, 2, seed: 8);
        byte[] png = Assemble(
            Ihdr(4, 4, 8, 2),
            [("tEXt", "Comment\0hi"u8.ToArray()), ("zzZz", new byte[1000])],
            [Compress(ImageData(4, 4, 8, 2, samples, interlaced: false))],
            ("tIME", new byte[7]),
            ("abCd", []));

        using Image image = Image.Decode(png, AsRgba);

        Assert.Equal(ExpectedRgba(4, 4, 8, 2, samples), Pixels(image));
    }

    [Fact]
    public void A_tRNS_in_an_image_with_alpha_is_ignored()
    {
        int[] samples = RandomSamples(4, 4, 8, 6, seed: 8);
        byte[] png = Build(4, 4, 8, 6, samples, before: ("tRNS", new byte[6]));

        using Image image = Image.Decode(png);

        Assert.Equal(ExpectedRgba(4, 4, 8, 6, samples), Pixels(image));
    }

    [Fact]
    public void A_palette_in_an_RGB_image_is_checked_then_ignored()
    {
        int[] samples = RandomSamples(4, 4, 8, 2, seed: 8);
        byte[] png = Build(4, 4, 8, 2, samples, before: ("PLTE", RandomPalette(3, seed: 1)));

        using Image image = Image.Decode(png);

        Assert.Equal(PixelFormat.Rgb24, image.PixelFormat);
    }

    [Fact]
    public async Task Streams_decode_the_same_as_memory_whether_or_not_they_seek()
    {
        int[] samples = RandomSamples(40, 30, 16, 6, seed: 12);
        byte[] png = Build(40, 30, 16, 6, samples, interlaced: true);
        byte[] expected = ExpectedRgba64(40, 30, 16, 6, samples);

        using var seekable = new MemoryStream(png);
        using Image fromSeekable = await Image.DecodeAsync(seekable, cancellationToken: TestContext.Current.CancellationToken);
        using var forwardOnly = new ForwardOnlyStream(png);
        using Image fromForwardOnly = await Image.DecodeAsync(forwardOnly, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected, Pixels(fromSeekable));
        Assert.Equal(expected, Pixels(fromForwardOnly));
    }

    [Fact]
    public async Task A_seekable_stream_is_left_just_after_the_image()
    {
        byte[] png = Build(4, 4, 8, 0, RandomSamples(4, 4, 8, 0, seed: 1));
        byte[] withTail = [.. png, .. "trailing bytes"u8];
        using var stream = new MemoryStream(withTail);

        using Image image = await Image.DecodeAsync(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(png.Length, stream.Position);
    }

    [Fact]
    public async Task A_peeked_stream_decodes_after_Identify()
    {
        int[] samples = RandomSamples(30, 20, 8, 3, seed: 3, paletteSize: 200);
        byte[] palette = RandomPalette(200, seed: 3);
        byte[] png = Build(30, 20, 8, 3, samples, interlaced: false, ("PLTE", palette), ("tEXt", new byte[5000]));
        using var body = new ForwardOnlyStream(png);

        FormatPeek peek = await ImageFormats.PeekAsync(body, TestContext.Current.CancellationToken);
        IdentifyResult identify = await Image.IdentifyAsync(peek.Stream, cancellationToken: TestContext.Current.CancellationToken);
        using Image image = await Image.DecodeAsync(peek.Stream, AsRgba, TestContext.Current.CancellationToken);

        Assert.True(identify.Succeeded);
        Assert.Equal(ExpectedRgba(30, 20, 8, 3, samples, palette), Pixels(image));
    }

    [Fact]
    public void Formats_without_a_decoder_say_so()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, .. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0];

        UnsupportedImageFormatException notYet = Assert.Throws<UnsupportedImageFormatException>(() => Image.Decode(jpeg));
        UnsupportedImageFormatException notImage = Assert.Throws<UnsupportedImageFormatException>(() => Image.Decode("%PDF-1.7"u8.ToArray()));
        Assert.Throws<UnsupportedImageFormatException>(() => Image.Decode([]));

        Assert.Same(ImageFormat.Jpeg, notYet.Format);
        Assert.Contains("JPEG", notYet.Message, StringComparison.Ordinal);
        Assert.Null(notImage.Format);
    }

    [Fact]
    public void An_image_over_MaxPixels_is_refused_before_anything_is_allocated()
    {
        // 100000 x 100000 with a few bytes of data: refused from IHDR alone.
        byte[] png = Assemble(Ihdr(100_000, 100_000, 8, 6), [], [Compress([0])]);

        long before = GC.GetAllocatedBytesForCurrentThread();
        ImageLimitException limit = Assert.Throws<ImageLimitException>(() => Image.Decode(png));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Contains("MaxPixels", limit.Message, StringComparison.Ordinal);
        Assert.True(allocated < 100_000, $"{allocated} bytes allocated");
        Assert.Equal(IdentifyStatus.ExceedsLimits, limit.Status);
    }

    [Fact]
    public void A_raised_MaxPixels_still_stops_at_what_one_array_holds()
    {
        byte[] png = Assemble(Ihdr(60_000, 60_000, 8, 6), [], [Compress([0])]);
        var options = new DecodeOptions { Limits = new ImageLimits { MaxPixels = long.MaxValue } };

        Assert.Throws<ImageLimitException>(() => Image.Decode(png, options));
    }

    [Fact]
    public void A_zlib_bomb_costs_no_more_than_the_image()
    {
        // 64 MiB of zeros compresses to about 64 KiB; the image needs only 10 rows of 41 bytes.
        byte[] zeros = new byte[64 * 1024 * 1024];
        byte[] bomb;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(zeros);
            }

            bomb = output.ToArray();
        }

        byte[] png = Assemble(Ihdr(10, 10, 8, 6), [], [bomb]);

        long before = GC.GetAllocatedBytesForCurrentThread();
        using Image image = Image.Decode(png);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(-1, image.GetPixelBytes().IndexOfAnyExcept((byte)0));
        Assert.True(allocated < 1_000_000, $"{allocated} bytes allocated");
    }

    public static TheoryData<string, Func<byte[], byte[]>> Damage => new()
    {
        { "bad IHDR CRC", png => Flip(png, 8 + 8 + 13) },
        { "bad IDAT CRC", png => Flip(png, ChunkEnd(png, "IDAT") - 1) },
        { "bad PLTE CRC", png => Flip(png, ChunkEnd(png, "PLTE") - 1) },
        { "filter type 5", png => Rebuild(png, rows => rows[0] = 5) },
        { "bit depth 3", png => Patch(png, 8 + 8 + 8, 3) },
        { "colour type 5", png => Patch(png, 8 + 8 + 9, 5) },
        { "interlace 2", png => Patch(png, 8 + 8 + 12, 2) },
        { "zero width", png => Patch(png, 8 + 8 + 3, 0) },
    };

    [Theory]
    [MemberData(nameof(Damage))]
    public void Damaged_files_throw_ImageFormatException(string what, Func<byte[], byte[]> damage)
    {
        byte[] palette = RandomPalette(4, seed: 1);
        byte[] png = Build(6, 5, 8, 3, RandomSamples(6, 5, 8, 3, seed: 1, paletteSize: 4), interlaced: false, ("PLTE", palette));

        ImageFormatException ex = Assert.ThrowsAny<ImageFormatException>(() => Image.Decode(damage(png)));

        Assert.False(string.IsNullOrEmpty(ex.Message), what);
    }

    [Fact]
    public void Structural_mistakes_are_refused()
    {
        int[] samples = RandomSamples(4, 4, 8, 3, seed: 1, paletteSize: 4);
        byte[] palette = RandomPalette(4, seed: 1);
        byte[] idat = Compress(ImageData(4, 4, 8, 3, samples, interlaced: false));
        byte[] ihdr = Ihdr(4, 4, 8, 3);

        AssertCorrupt(Assemble(ihdr, [], [idat]), "no PLTE");
        AssertCorrupt(Assemble(ihdr, [("PLTE", palette), ("PLTE", palette)], [idat]), "two PLTE");
        AssertCorrupt(Assemble(ihdr, [("tRNS", [1]), ("PLTE", palette)], [idat]), "tRNS before PLTE");
        AssertCorrupt(Assemble(ihdr, [("PLTE", palette), ("tRNS", new byte[5])], [idat]), "tRNS longer than the palette");
        AssertCorrupt(Assemble(ihdr, [("PLTE", new byte[4])], [idat]), "PLTE not a multiple of 3");
        AssertCorrupt(Assemble(Ihdr(4, 4, 1, 3), [("PLTE", RandomPalette(3, 1))], [idat]), "palette longer than 1-bit indices reach");
        AssertCorrupt(Assemble(Ihdr(4, 4, 8, 0), [("PLTE", palette)], [idat]), "PLTE in a grey image");
        AssertCorrupt(Assemble(ihdr, [("PLTE", palette)], [idat[..10]], ("tEXt", []), ("IDAT", idat[10..])), "IDAT run broken");
        AssertCorrupt(Assemble(ihdr, [("PLTE", palette)], [Compress(new byte[10])]), "too little image data");
        AssertCorrupt(Assemble(ihdr, [("PLTE", palette)], [idat], ("PLTE", palette)), "PLTE after IDAT");
        AssertCorrupt([.. Assemble(ihdr, [("PLTE", palette)], [idat])[..^12], .. Chunk("IEND", [1])], "IEND with data");
        AssertCorrupt([.. Assemble(ihdr, [("PLTE", palette)], [])], "IEND before IDAT");
        AssertCorrupt(Assemble(ihdr, [("PLTE", palette)], [[.. idat[..^4], 0, 0, 0, 0]]), "bad Adler-32");
    }

    [Fact]
    public void A_file_cut_short_is_truncated()
    {
        byte[] png = Build(6, 5, 8, 2, RandomSamples(6, 5, 8, 2, seed: 1));

        ImageFormatException noIend = Assert.ThrowsAny<ImageFormatException>(() => Image.Decode(png[..^12]));
        ImageFormatException midIdat = Assert.ThrowsAny<ImageFormatException>(() => Image.Decode(png[..(ChunkEnd(png, "IDAT") - 20)]));

        Assert.Equal(IdentifyStatus.Truncated, noIend.Status);
        Assert.Equal(IdentifyStatus.Truncated, midIdat.Status);
    }

    [Fact]
    public void Unknown_critical_chunks_and_CgBI_are_unsupported()
    {
        byte[] idat = Compress(ImageData(2, 2, 8, 0, [1, 2, 3, 4], interlaced: false));
        byte[] critical = Assemble(Ihdr(2, 2, 8, 0), [("ABCD", [])], [idat]);
        byte[] cgbi = [.. critical[..8], .. Chunk("CgBI", new byte[4]), .. critical[8..]];

        Assert.Contains("ABCD", Assert.Throws<UnsupportedImageFormatException>(() => Image.Decode(critical)).Message, StringComparison.Ordinal);
        Assert.Contains("CgBI", Assert.Throws<UnsupportedImageFormatException>(() => Image.Decode(cgbi)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_chunk_length_over_2_GB_is_refused_without_reading()
    {
        byte[] png = Build(2, 2, 8, 0, [1, 2, 3, 4], before: ("tEXt", []));
        int text = Chunks(png).Find(c => c.Type == "tEXt").Offset;
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(text), 0x8000_0000);

        Assert.Equal(IdentifyStatus.Corrupt, Assert.ThrowsAny<ImageFormatException>(() => Image.Decode(png)).Status);
    }

    internal static byte[] RandomPalette(int entries, int seed)
    {
        byte[] palette = new byte[entries * 3];
        new Random(seed).NextBytes(palette);
        return palette;
    }

    private static void AssertCorrupt(byte[] png, string what) =>
        Assert.True(Record.Exception(() => Image.Decode(png)) is ImageFormatException, what);

    private static int ChunkEnd(byte[] png, string type)
    {
        int at = Chunks(png).Find(c => c.Type == type).Offset;
        return at + 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
    }

    private static byte[] Flip(byte[] png, int at)
    {
        byte[] copy = (byte[])png.Clone();
        copy[at] ^= 0x10;
        return copy;
    }

    private static byte[] Patch(byte[] png, int at, byte value)
    {
        byte[] copy = (byte[])png.Clone();
        copy[at] = value;
        int crcAt = 8 + 8 + 13;
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(crcAt), Zorg.Imaging.Codecs.Png.Crc32.Compute(copy.AsSpan(12, 17)));
        return copy;
    }

    // Inflates the IDAT, lets the caller change the filtered rows, and builds the file again with good CRCs.
    private static byte[] Rebuild(byte[] png, Action<byte[]> change)
    {
        int at = Chunks(png).Find(c => c.Type == "IDAT").Offset;
        int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
        using var input = new ZLibStream(new MemoryStream(png, at + 8, length), CompressionMode.Decompress);
        using var rows = new MemoryStream();
        input.CopyTo(rows);
        byte[] data = rows.ToArray();
        change(data);
        return [.. png[..at], .. Chunk("IDAT", Compress(data)), .. png[(at + 12 + length)..]];
    }
}

/// <summary>A stream that can't seek and hands out a few bytes at a time, like a network body.</summary>
internal sealed class ForwardOnlyStream(byte[] data) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int count = Math.Min(Math.Min(buffer.Length, 1000), data.Length - _position);
        data.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
