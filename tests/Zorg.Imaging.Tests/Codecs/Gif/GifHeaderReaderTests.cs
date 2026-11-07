using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Codecs.Gif;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Tests.Codecs.Gif;

public class GifHeaderReaderTests
{
    private const int Budget = 1024 * 1024;

    private static readonly IdentifyOptions Counting = new() { CountFrames = true };

    [Theory]
    [InlineData("87a")]
    [InlineData("89a")]
    public async Task A_still_gif_is_read(string version)
    {
        ImageInfo info = await ReadAsync(Gif(version, Screen(30, 20), Frame(30, 20), [0x3B]));

        Assert.Equal(ImageFormat.Gif, info.Format);
        Assert.Equal(30, info.Width);
        Assert.Equal(20, info.Height);
        Assert.Null(info.FrameCount);
        Assert.False(info.IsAnimated);
        Assert.False(info.HasAlpha);
        Assert.False(info.HasExif);
        Assert.Equal(1, info.Orientation);
    }

    [Fact]
    public async Task A_still_gif_is_counted_as_one_frame()
    {
        ImageInfo info = await ReadAsync(Gif("89a", Screen(30, 20), Frame(30, 20), [0x3B]), Counting);

        Assert.Equal(1, info.FrameCount);
        Assert.False(info.IsAnimated);
    }

    [Fact]
    public async Task A_graphic_control_extension_with_transparency_means_alpha()
    {
        ImageInfo info = await ReadAsync(Gif("89a", Screen(3, 3), GraphicControl(transparent: true), Frame(3, 3), [0x3B]));
        Assert.True(info.HasAlpha);
    }

    [Fact]
    public async Task A_graphic_control_extension_without_transparency_does_not()
    {
        ImageInfo info = await ReadAsync(Gif("89a", Screen(3, 3), GraphicControl(transparent: false), Frame(3, 3), [0x3B]));
        Assert.False(info.HasAlpha);
    }

    [Fact]
    public async Task Transparency_after_the_first_frame_is_only_seen_when_counting()
    {
        byte[] data = Gif("89a", Screen(3, 3), Frame(3, 3), GraphicControl(transparent: true), Frame(3, 3), [0x3B]);

        Assert.False((await ReadAsync(data)).HasAlpha);
        Assert.True((await ReadAsync(data, Counting)).HasAlpha);
    }

    [Theory]
    [InlineData("NETSCAPE2.0")]
    [InlineData("ANIMEXTS1.0")]
    public async Task A_loop_extension_means_animated(string name)
    {
        byte[] data = Gif("89a", Screen(3, 3), Application(name, [1, 0, 0]), Frame(3, 3), [0x3B]);

        ImageInfo info = await ReadAsync(data);
        Assert.True(info.IsAnimated);
        Assert.Null(info.FrameCount);

        ImageInfo counted = await ReadAsync(data, Counting);
        Assert.True(counted.IsAnimated);
        Assert.Equal(1, counted.FrameCount);
    }

    [Fact]
    public async Task An_ICC_application_extension_means_an_ICC_profile()
    {
        ImageInfo info = await ReadAsync(Gif("89a", Screen(3, 3), Application("ICCRGBG1012", new byte[40]), Frame(3, 3), [0x3B]));
        Assert.True(info.HasIccProfile);
    }

    [Fact]
    public async Task Other_extensions_are_skipped()
    {
        byte[] comment = [0x21, 0xFE, 5, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 0];
        byte[] data = Gif("89a", Screen(7, 8), comment, Application("SOMEAPP0.1", [1, 2]), Frame(7, 8), [0x3B]);

        ImageInfo info = await ReadAsync(data);

        Assert.Equal(7, info.Width);
        Assert.False(info.IsAnimated);
    }

    [Fact]
    public async Task Several_frames_are_counted_only_when_asked()
    {
        byte[] data = Gif("89a", Screen(4, 4), Frame(4, 4), Frame(4, 4), Frame(4, 4), [0x3B]);

        ImageInfo plain = await ReadAsync(data);
        Assert.Null(plain.FrameCount);
        Assert.False(plain.IsAnimated);

        ImageInfo counted = await ReadAsync(data, Counting);
        Assert.Equal(3, counted.FrameCount);
        Assert.True(counted.IsAnimated);
    }

