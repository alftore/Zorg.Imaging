using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Codecs.Jpeg;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Tests.Codecs.Jpeg;

public class JpegHeaderReaderTests
{
    private const int Budget = 1024 * 1024;

    [Theory]
    [InlineData(0xC0)]
    [InlineData(0xC1)]
    [InlineData(0xC2)]
    [InlineData(0xC3)]
    [InlineData(0xC5)]
    [InlineData(0xC9)]
    [InlineData(0xCF)]
    public async Task Frame_header_gives_the_size(byte sof)
    {
        ImageInfo info = await ReadMemory(Jpeg(Sof(sof, 640, 480)));

        Assert.Equal(ImageFormat.Jpeg, info.Format);
        Assert.Equal(640, info.Width);
        Assert.Equal(480, info.Height);
        Assert.Equal(1, info.Orientation);
        Assert.Equal(1, info.FrameCount);
        Assert.False(info.HasAlpha);
        Assert.False(info.IsAnimated);
        Assert.False(info.HasExif);
        Assert.False(info.HasIccProfile);
    }

    [Fact]
    public async Task Maximum_size_is_read()
    {
        ImageInfo info = await ReadMemory(Jpeg(Sof(0xC2, 65535, 65535)));
        Assert.Equal(65535, info.Width);
        Assert.Equal(65535, info.Height);
    }

    [Fact]
    public async Task Exif_orientation_is_read_in_little_endian_data()
    {
        ImageInfo info = await ReadMemory(Jpeg(Segment(0xE1, Exif(littleEndian: true, 6)), Sof(0xC0, 10, 20)));

        Assert.Equal(6, info.Orientation);
        Assert.True(info.HasExif);
        Assert.Equal(20, info.DisplayWidth);
    }

    [Fact]
    public async Task Exif_orientation_is_read_in_big_endian_data()
    {
        ImageInfo info = await ReadMemory(Jpeg(Segment(0xE1, Exif(littleEndian: false, 6)), Sof(0xC0, 10, 20)));

        Assert.Equal(6, info.Orientation);
        Assert.True(info.HasExif);
    }

    [Fact]
    public async Task Only_the_first_exif_segment_gives_the_orientation()
    {
        ImageInfo info = await ReadMemory(Jpeg(
            Segment(0xE1, Exif(littleEndian: true, 3)),
            Segment(0xE1, Exif(littleEndian: true, 6)),
            Sof(0xC0, 10, 20)));

        Assert.Equal(3, info.Orientation);
    }

    [Fact]
    public async Task Exif_without_an_orientation_has_orientation_1()
    {
        ImageInfo info = await ReadMemory(Jpeg(Segment(0xE1, "Exif\0\0"u8.ToArray()), Sof(0xC0, 10, 20)));

        Assert.True(info.HasExif);
        Assert.Equal(1, info.Orientation);
    }

    [Fact]
    public async Task Icc_profile_in_APP2_is_noticed()
    {
        byte[] icc = [.. "ICC_PROFILE\0"u8, 1, 1, 0, 0, 0, 0];
        ImageInfo info = await ReadMemory(Jpeg(Segment(0xE2, icc), Sof(0xC0, 10, 20)));

        Assert.True(info.HasIccProfile);
        Assert.False(info.HasExif);
    }

    [Fact]
    public async Task Other_APP_segments_are_not_taken_for_exif_or_icc()
    {
        ImageInfo info = await ReadMemory(Jpeg(
            Segment(0xE0, "JFIF\0\x01\x01\0\0\x01\0\x01\0\0"u8.ToArray()),
            Segment(0xE2, "MPF\0"u8.ToArray()),
            Segment(0xE1, "http://ns.adobe.com/xap/1.0/\0"u8.ToArray()),
            Segment(0xEE, "Adobe"u8.ToArray()),
            Sof(0xC0, 10, 20)));

        Assert.False(info.HasExif);
        Assert.False(info.HasIccProfile);
    }

    [Fact]
    public async Task Short_APP_segments_are_skipped()
    {
        ImageInfo info = await ReadMemory(Jpeg(Segment(0xE1, [1, 2]), Segment(0xE2, []), Sof(0xC0, 10, 20)));
        Assert.Equal(10, info.Width);
    }

    [Fact]
    public async Task Fill_bytes_before_a_marker_are_skipped()
    {
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0xFF, 0xFF, 0xFE, 0x00, 0x04, 0x41, 0x42, .. Sof(0xC0, 7, 9)];
        ImageInfo info = await ReadMemory(bytes);

