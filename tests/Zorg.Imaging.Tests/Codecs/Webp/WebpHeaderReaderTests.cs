using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Codecs.Webp;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Tests.Codecs.Webp;

public class WebpHeaderReaderTests
{
    private const int Budget = 1024 * 1024;
    private const byte Icc = 0x20;
    private const byte Alpha = 0x10;
    private const byte ExifFlag = 0x08;
    private const byte Animation = 0x02;

    private static readonly IdentifyOptions Counting = new() { CountFrames = true };

    [Fact]
    public async Task Lossy_key_frame_gives_the_size()
    {
        ImageInfo info = await Read(Riff(Chunk("VP8 ", Vp8(320, 200))));

        Assert.Equal(ImageFormat.Webp, info.Format);
        Assert.Equal(320, info.Width);
        Assert.Equal(200, info.Height);
        Assert.False(info.HasAlpha);
        Assert.False(info.IsAnimated);
        Assert.Equal(1, info.FrameCount);
        Assert.Equal(1, info.Orientation);
    }

    [Fact]
    public async Task Lossy_size_ignores_the_scale_bits()
    {
        byte[] payload = Vp8(100, 50);
        payload[7] |= 0xC0;
        payload[9] |= 0x40;

        ImageInfo info = await Read(Riff(Chunk("VP8 ", payload)));

        Assert.Equal(100, info.Width);
        Assert.Equal(50, info.Height);
    }

    [Fact]
    public async Task Lossy_frame_that_is_not_a_key_frame_is_corrupt()
    {
        byte[] payload = Vp8(10, 10);
        payload[0] |= 1;
        await AssertStatus(IdentifyStatus.Corrupt, Riff(Chunk("VP8 ", payload)));
    }

    [Fact]
    public async Task Lossy_frame_with_a_bad_start_code_is_corrupt()
    {
        byte[] payload = Vp8(10, 10);
        payload[4] = 0;
        await AssertStatus(IdentifyStatus.Corrupt, Riff(Chunk("VP8 ", payload)));
    }

    [Fact]
    public async Task Lossy_chunk_under_10_bytes_is_corrupt() =>
        await AssertStatus(IdentifyStatus.Corrupt, Riff(Chunk("VP8 ", Vp8(10, 10)[..9])));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lossless_gives_the_size_and_alpha_bit(bool alpha)
    {
        ImageInfo info = await Read(Riff(Chunk("VP8L", Vp8L(16384, 1, alpha, 0))));

        Assert.Equal(16384, info.Width);
        Assert.Equal(1, info.Height);
        Assert.Equal(alpha, info.HasAlpha);
        Assert.Equal(1, info.FrameCount);
    }

    [Fact]
    public async Task Lossless_with_a_version_other_than_0_is_corrupt() =>
        await AssertStatus(IdentifyStatus.Corrupt, Riff(Chunk("VP8L", Vp8L(10, 10, alpha: false, 1))));

    [Fact]
    public async Task Lossless_with_a_bad_signature_is_corrupt()
    {
        byte[] payload = Vp8L(10, 10, alpha: false, 0);
        payload[0] = 0x2E;
        await AssertStatus(IdentifyStatus.Corrupt, Riff(Chunk("VP8L", payload)));
    }

    [Fact]
    public async Task Lossless_chunk_under_5_bytes_is_corrupt() =>
        await AssertStatus(IdentifyStatus.Corrupt, Riff(Chunk("VP8L", [0x2F, 0, 0, 0])));

    [Fact]
    public async Task Extended_still_image_gives_canvas_size_and_flags()
    {
        ImageInfo info = await Read(Riff(Chunk("VP8X", Vp8X(Alpha | Icc, 1000, 2000)), Chunk("VP8 ", Vp8(1000, 2000))));

        Assert.Equal(1000, info.Width);
        Assert.Equal(2000, info.Height);
        Assert.True(info.HasAlpha);
        Assert.True(info.HasIccProfile);
        Assert.False(info.HasExif);
        Assert.False(info.IsAnimated);
        Assert.Equal(1, info.FrameCount);
    }