    [Fact]
    public async Task Exceeding_MaxFrames_while_counting_stops_with_ExceedsLimits()
    {
        byte[] data = Gif("89a", Screen(4, 4), Frame(4, 4), Frame(4, 4), Frame(4, 4), [0x3B]);
        var options = new IdentifyOptions { CountFrames = true, Limits = new ImageLimits { MaxFrames = 2 } };

        ImageException failure = await FailAsync(data, options);

        Assert.Equal(IdentifyStatus.ExceedsLimits, failure.Status);
    }

    [Fact]
    public async Task Exactly_MaxFrames_frames_is_allowed()
    {
        byte[] data = Gif("89a", Screen(4, 4), Frame(4, 4), Frame(4, 4), [0x3B]);
        var options = new IdentifyOptions { CountFrames = true, Limits = new ImageLimits { MaxFrames = 2 } };

        ImageInfo info = await ReadAsync(data, options);

        Assert.Equal(2, info.FrameCount);
    }

    [Fact]
    public async Task The_frame_limit_is_hit_before_the_rest_of_the_file_is_read()
    {
        // Frames follow forever in the data, but the third frame's descriptor stops the walk (the data after is garbage).
        byte[] data = Gif("89a", Screen(4, 4), Frame(4, 4), Frame(4, 4), [0x2C, 0, 0, 0, 0, 4, 0, 4, 0, 0]);
        var options = new IdentifyOptions { CountFrames = true, Limits = new ImageLimits { MaxFrames = 2 } };

        ImageException failure = await FailAsync(data, options);

        Assert.Equal(IdentifyStatus.ExceedsLimits, failure.Status);
    }

    [Fact]
    public async Task A_missing_trailer_after_a_frame_ends_the_walk()
    {
        ImageInfo info = await ReadAsync(Gif("89a", Screen(4, 4), Frame(4, 4), Frame(4, 4)), Counting);
        Assert.Equal(2, info.FrameCount);
    }

    [Fact]
    public async Task A_missing_trailer_after_trailing_extensions_ends_the_walk()
    {
        ImageInfo info = await ReadAsync(Gif("89a", Screen(4, 4), Frame(4, 4), GraphicControl(transparent: false)), Counting);
        Assert.Equal(1, info.FrameCount);
    }

    [Fact]
    public async Task A_file_that_ends_inside_frame_data_sub_blocks_is_truncated()
    {
        byte[] full = Gif("89a", Screen(4, 4), Frame(4, 4), Frame(4, 4), [0x3B]);
        ImageException failure = await FailAsync(full[..^4], Counting);
        Assert.Equal(IdentifyStatus.Truncated, failure.Status);
    }

    [Fact]
    public async Task A_file_that_ends_inside_an_extension_is_truncated()
    {
        byte[] full = Gif("89a", Screen(4, 4), Application("NETSCAPE2.0", [1, 0, 0]), Frame(4, 4));
        ImageException failure = await FailAsync(full[..(6 + 7 + 8)]);
        Assert.Equal(IdentifyStatus.Truncated, failure.Status);
    }

    [Fact]
    public async Task A_file_that_ends_inside_the_screen_descriptor_is_truncated()
    {
        ImageException failure = await FailAsync(Gif("89a", Screen(4, 4))[..10]);
        Assert.Equal(IdentifyStatus.Truncated, failure.Status);
    }

    [Fact]
    public async Task A_file_that_ends_before_any_block_is_truncated()
    {
        ImageException failure = await FailAsync(Gif("89a", Screen(4, 4)));
        Assert.Equal(IdentifyStatus.Truncated, failure.Status);
    }

    [Fact]
    public async Task A_file_that_ends_inside_a_global_colour_table_is_truncated()
    {
        byte[] data = Gif("89a", Screen(4, 4, tableBits: 2));
        ImageException failure = await FailAsync(data[..^5]);
        Assert.Equal(IdentifyStatus.Truncated, failure.Status);
    }