        Assert.Equal(7, info.Width);
        Assert.Equal(9, info.Height);
    }

    [Fact]
    public async Task Markers_without_a_length_are_skipped()
    {
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0x01, 0xFF, 0xD0, 0xFF, 0xD7, .. Sof(0xC0, 7, 9)];
        ImageInfo info = await ReadMemory(bytes);

        Assert.Equal(7, info.Width);
    }

    [Fact]
    public async Task Tables_before_the_frame_are_skipped()
    {
        ImageInfo info = await ReadMemory(Jpeg(
            Segment(0xDB, new byte[65]),
            Segment(0xC4, new byte[20]),
            Segment(0xDD, [0, 10]),
            Sof(0xC0, 11, 12)));

        Assert.Equal(11, info.Width);
    }

    [Fact]
    public async Task Reading_stops_after_the_frame_header()
    {
        // Nothing valid follows the frame header, and it must not matter.
        byte[] bytes = [.. Jpeg(Sof(0xC0, 5, 6)), 0x12, 0x34];
        ImageInfo info = await ReadMemory(bytes);
        Assert.Equal(5, info.Width);
    }

    [Theory]
    [InlineData(0xD9)]
    [InlineData(0xDA)]
    public async Task A_missing_frame_header_is_corrupt(byte marker)
    {
        byte[] bytes = [.. Jpeg(), 0xFF, marker, 0x00, 0x08, 1, 2, 3, 4, 5, 6];
        ImageException failure = await Assert.ThrowsAnyAsync<ImageException>(() => ReadMemory(bytes));

        Assert.Equal(IdentifyStatus.Corrupt, failure.Status);
        Assert.Contains("JPEG", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_start_of_image_before_the_frame_is_corrupt() =>
        await AssertStatus(IdentifyStatus.Corrupt, [.. Jpeg(), 0xFF, 0xD8, .. Sof(0xC0, 1, 1)]);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task A_segment_length_under_2_is_corrupt(byte length) =>
        await AssertStatus(IdentifyStatus.Corrupt, [.. Jpeg(), 0xFF, 0xE0, 0x00, length, 0, 0, 0, 0]);

    [Fact]
    public async Task Data_where_a_marker_should_be_is_corrupt() =>
        await AssertStatus(IdentifyStatus.Corrupt, [.. Jpeg(), 0x12, 0x34, 0x56]);

    [Fact]
    public async Task A_frame_header_that_is_too_short_is_corrupt() =>
        await AssertStatus(IdentifyStatus.Corrupt, Jpeg(Segment(0xC0, [8, 0, 1, 0])));

    [Fact]
    public async Task A_height_of_zero_is_corrupt_and_says_it_is_not_supported()
    {
        ImageException failure = await Assert.ThrowsAnyAsync<ImageException>(() => ReadMemory(Jpeg(Sof(0xC0, 10, 0))));

        Assert.Equal(IdentifyStatus.Corrupt, failure.Status);
        Assert.Contains("isn't supported", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_width_of_zero_is_corrupt() =>
        await AssertStatus(IdentifyStatus.Corrupt, Jpeg(Sof(0xC0, 0, 10)));

    [Fact]
    public async Task Data_ending_inside_a_segment_is_truncated()
    {
        byte[] whole = Jpeg(Segment(0xE0, new byte[40]), Sof(0xC0, 10, 20));
        await AssertStatus(IdentifyStatus.Truncated, whole.AsMemory(0, 30).ToArray());
    }

    [Fact]
    public async Task Data_ending_inside_the_frame_header_is_truncated()
    {
        byte[] whole = Jpeg(Sof(0xC0, 10, 20));
        for (int length = 2; length < 12; length++)
        {
            await AssertStatus(IdentifyStatus.Truncated, whole.AsMemory(0, length).ToArray());
        }
    }

    [Fact]
    public async Task A_file_with_only_the_signature_is_truncated() =>
        await AssertStatus(IdentifyStatus.Truncated, [0xFF, 0xD8]);

    [Fact]
    public async Task A_segment_longer_than_the_data_is_truncated() =>
        await AssertStatus(IdentifyStatus.Truncated, [.. Jpeg(), 0xFF, 0xE0, 0xFF, 0xFF, 1, 2, 3]);

    [Fact]
    public async Task Over_memory_the_task_is_already_completed()
    {
        HeaderReader reader = HeaderReader.FromMemory(Jpeg(Segment(0xE1, Exif(true, 3)), Sof(0xC0, 10, 20)), Budget);
        ValueTask<ImageInfo> task = JpegHeaderReader.ReadAsync(reader, IdentifyOptions.Default, TestContext.Current.CancellationToken);

        Assert.True(task.IsCompleted);
        Assert.Equal(10, (await task).Width);
    }

    [Fact]
    public async Task A_large_APP_segment_that_is_not_exif_costs_only_its_identifier_on_a_seekable_stream()
    {
        byte[] bytes = Jpeg(Segment(0xE1, XmpSegmentData()), Sof(0xC0, 30, 40));
        HeaderReader reader = HeaderReader.FromStream(new MemoryStream(bytes), Budget);

        ImageInfo info = await JpegHeaderReader.ReadAsync(reader, IdentifyOptions.Default, TestContext.Current.CancellationToken);

        Assert.Equal(30, info.Width);
        Assert.False(info.HasExif);
        Assert.InRange(reader.BytesCharged, 1, 300);
    }

    [Fact]
    public async Task A_large_APP_segment_over_a_stream_that_cannot_seek_exceeds_a_small_budget()
    {
        byte[] bytes = Jpeg(Segment(0xE1, XmpSegmentData()), Sof(0xC0, 30, 40));
        HeaderReader reader = HeaderReader.FromStream(new NonSeekableStream(bytes), 4096);

        ImageException failure = await Assert.ThrowsAnyAsync<ImageException>(
            () => JpegHeaderReader.ReadAsync(reader, IdentifyOptions.Default, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(IdentifyStatus.ExceedsLimits, failure.Status);
    }

    [Fact]
    public async Task A_non_seekable_stream_with_enough_budget_reads_exif_orientation()
    {
        byte[] bytes = Jpeg(Segment(0xE1, Exif(false, 8)), Sof(0xC0, 30, 40));
        HeaderReader reader = HeaderReader.FromStream(new NonSeekableStream(bytes), Budget);

        ImageInfo info = await JpegHeaderReader.ReadAsync(reader, IdentifyOptions.Default, TestContext.Current.CancellationToken);

        Assert.Equal(8, info.Orientation);
        Assert.Equal(30, info.Width);
    }

    private static byte[] XmpSegmentData()
    {
        byte[] data = new byte[60_000];
        "http://ns.adobe.com/xap/1.0/\0"u8.CopyTo(data);
        return data;
    }

    private static async Task<ImageInfo> ReadMemory(byte[] bytes)
    {
        HeaderReader reader = HeaderReader.FromMemory(bytes, Budget);
        return await JpegHeaderReader.ReadAsync(reader, IdentifyOptions.Default, TestContext.Current.CancellationToken);
    }

    private static async Task AssertStatus(IdentifyStatus status, byte[] bytes)
    {
        ImageException failure = await Assert.ThrowsAnyAsync<ImageException>(() => ReadMemory(bytes));
        Assert.Equal(status, failure.Status);
    }

    private static byte[] Jpeg(params byte[][] segments)
    {
        List<byte> bytes = [0xFF, 0xD8];
        foreach (byte[] segment in segments)
        {
            bytes.AddRange(segment);
        }

        return [.. bytes];
    }

    private static byte[] Segment(byte marker, byte[] data)
    {
        byte[] segment = new byte[4 + data.Length];
        segment[0] = 0xFF;
        segment[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(data.Length + 2));
        data.CopyTo(segment, 4);
        return segment;
    }

    private static byte[] Sof(byte marker, int width, int height)
    {
        byte[] data = new byte[15];
        data[0] = 8;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(1), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(3), (ushort)width);
        data[5] = 3;
        return Segment(marker, data);
    }

    private static byte[] Exif(bool littleEndian, ushort orientation)
    {
        byte[] block = new byte[6 + 8 + 2 + 12 + 4];
        "Exif\0\0"u8.CopyTo(block);
        Span<byte> tiff = block.AsSpan(6);
        tiff[0] = tiff[1] = littleEndian ? (byte)'I' : (byte)'M';
        if (littleEndian)
        {
            tiff[2] = 42;
            BinaryPrimitives.WriteUInt32LittleEndian(tiff[4..], 8);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff[8..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff[10..], 0x0112);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff[12..], 3);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff[14..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff[18..], orientation);
        }
        else
        {
            tiff[3] = 42;
            BinaryPrimitives.WriteUInt32BigEndian(tiff[4..], 8);
            BinaryPrimitives.WriteUInt16BigEndian(tiff[8..], 1);
            BinaryPrimitives.WriteUInt16BigEndian(tiff[10..], 0x0112);
            BinaryPrimitives.WriteUInt16BigEndian(tiff[12..], 3);
            BinaryPrimitives.WriteUInt32BigEndian(tiff[14..], 1);
            BinaryPrimitives.WriteUInt16BigEndian(tiff[18..], orientation);
        }

        return block;
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
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

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

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