    [Fact]
    public async Task Extended_canvas_can_use_24_bits()
    {
        ImageInfo info = await Read(Riff(Chunk("VP8X", Vp8X(0, 1 << 24, 1))));

        Assert.Equal(1 << 24, info.Width);
        Assert.Equal(1, info.Height);
    }

    [Fact]
    public async Task Extended_without_flags_has_none()
    {
        ImageInfo info = await Read(Riff(Chunk("VP8X", Vp8X(0, 5, 6)), Chunk("VP8L", Vp8L(5, 6, false, 0))));

        Assert.False(info.HasAlpha);
        Assert.False(info.HasIccProfile);
        Assert.False(info.HasExif);
    }

    [Fact]
    public async Task Extended_chunk_under_10_bytes_is_corrupt() =>
        await AssertStatus(IdentifyStatus.Corrupt, Riff(Chunk("VP8X", Vp8X(0, 5, 6)[..9])));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Exif_orientation_after_the_image_chunk_is_read(bool withPrefix)
    {
        byte[] exif = Exif(8);
        if (withPrefix)
        {
            exif = [.. "Exif\0\0"u8, .. exif];
        }

        ImageInfo info = await Read(Riff(
            Chunk("VP8X", Vp8X(ExifFlag, 30, 40)),
            Chunk("VP8 ", Vp8(30, 40)),
            Chunk("EXIF", exif)));

        Assert.True(info.HasExif);
        Assert.Equal(8, info.Orientation);
        Assert.Equal(40, info.DisplayWidth);
    }

    [Fact]
    public async Task Exif_flag_without_an_exif_chunk_has_orientation_1()
    {
        ImageInfo info = await Read(Riff(Chunk("VP8X", Vp8X(ExifFlag, 30, 40)), Chunk("VP8 ", Vp8(30, 40))));

        Assert.True(info.HasExif);
        Assert.Equal(1, info.Orientation);
    }

    [Fact]
    public async Task Exif_chunk_without_the_flag_is_ignored()
    {
        ImageInfo info = await Read(Riff(Chunk("VP8X", Vp8X(0, 30, 40)), Chunk("VP8 ", Vp8(30, 40)), Chunk("EXIF", Exif(6))));

        Assert.False(info.HasExif);
        Assert.Equal(1, info.Orientation);
    }

    [Fact]
    public async Task Animated_without_counting_has_no_frame_count()
    {
        ImageInfo info = await Read(Riff(Chunk("VP8X", Vp8X(Animation | Alpha, 50, 60)), Chunk("ANIM", new byte[6]), Anmf(), Anmf(), Anmf()));

        Assert.True(info.IsAnimated);
        Assert.Null(info.FrameCount);
        Assert.Equal(50, info.Width);
    }

    [Fact]
    public async Task Animated_with_counting_counts_the_ANMF_chunks()
    {
        ImageInfo info = await Read(
            Riff(Chunk("VP8X", Vp8X(Animation, 50, 60)), Chunk("ANIM", new byte[6]), Anmf(), Anmf(), Anmf(), Chunk("EXIF", Exif(3))),
            Counting);

        Assert.True(info.IsAnimated);
        Assert.Equal(3, info.FrameCount);
    }

    [Fact]
    public async Task Animated_with_counting_and_exif_finds_both()
    {
        ImageInfo info = await Read(
            Riff(Chunk("VP8X", Vp8X(Animation | ExifFlag, 50, 60)), Chunk("ANIM", new byte[6]), Anmf(), Anmf(), Anmf(), Chunk("EXIF", Exif(3))),
            Counting);

        Assert.Equal(3, info.FrameCount);
        Assert.Equal(3, info.Orientation);
    }

    [Fact]
    public async Task Animated_with_exif_before_the_frames_and_no_counting_stops_early()
    {
        ImageInfo info = await Read(Riff(Chunk("VP8X", Vp8X(Animation | ExifFlag, 50, 60)), Chunk("EXIF", Exif(5)), Anmf()));

        Assert.Equal(5, info.Orientation);
        Assert.Null(info.FrameCount);
    }