    [Fact]
    public async Task A_bad_block_introducer_is_corrupt()
    {
        ImageException failure = await FailAsync(Gif("89a", Screen(4, 4), [0x99, 0, 0]));

        Assert.Equal(IdentifyStatus.Corrupt, failure.Status);
        Assert.Contains("GIF", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_block_introducer_after_a_frame_is_corrupt_when_counting()
    {
        ImageException failure = await FailAsync(Gif("89a", Screen(4, 4), Frame(4, 4), [0x99, 0, 0]), Counting);
        Assert.Equal(IdentifyStatus.Corrupt, failure.Status);
    }

    [Fact]
    public async Task A_trailer_with_no_image_is_corrupt()
    {
        ImageException failure = await FailAsync(Gif("89a", Screen(4, 4), [0x3B]));
        Assert.Equal(IdentifyStatus.Corrupt, failure.Status);
    }

    [Fact]
    public async Task A_zero_screen_size_uses_the_first_frame()
    {
        byte[] data = Gif("89a", Screen(0, 0), Frame(12, 9), Frame(5, 5), [0x3B]);

        ImageInfo plain = await ReadAsync(data);
        Assert.Equal(12, plain.Width);
        Assert.Equal(9, plain.Height);

        ImageInfo counted = await ReadAsync(data, Counting);
        Assert.Equal(12, counted.Width);
        Assert.Equal(9, counted.Height);
        Assert.Equal(2, counted.FrameCount);
    }

    [Fact]
    public async Task A_zero_screen_width_alone_uses_the_first_frame_for_both()
    {
        ImageInfo info = await ReadAsync(Gif("89a", Screen(0, 7), Frame(12, 9), [0x3B]));

        Assert.Equal(12, info.Width);
        Assert.Equal(9, info.Height);
    }

    [Fact]
    public async Task A_zero_sized_screen_and_frame_gives_zero_for_the_caller_to_reject()
    {
        ImageInfo info = await ReadAsync(Gif("89a", Screen(0, 0), Frame(0, 0), [0x3B]));

        Assert.Equal(0, info.Width);
        Assert.Equal(0, info.Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public async Task Global_and_local_colour_tables_are_skipped(int tableBits)
    {
        byte[] data = Gif(
            "89a",
            Screen(10, 11, tableBits),
            Frame(10, 11, localTableBits: tableBits),
            Frame(10, 11, localTableBits: 0),
            Frame(10, 11, localTableBits: tableBits),
            [0x3B]);

        ImageInfo info = await ReadAsync(data, Counting);

        Assert.Equal(10, info.Width);
        Assert.Equal(3, info.FrameCount);
    }

    [Fact]
    public async Task A_global_colour_table_is_skipped_before_the_first_frame_without_counting()
    {
        ImageInfo info = await ReadAsync(Gif("89a", Screen(10, 11, tableBits: 7), Frame(10, 11), [0x3B]));
        Assert.Equal(11, info.Height);
    }

    [Fact]
    public async Task Frame_data_is_skipped_for_free_over_a_seekable_stream()
    {
        byte[] bigFrame = FrameWithData(4, 4, new byte[100_000]);
        byte[] data = Gif("89a", Screen(4, 4), bigFrame, Frame(4, 4), [0x3B]);

        using (var seekable = new MemoryStream(data))
        {
            ImageInfo info = await GifHeaderReader.ReadAsync(HeaderReader.FromStream(seekable, 4096), Counting, TestContext.Current.CancellationToken);

            // 100 KB of 255-byte sub-blocks: each length byte is read, but the payloads are skipped.
            Assert.Equal(2, info.FrameCount);
        }
    }

    [Fact]
    public async Task A_non_seekable_stream_is_read_when_the_budget_allows()
    {
        byte[] data = Gif("89a", Screen(4, 4), Frame(4, 4), Frame(4, 4));
        using var forwardOnly = new ForwardOnlyStream(data);

        ImageInfo info = await GifHeaderReader.ReadAsync(HeaderReader.FromStream(forwardOnly, Budget), Counting, TestContext.Current.CancellationToken);

        Assert.Equal(2, info.FrameCount);
    }

    [Fact]
    public async Task Counting_frames_on_a_non_seekable_stream_runs_out_of_budget()
    {
        byte[] data = Gif("89a", Screen(4, 4), FrameWithData(4, 4, new byte[100_000]), [0x3B]);
        using var forwardOnly = new ForwardOnlyStream(data);

        ImageException failure = await Assert.ThrowsAnyAsync<ImageException>(async () =>
            await GifHeaderReader.ReadAsync(HeaderReader.FromStream(forwardOnly, 4096), Counting, TestContext.Current.CancellationToken));

        Assert.Equal(IdentifyStatus.ExceedsLimits, failure.Status);
    }

    [Fact]
    public void Reading_from_memory_completes_synchronously()
    {
        byte[] data = Gif("89a", Screen(4, 4), GraphicControl(transparent: true), Frame(4, 4), Frame(4, 4), [0x3B]);

        ValueTask<ImageInfo> plain = GifHeaderReader.ReadAsync(HeaderReader.FromMemory(data, Budget), IdentifyOptions.Default, TestContext.Current.CancellationToken);
        ValueTask<ImageInfo> counted = GifHeaderReader.ReadAsync(HeaderReader.FromMemory(data, Budget), Counting, TestContext.Current.CancellationToken);

        Assert.True(plain.IsCompletedSuccessfully);
        Assert.True(counted.IsCompletedSuccessfully);
    }

    private static async Task<ImageInfo> ReadAsync(byte[] data, IdentifyOptions? options = null) =>
        await GifHeaderReader.ReadAsync(HeaderReader.FromMemory(data, Budget), options ?? IdentifyOptions.Default, TestContext.Current.CancellationToken);

    private static async Task<ImageException> FailAsync(byte[] data, IdentifyOptions? options = null) =>
        await Assert.ThrowsAnyAsync<ImageException>(async () => await ReadAsync(data, options));

    private static byte[] Gif(string version, params byte[][] parts)
    {
        var bytes = new List<byte>(System.Text.Encoding.ASCII.GetBytes("GIF" + version));
        foreach (byte[] part in parts)
        {
            bytes.AddRange(part);
        }

        return [.. bytes];
    }

    // The logical screen descriptor, plus a global colour table of 2^(tableBits + 1) entries when tableBits > 0.
    private static byte[] Screen(int width, int height, int tableBits = 0)
    {
        var bytes = new List<byte> { (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8) };
        if (tableBits > 0)
        {
            bytes.Add((byte)(0x80 | (tableBits - 1)));
            bytes.AddRange([0, 0]);
            bytes.AddRange(new byte[3 * (1 << tableBits)]);
        }
        else
        {
            bytes.Add(0);
            bytes.AddRange([0, 0]);
        }

        return [.. bytes];
    }

    private static byte[] GraphicControl(bool transparent) =>
        [0x21, 0xF9, 4, (byte)(transparent ? 1 : 0), 10, 0, 0, 0];

    private static byte[] Application(string name, byte[] data)
    {
        var bytes = new List<byte> { 0x21, 0xFF, (byte)name.Length };
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(name));
        bytes.Add((byte)data.Length);
        bytes.AddRange(data);
        bytes.Add(0);
        return [.. bytes];
    }

    private static byte[] Frame(int width, int height, int localTableBits = 0) =>
        FrameWithData(width, height, new byte[3], localTableBits);

    // An image descriptor, optional local colour table, and the data cut into sub-blocks of at most 255 bytes.
    private static byte[] FrameWithData(int width, int height, byte[] data, int localTableBits = 0)
    {
        var bytes = new List<byte> { 0x2C, 0, 0, 0, 0, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8) };
        bytes.Add((byte)(localTableBits > 0 ? 0x80 | (localTableBits - 1) : 0));
        if (localTableBits > 0)
        {
            bytes.AddRange(new byte[3 * (1 << localTableBits)]);
        }

        bytes.Add(2);
        for (int offset = 0; offset < data.Length; offset += 255)
        {
            int length = Math.Min(255, data.Length - offset);
            bytes.Add((byte)length);
            bytes.AddRange(data.AsSpan(offset, length).ToArray());
        }

        bytes.Add(0);
        return [.. bytes];
    }

    // A stream that can't seek, so skipping it costs reads.
    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
