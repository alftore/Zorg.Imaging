using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Codecs.Gif;
using Zorg.Imaging.Formats;
using static Zorg.Imaging.Tests.Codecs.Gif.GifTestFiles;

namespace Zorg.Imaging.Tests.Codecs.Gif;

public class GifDecoderTests
{
    // Red, green, blue, white.
    private static readonly byte[] Four = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255];

    private static readonly DecodeOptions Small = new() { Limits = new ImageLimits { MaxPixels = 1_000_000, MaxFrames = 50 } };

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public void Lzw_round_trips_through_full_tables(int codeSize)
    {
        var random = new Random(codeSize);
        foreach (int length in new[] { 0, 1, 2, 3, 100, 5000, 200_000 })
        {
            // Runs and noise, so the table fills and clears many times.
            byte[] indices = new byte[length];
            for (int i = 0; i < length; i++)
            {
                indices[i] = i % 1000 < 500 ? (byte)((i / 37) & ((1 << codeSize) - 1)) : (byte)random.Next(1 << codeSize);
            }

            var lzw = new ArrayBufferWriter<byte>();
            GifLzw.Encode(indices, codeSize, lzw);
            byte[] back = new byte[length];
            Assert.Equal(length, GifLzw.Decode(Joined(lzw.WrittenSpan), codeSize, back));
            Assert.Equal(indices, back);
        }
    }

    [Fact]
    public void Lzw_without_a_clear_when_the_table_is_full_keeps_12_bit_codes()
    {
        // A deferred clear: after the table fills, the encoder goes on with 12-bit codes for single indices.
        const int CodeSize = 2;
        var codes = new List<(int Code, int Size)> { (4, 3) };
        int next = 6;
        int size = 3;
        var expected = new List<byte>();
        for (int i = 0; i < 6000; i++)
        {
            int index = i & 3;
            codes.Add((index, size));
            expected.Add((byte)index);
            if (i > 0 && next <= 4095)
            {
                next++;
            }

            if (next >= 1 << size && size < 12)
            {
                size++;
            }
        }

        codes.Add((5, size));
        byte[] data = Pack(codes);
        byte[] back = new byte[expected.Count];
        Assert.Equal(expected.Count, GifLzw.Decode(data, CodeSize, back));
        Assert.Equal(expected.ToArray(), back);
    }

    [Fact]
    public void A_still_gif_decodes_to_its_palette_colours()
    {
        byte[] indices = [0, 1, 2, 3, 3, 2, 1, 0];
        byte[] gif = File(Screen(4, 2, Four), Frame(0, 0, 4, 2, indices, codeSize: 2), Trailer);
        using Image image = Image.Decode(gif);
        Assert.Equal(PixelFormat.Rgb24, image.PixelFormat);
        Assert.Equal(new Rgb24(255, 0, 0), image.GetRow<Rgb24>(0)[0]);
        Assert.Equal(new Rgb24(255, 255, 255), image.GetRow<Rgb24>(0)[3]);
        Assert.Equal(new Rgb24(0, 0, 255), image.GetRow<Rgb24>(1)[1]);
    }

    [Fact]
    public void A_local_table_wins_and_the_transparent_index_is_clear()
    {
        byte[] local = [9, 9, 9, 8, 8, 8];
        byte[] gif = File(Screen(2, 1, Four), Control(transparent: 1), Frame(0, 0, 2, 1, [0, 1], local, codeSize: 2), Trailer);
        using Image image = Image.Decode(gif);
        Assert.Equal(PixelFormat.Rgba32, image.PixelFormat);
        Assert.Equal(new Rgba32(9, 9, 9, 255), image.GetRow<Rgba32>(0)[0]);
        Assert.Equal(0, image.GetRow<Rgba32>(0)[1].A);
    }

    [Fact]
    public void A_frame_smaller_than_the_screen_leaves_the_rest_transparent_and_is_clipped()
    {
        byte[] gif = File(Screen(4, 4, Four), Frame(2, 3, 3, 2, [1, 1, 1, 1, 1, 1], codeSize: 2), Trailer);
        using Image image = Image.Decode(gif);
        Assert.Equal((4, 4, PixelFormat.Rgba32), (image.Width, image.Height, image.PixelFormat));
        Assert.Equal(new Rgba32(0, 255, 0, 255), image.GetRow<Rgba32>(3)[2]);
        Assert.Equal(new Rgba32(0, 255, 0, 255), image.GetRow<Rgba32>(3)[3]);
        Assert.Equal(0, image.GetRow<Rgba32>(2)[2].A);
        Assert.Equal(0, image.GetRow<Rgba32>(0)[0].A);
    }

    [Fact]
    public void A_zero_screen_takes_the_first_frames_size()
    {
        byte[] gif = File(Screen(0, 0, Four), Frame(5, 7, 3, 2, [0, 1, 2, 3, 0, 1], codeSize: 2), Trailer);
        using Image image = Image.Decode(gif);
        Assert.Equal((3, 2, PixelFormat.Rgb24), (image.Width, image.Height, image.PixelFormat));
        Assert.Equal(new Rgb24(255, 0, 0), image.GetRow<Rgb24>(0)[0]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(17)]
    public void Interlaced_frames_decode_as_plain_ones(int height)
    {
        byte[] indices = new byte[5 * height];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = (byte)((i / 5) & 3);
        }

        using Image plain = Image.Decode(File(Screen(5, height, Four), Frame(0, 0, 5, height, indices, codeSize: 2), Trailer));
        using Image interlaced = Image.Decode(File(Screen(5, height, Four), Frame(0, 0, 5, height, indices, interlaced: true, codeSize: 2), Trailer));
        Assert.Equal(plain.GetPixelBytes().ToArray(), interlaced.GetPixelBytes().ToArray());

        // Each image row once.
        var rows = new HashSet<int>();
        for (int s = 0; s < height; s++)
        {
            Assert.True(rows.Add(GifReader.InterlacedRow(s, height)));
        }

        Assert.Equal(height, rows.Count);
        Assert.InRange(rows.Max(), 0, height - 1);
    }

    [Fact]
    public async Task Disposal_is_applied_before_the_next_frame()
    {
        // Frame 1 fills the canvas red. Frame 2 (green, left half) is kept (1); frame 3 (blue, right half) is cleared
        // (2); frame 4 (white, middle) is restored to what was under it (3); frame 5 draws one pixel.
        byte[] gif = File(
            Screen(4, 1, Four),
            Frame(0, 0, 4, 1, [0, 0, 0, 0], codeSize: 2),
            Control(disposal: 1),
            Frame(0, 0, 2, 1, [1, 1], codeSize: 2),
            Control(disposal: 2),
            Frame(2, 0, 2, 1, [2, 2], codeSize: 2),
            Control(disposal: 3),
            Frame(1, 0, 2, 1, [3, 3], codeSize: 2),
            Frame(3, 0, 1, 1, [0], codeSize: 2),
            Trailer);

        GifReader reader = await GifReader.OpenAsync(DataReader.FromMemory(gif), ImageLimits.Default, TestContext.Current.CancellationToken);
        const uint R = 0xFFFF0000, G = 0xFF00FF00, B = 0xFF0000FF, W = 0xFFFFFFFF, Clear = 0;
        uint[][] expected =
        [
            [R, R, R, R],
            [G, G, R, R],
            [G, G, B, B],
            [G, W, W, Clear],
            [G, G, Clear, R],
        ];
        foreach (uint[] canvas in expected)
        {
            Assert.True(await reader.NextFrameAsync(TestContext.Current.CancellationToken));
            Assert.Equal(canvas, reader.Canvas);
        }

        Assert.False(await reader.NextFrameAsync(TestContext.Current.CancellationToken));
        Assert.Equal(5, reader.Frames);
    }

    [Fact]
    public void Short_or_bad_image_data_leaves_the_rest_of_the_frame_transparent()
    {
        byte[] full = Frame(0, 0, 4, 4, new byte[16], codeSize: 2);

        // A clear code, then one index and the end code: one pixel of sixteen.
        byte[] shortData = [.. full[..10], 2, .. SubBlocks(Pack([(4, 3), (1, 3), (5, 3)]))];
        using Image cut = Image.Decode(File(Screen(4, 4, Four), shortData, Trailer));
        Assert.Equal(new Rgba32(0, 255, 0, 255), cut.GetRow<Rgba32>(0)[0]);
        Assert.Equal(0, cut.GetRow<Rgba32>(0)[1].A);

        // A code past the table stops decoding.
        byte[] bad = [.. full[..10], 2, .. SubBlocks(Pack([(4, 3), (2, 3), (7, 3), (5, 3)]))];
        using Image stopped = Image.Decode(File(Screen(4, 4, Four), bad, Trailer));
        Assert.Equal(new Rgba32(0, 0, 255, 255), stopped.GetRow<Rgba32>(0)[0]);
        Assert.Equal(0, stopped.GetRow<Rgba32>(0)[1].A);
    }

    [Fact]
    public void A_file_without_a_trailer_still_decodes()
    {
        using Image image = Image.Decode(File(Screen(2, 1, Four), Frame(0, 0, 2, 1, [2, 3], codeSize: 2)));
        Assert.Equal(new Rgb24(0, 0, 255), image.GetRow<Rgb24>(0)[0]);
    }

    [Fact]
    public void Limits_are_checked_before_allocating()
    {
        var tight = new DecodeOptions { Limits = new ImageLimits { MaxPixels = 100 } };
        Assert.Throws<ImageLimitException>(() => Image.Decode(File(Screen(20, 20, Four), Frame(0, 0, 1, 1, [0], codeSize: 2)), tight).Dispose());
        Assert.Throws<ImageLimitException>(() => Image.Decode(File(Screen(5, 5, Four), Frame(0, 0, 60000, 60000, [0], codeSize: 2)), tight).Dispose());
    }

    [Fact]
    public async Task Frames_past_the_limit_are_refused()
    {
        var blocks = new List<byte[]> { Screen(1, 1, Four) };
        for (int i = 0; i < 4; i++)
        {
            blocks.Add(Frame(0, 0, 1, 1, [0], codeSize: 2));
        }

        GifReader reader = await GifReader.OpenAsync(DataReader.FromMemory(File([.. blocks])), new ImageLimits { MaxFrames = 3 }, TestContext.Current.CancellationToken);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(await reader.NextFrameAsync(TestContext.Current.CancellationToken));
        }

        await Assert.ThrowsAsync<ImageLimitException>(async () => await reader.NextFrameAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_bad_code_size_or_block_is_corrupt()
    {
        byte[] frame = Frame(0, 0, 1, 1, [0], codeSize: 2);
        frame[10] = 12;
        Assert.Throws<ImageFormatException>(() => Image.Decode(File(Screen(1, 1, Four), frame)).Dispose());
        Assert.Throws<ImageFormatException>(() => Image.Decode(File(Screen(1, 1, Four), [0x99])).Dispose());
        Assert.Throws<ImageFormatException>(() => Image.Decode(File(Screen(1, 1, Four), Trailer)).Dispose());
        Assert.Throws<ImageFormatException>(() => Image.Decode(File(Screen(1, 1, Four), Frame(0, 0, 9, 9, new byte[81], codeSize: 2)[..20])).Dispose());
    }

    [Fact]
    public void An_icc_profile_extension_sets_the_colour_space()
    {
        byte[] profile = Zorg.Imaging.Metadata.IccProfileWriter.For(ColorSpace.DisplayP3, "GIF").ToArray();
        byte[] extension = [0x21, 0xFF, 11, .. "ICCRGBG1012"u8, .. SubBlocks(profile)];
        using Image image = Image.Decode(File(Screen(1, 1, Four), extension, Frame(0, 0, 1, 1, [0], codeSize: 2), Trailer));
        Assert.Equal(profile, image.ColorSpace.IccProfile.ToArray());
    }

    [Fact]
    public async Task A_stream_decodes_as_memory_does()
    {
        byte[] gif = File(Screen(3, 1, Four), Control(transparent: 0), Frame(0, 0, 3, 1, [0, 1, 2], codeSize: 2), Trailer);
        using Image memory = Image.Decode(gif, new DecodeOptions { PixelFormat = PixelFormat.Bgra32 });
        using Image stream = await Image.DecodeAsync(new Zorg.Imaging.Tests.Codecs.Png.ForwardOnlyStream(gif), new DecodeOptions { PixelFormat = PixelFormat.Bgra32 }, TestContext.Current.CancellationToken);
        Assert.Equal(memory.GetPixelBytes().ToArray(), stream.GetPixelBytes().ToArray());
    }

    [Fact]
    public void Damage_throws_only_our_exceptions_and_never_takes_long()
    {
        byte[] indices = new byte[60 * 40];
        var random = new Random(5);
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = (byte)(i % 7 == 0 ? random.Next(256) : (i / 60) & 0xFF);
        }

        byte[] palette = new byte[768];
        random.NextBytes(palette);
        byte[] gif = File(
            Screen(60, 40, palette),
            Control(disposal: 2, transparent: 3),
            Frame(0, 0, 60, 40, indices),
            Control(disposal: 3),
            Frame(5, 5, 30, 20, indices[..600], palette, interlaced: true),
            Trailer);

        for (int length = 0; length < gif.Length; length += length < 2048 ? 1 : 7)
        {
            AssertSafe(gif[..length], $"cut to {length}");
        }

        for (int round = 0; round < 3000; round++)
        {
            byte[] copy = (byte[])gif.Clone();
            for (int f = random.Next(1, 5); f > 0; f--)
            {
                int at = random.Next(copy.Length);
                copy[at] = random.Next(3) == 0 ? (byte)random.Next(256) : (byte)(copy[at] ^ (1 << random.Next(8)));
            }

            AssertSafe(copy, $"round {round}");
        }
    }

    private static void AssertSafe(byte[] data, string what)
    {
        long started = Stopwatch.GetTimestamp();
        Exception? error = Record.Exception(() =>
        {
            using Image image = Image.Decode(data, Small);
        });
        Assert.True(error is null or ImageException, $"{what}: {error}");
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2), what);
    }

    // Codes packed least significant bit first, as GIF stores them.
    private static byte[] Pack(IEnumerable<(int Code, int Size)> codes)
    {
        var bytes = new List<byte>();
        ulong bits = 0;
        int count = 0;
        foreach ((int code, int size) in codes)
        {
            bits |= (ulong)code << count;
            count += size;
            while (count >= 8)
            {
                bytes.Add((byte)bits);
                bits >>= 8;
                count -= 8;
            }
        }

        if (count > 0)
        {
            bytes.Add((byte)bits);
        }

        return [.. bytes];
    }
}