    [Fact]
    public async Task Counting_stops_with_ExceedsLimits_when_there_are_too_many_frames()
    {
        byte[] bytes = Riff(Chunk("VP8X", Vp8X(Animation, 50, 60)), Anmf(), Anmf(), Anmf());
        var options = new IdentifyOptions { CountFrames = true, Limits = new ImageLimits { MaxFrames = 2 } };

        IdentifyFailure failure = await Assert.ThrowsAsync<IdentifyFailure>(() => Read(bytes, options));

        Assert.Equal(IdentifyStatus.ExceedsLimits, failure.Status);
    }

    [Fact]
    public async Task Frames_at_the_limit_are_allowed()
    {
        byte[] bytes = Riff(Chunk("VP8X", Vp8X(Animation, 50, 60)), Anmf(), Anmf());
        var options = new IdentifyOptions { CountFrames = true, Limits = new ImageLimits { MaxFrames = 2 } };

        ImageInfo info = await Read(bytes, options);

        Assert.Equal(2, info.FrameCount);
    }

    [Fact]
    public async Task Odd_sized_chunks_are_padded()
    {
        ImageInfo info = await Read(
            Riff(
                Chunk("VP8X", Vp8X(Animation | ExifFlag, 50, 60)),
                Chunk("ICCP", new byte[3]),
                Chunk("ANIM", new byte[7]),
                Chunk("ANMF", new byte[17]),
                Chunk("JUNK", new byte[1]),
                Chunk("ANMF", new byte[19]),
                Chunk("EXIF", Exif(6))),
            Counting);

        Assert.Equal(2, info.FrameCount);
        Assert.Equal(6, info.Orientation);
    }

    [Fact]
    public async Task A_last_chunk_without_its_padding_byte_is_accepted()
    {
        byte[] bytes = Riff(Chunk("VP8X", Vp8X(Animation, 50, 60)), Anmf(), Chunk("ANMF", new byte[3]));
        bytes = bytes[..^1];
        FixRiffSize(bytes);

        ImageInfo info = await Read(bytes, Counting);

        Assert.Equal(2, info.FrameCount);
    }

    [Fact]
    public async Task A_chunk_extending_past_the_riff_end_is_corrupt()
    {
        byte[] bytes = Riff(Chunk("VP8X", Vp8X(ExifFlag, 50, 60)), Chunk("VP8 ", Vp8(50, 60)), Chunk("EXIF", Exif(6)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - Exif(6).Length - 4), 5000);

        await AssertStatus(IdentifyStatus.Corrupt, bytes);
    }

    [Fact]
    public async Task The_first_chunk_extending_past_the_riff_end_is_corrupt()
    {
        byte[] bytes = Riff(Chunk("VP8 ", Vp8(50, 60)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 5000);

        await AssertStatus(IdentifyStatus.Corrupt, bytes);
    }

    [Fact]
    public async Task An_unknown_first_chunk_is_corrupt() =>
        await AssertStatus(IdentifyStatus.Corrupt, Riff(Chunk("ABCD", new byte[20])));

    [Fact]
    public async Task A_riff_size_under_4_is_corrupt()
    {
        byte[] bytes = Riff();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 3);

        await AssertStatus(IdentifyStatus.Corrupt, bytes);
    }

    [Fact]
    public async Task A_riff_with_no_chunk_is_corrupt() => await AssertStatus(IdentifyStatus.Corrupt, Riff());

    [Fact]
    public async Task Data_ending_inside_the_image_chunk_is_truncated()
    {
        byte[] bytes = Riff(Chunk("VP8 ", Vp8(50, 60)));
        await AssertStatus(IdentifyStatus.Truncated, bytes[..24]);
    }

    [Fact]
    public async Task Data_ending_before_the_riff_end_while_looking_for_exif_is_truncated()
    {
        byte[] bytes = Riff(Chunk("VP8X", Vp8X(ExifFlag, 50, 60)), Chunk("VP8 ", Vp8(50, 60)), Chunk("EXIF", Exif(6)));

        await AssertStatus(IdentifyStatus.Truncated, bytes[..^10]);
    }

    [Fact]
    public async Task Data_ending_inside_the_riff_header_is_truncated()
    {
        byte[] bytes = Riff(Chunk("VP8 ", Vp8(50, 60)));
        for (int length = 0; length < 12; length++)
        {
            await AssertStatus(IdentifyStatus.Truncated, bytes[..length]);
        }
    }

    [Fact]
    public async Task Over_memory_the_task_is_already_completed()
    {
        byte[] bytes = Riff(Chunk("VP8X", Vp8X(Animation | ExifFlag, 50, 60)), Anmf(), Chunk("EXIF", Exif(3)));
        HeaderReader reader = HeaderReader.FromMemory(bytes, Budget);

        ValueTask<ImageInfo> task = WebpHeaderReader.ReadAsync(reader, Counting, TestContext.Current.CancellationToken);

        Assert.True(task.IsCompleted);
        Assert.Equal(1, (await task).FrameCount);
    }

    [Fact]
    public async Task A_seekable_stream_walks_chunks_for_almost_nothing()
    {
        byte[] bytes = Riff(
            Chunk("VP8X", Vp8X(ExifFlag, 50, 60)),
            Chunk("VP8 ", [.. Vp8(50, 60), .. new byte[50_000]]),
            Chunk("EXIF", Exif(6)));
        HeaderReader reader = HeaderReader.FromStream(new System.IO.MemoryStream(bytes), Budget);

        ImageInfo info = await WebpHeaderReader.ReadAsync(reader, IdentifyOptions.Default, TestContext.Current.CancellationToken);

        Assert.Equal(6, info.Orientation);
        Assert.InRange(reader.BytesCharged, 1, 300);
    }

    private static Task<ImageInfo> Read(byte[] bytes, IdentifyOptions? options = null) => ReadCore(bytes, options ?? IdentifyOptions.Default);

    private static async Task<ImageInfo> ReadCore(byte[] bytes, IdentifyOptions options)
    {
        HeaderReader reader = HeaderReader.FromMemory(bytes, Budget);
        return await WebpHeaderReader.ReadAsync(reader, options, TestContext.Current.CancellationToken);
    }

    private static async Task AssertStatus(IdentifyStatus status, byte[] bytes)
    {
        IdentifyFailure failure = await Assert.ThrowsAsync<IdentifyFailure>(() => Read(bytes));
        Assert.Equal(status, failure.Status);
    }

    private static byte[] Riff(params byte[][] chunks)
    {
        List<byte> bytes = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8];
        foreach (byte[] chunk in chunks)
        {
            bytes.AddRange(chunk);
        }

        byte[] result = [.. bytes];
        FixRiffSize(result);
        return result;
    }

    private static void FixRiffSize(byte[] riff) => BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(4), (uint)(riff.Length - 8));

    private static byte[] Chunk(string fourCc, byte[] data)
    {
        byte[] chunk = new byte[8 + data.Length + (data.Length & 1)];
        for (int i = 0; i < 4; i++)
        {
            chunk[i] = (byte)fourCc[i];
        }

        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)data.Length);
        data.CopyTo(chunk, 8);
        return chunk;
    }

    private static byte[] Anmf() => Chunk("ANMF", new byte[16]);

    private static byte[] Vp8(int width, int height)
    {
        byte[] data = new byte[10];
        data[0] = 0x10;
        data[3] = 0x9D;
        data[4] = 0x01;
        data[5] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), (ushort)height);
        return data;
    }

    private static byte[] Vp8L(int width, int height, bool alpha, int version)
    {
        uint bits = (uint)(width - 1) | ((uint)(height - 1) << 14) | (alpha ? 1u << 28 : 0) | ((uint)version << 29);
        byte[] data = new byte[5];
        data[0] = 0x2F;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(1), bits);
        return data;
    }

    private static byte[] Vp8X(byte flags, int width, int height)
    {
        byte[] data = new byte[10];
        data[0] = flags;
        WriteUInt24(data.AsSpan(4), width - 1);
        WriteUInt24(data.AsSpan(7), height - 1);
        return data;
    }

    private static void WriteUInt24(Span<byte> target, int value)
    {
        target[0] = (byte)value;
        target[1] = (byte)(value >> 8);
        target[2] = (byte)(value >> 16);
    }

    private static byte[] Exif(ushort orientation)
    {
        byte[] tiff = new byte[8 + 2 + 12 + 4];
        tiff[0] = tiff[1] = (byte)'I';
        tiff[2] = 42;
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), 0x0112);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(18), orientation);
        return tiff;
    }
}
