using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Codecs.Png;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Tests.Codecs.Png;

public class PngHeaderReaderTests
{
    private const int Budget = 1024 * 1024;

    public static TheoryData<int, int> ValidCombinations()
    {
        var data = new TheoryData<int, int>();
        foreach (int depth in new[] { 1, 2, 4, 8, 16 })
        {
            data.Add(0, depth);
        }

        data.Add(2, 8);
        data.Add(2, 16);
        foreach (int depth in new[] { 1, 2, 4, 8 })
        {
            data.Add(3, depth);
        }

        data.Add(4, 8);
        data.Add(4, 16);
        data.Add(6, 8);
        data.Add(6, 16);
        return data;
    }

    [Theory]
    [MemberData(nameof(ValidCombinations))]
    public async Task Valid_colour_type_and_bit_depth_combinations_are_read(int colourType, int bitDepth)
    {
        ImageInfo info = await ReadMemoryAsync(Png(Ihdr(30, 20, bitDepth, colourType), Idat()));

        Assert.Equal(ImageFormat.Png, info.Format);
        Assert.Equal(30, info.Width);
        Assert.Equal(20, info.Height);
        Assert.Equal(colourType is 4 or 6, info.HasAlpha);
        Assert.Equal(1, info.FrameCount);
        Assert.False(info.IsAnimated);
        Assert.False(info.HasExif);
        Assert.False(info.HasIccProfile);
        Assert.Equal(1, info.Orientation);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(0, 32)]
    [InlineData(2, 4)]
    [InlineData(2, 1)]
    [InlineData(3, 16)]
    [InlineData(4, 4)]
    [InlineData(6, 2)]
    [InlineData(1, 8)]
    [InlineData(5, 8)]
    [InlineData(255, 8)]
    public async Task Invalid_colour_type_and_bit_depth_combinations_are_corrupt(int colourType, int bitDepth)
    {
        ImageException failure = await FailMemoryAsync(Png(Ihdr(1, 1, bitDepth, colourType), Idat()));
        Assert.Equal(IdentifyStatus.Corrupt, failure.Status);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 2)]
    public async Task Invalid_compression_filter_or_interlace_is_corrupt(int compression, int filter, int interlace)
    {
        ImageException failure = await FailMemoryAsync(Png(Ihdr(1, 1, 8, 2, compression, filter, interlace), Idat()));
        Assert.Equal(IdentifyStatus.Corrupt, failure.Status);
    }

    [Fact]
    public async Task Adam7_interlace_is_accepted()
    {
        ImageInfo info = await ReadMemoryAsync(Png(Ihdr(5, 5, 8, 2, interlace: 1), Idat()));
        Assert.Equal(5, info.Width);
    }

    [Theory]
    [InlineData(0u, 5u)]
    [InlineData(5u, 0u)]
    [InlineData(0x80000000u, 5u)]
    [InlineData(5u, uint.MaxValue)]
    public async Task Zero_or_oversized_dimensions_are_corrupt(uint width, uint height)
    {
        ImageException failure = await FailMemoryAsync(Png(Ihdr(width, height, 8, 2), Idat()));
        Assert.Equal(IdentifyStatus.Corrupt, failure.Status);
    }

    [Fact]
    public async Task The_largest_allowed_dimension_is_read()
    {
        ImageInfo info = await ReadMemoryAsync(Png(Ihdr(int.MaxValue, int.MaxValue, 8, 2), Idat()));
        Assert.Equal(int.MaxValue, info.Width);
        Assert.Equal(int.MaxValue, info.Height);
    }

    [Fact]
    public async Task A_bad_IHDR_checksum_is_corrupt()
    {
        byte[] ihdr = Ihdr(10, 10, 8, 2);
        ihdr[^1] ^= 0xFF;

        ImageException failure = await FailMemoryAsync(Png(ihdr, Idat()));

        Assert.Equal(IdentifyStatus.Corrupt, failure.Status);
        Assert.Contains("PNG", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_first_chunk_that_is_not_IHDR_is_corrupt()
    {
        ImageException failure = await FailMemoryAsync(Png(Chunk("tEXt", new byte[4]), Ihdr(10, 10, 8, 2), Idat()));

        Assert.Equal(IdentifyStatus.Corrupt, failure.Status);
        Assert.Equal("The PNG file's first chunk isn't IHDR.", failure.Message);
    }

    [Fact]
    public async Task An_IHDR_of_the_wrong_length_is_corrupt()
    {
        ImageException failure = await FailMemoryAsync(Png(Chunk("IHDR", new byte[12]), Idat()));
        Assert.Equal(IdentifyStatus.Corrupt, failure.Status);
    }

    [Fact]
    public async Task An_Apple_CgBI_file_is_corrupt_with_a_message_saying_so()
    {
        ImageException failure = await FailMemoryAsync(Png(Chunk("CgBI", new byte[4]), Ihdr(10, 10, 8, 6), Idat()));

        Assert.Equal(IdentifyStatus.Corrupt, failure.Status);
        Assert.Contains("CgBI", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tRNS_chunk_means_alpha()
    {
        ImageInfo info = await ReadMemoryAsync(Png(Ihdr(10, 10, 8, 3), Chunk("PLTE", new byte[3]), Chunk("tRNS", new byte[1]), Idat()));
        Assert.True(info.HasAlpha);
    }

    [Fact]
    public async Task An_iCCP_chunk_means_an_ICC_profile()
    {
        ImageInfo info = await ReadMemoryAsync(Png(Ihdr(10, 10, 8, 2), Chunk("iCCP", new byte[50]), Idat()));
        Assert.True(info.HasIccProfile);
    }

    [Fact]
    public async Task An_eXIf_chunk_sets_the_orientation()
    {
        ImageInfo info = await ReadMemoryAsync(Png(Ihdr(40, 10, 8, 2), Chunk("eXIf", ExifWithOrientation(6)), Idat()));

        Assert.True(info.HasExif);
        Assert.Equal(6, info.Orientation);
        Assert.Equal(10, info.DisplayWidth);
        Assert.Equal(40, info.DisplayHeight);
    }

    [Fact]
    public async Task An_eXIf_chunk_without_a_valid_orientation_reads_as_1()
    {
        ImageInfo info = await ReadMemoryAsync(Png(Ihdr(40, 10, 8, 2), Chunk("eXIf", new byte[20]), Idat()));

        Assert.True(info.HasExif);
        Assert.Equal(1, info.Orientation);
    }

    [Fact]
    public async Task A_large_eXIf_chunk_is_only_partly_read()
    {
        byte[] exif = new byte[500_000];
        ExifWithOrientation(8).CopyTo(exif, 0);

        // 500 KB would be over the budget if read; the reader takes 64 KB and skips the rest for free.
        ImageInfo info = await ReadMemoryAsync(Png(Ihdr(4, 4, 8, 2), Chunk("eXIf", exif), Idat()), budget: 100_000);

        Assert.Equal(8, info.Orientation);
    }

    [Theory]
    [InlineData(1u, false)]
    [InlineData(3u, true)]
    public async Task An_acTL_chunk_sets_the_frame_count(uint frames, bool animated)
    {
        ImageInfo info = await ReadMemoryAsync(Png(Ihdr(10, 10, 8, 6), Chunk("acTL", Actl(frames)), Idat()));

        Assert.Equal((int)frames, info.FrameCount);
        Assert.Equal(animated, info.IsAnimated);
    }

    [Fact]
    public async Task An_acTL_chunk_with_zero_frames_is_corrupt()
    {
        ImageException failure = await FailMemoryAsync(Png(Ihdr(10, 10, 8, 6), Chunk("acTL", Actl(0)), Idat()));
        Assert.Equal(IdentifyStatus.Corrupt, failure.Status);
    }

    [Fact]
    public async Task An_acTL_chunk_of_the_wrong_length_is_corrupt()
    {
        ImageException failure = await FailMemoryAsync(Png(Ihdr(10, 10, 8, 6), Chunk("acTL", new byte[5]), Idat()));
        Assert.Equal(IdentifyStatus.Corrupt, failure.Status);
    }

    [Fact]
    public async Task A_huge_frame_count_is_read_for_the_limit_check()
    {
        ImageInfo info = await ReadMemoryAsync(Png(Ihdr(10, 10, 8, 6), Chunk("acTL", Actl(uint.MaxValue)), Idat()));
        Assert.Equal(int.MaxValue, info.FrameCount);
    }

    [Fact]
    public async Task IEND_before_IDAT_is_corrupt()
    {
        ImageException failure = await FailMemoryAsync(Png(Ihdr(10, 10, 8, 2), Chunk("IEND", [])));

        Assert.Equal(IdentifyStatus.Corrupt, failure.Status);
        Assert.Contains("no image data", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chunk_type_that_is_not_letters_is_corrupt()
    {
        ImageException failure = await FailMemoryAsync(Png(Ihdr(10, 10, 8, 2), Chunk("te1t", new byte[2]), Idat()));
        Assert.Equal(IdentifyStatus.Corrupt, failure.Status);
    }

    [Fact]
    public async Task A_chunk_length_over_2_GiB_is_corrupt()
    {
        byte[] huge = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(huge, 0x80000000u);
        "tEXt"u8.CopyTo(huge.AsSpan(4));

        ImageException failure = await FailMemoryAsync(Png(Ihdr(10, 10, 8, 2), huge));
        Assert.Equal(IdentifyStatus.Corrupt, failure.Status);
    }

    [Fact]
    public async Task A_chunk_that_claims_2_GiB_but_ends_early_is_truncated()
    {
        byte[] huge = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(huge, int.MaxValue);
        "tEXt"u8.CopyTo(huge.AsSpan(4));

        ImageException failure = await FailMemoryAsync(Png(Ihdr(10, 10, 8, 2), huge, new byte[100]));
        Assert.Equal(IdentifyStatus.Truncated, failure.Status);
    }

    [Fact]
    public async Task A_file_that_ends_inside_IHDR_is_truncated()
    {
        byte[] full = Png(Ihdr(10, 10, 8, 2), Idat());
        ImageException failure = await FailMemoryAsync(full[..(8 + 8 + 5)]);
        Assert.Equal(IdentifyStatus.Truncated, failure.Status);
    }

    [Fact]
    public async Task A_file_that_ends_inside_a_chunk_header_is_truncated()
    {
        byte[] ihdr = Ihdr(10, 10, 8, 2);
        ImageException failure = await FailMemoryAsync(Png(ihdr, new byte[3]));
        Assert.Equal(IdentifyStatus.Truncated, failure.Status);
    }

    [Fact]
    public async Task A_file_that_ends_inside_skipped_chunk_data_is_truncated()
    {
        byte[] full = Png(Ihdr(10, 10, 8, 2), Chunk("tEXt", new byte[100]));
        ImageException failure = await FailMemoryAsync(full[..^50]);
        Assert.Equal(IdentifyStatus.Truncated, failure.Status);
    }

    [Fact]
    public async Task A_file_that_ends_inside_skipped_chunk_data_is_truncated_on_a_seekable_stream()
    {
        byte[] full = Png(Ihdr(10, 10, 8, 2), Chunk("tEXt", new byte[100]));
        using var stream = new MemoryStream(full[..^50]);

        ImageException failure = await Assert.ThrowsAnyAsync<ImageException>(async () =>
            await PngHeaderReader.ReadAsync(HeaderReader.FromStream(stream, Budget), IdentifyOptions.Default, TestContext.Current.CancellationToken));

        Assert.Equal(IdentifyStatus.Truncated, failure.Status);
    }

    [Fact]
    public async Task A_file_that_ends_before_any_image_data_is_truncated()
    {
        ImageException failure = await FailMemoryAsync(Png(Ihdr(10, 10, 8, 2)));
        Assert.Equal(IdentifyStatus.Truncated, failure.Status);
    }

    [Fact]
    public async Task Skipped_chunks_are_free_over_memory_and_a_seekable_stream_but_not_a_non_seekable_one()
    {
        byte[] data = Png(Ihdr(10, 10, 8, 2), Chunk("tEXt", new byte[100_000]), Idat());

        ImageInfo fromMemory = await ReadMemoryAsync(data, budget: 4096);
        Assert.Equal(10, fromMemory.Width);

        using (var seekable = new MemoryStream(data))
        {
            ImageInfo info = await PngHeaderReader.ReadAsync(HeaderReader.FromStream(seekable, 4096), IdentifyOptions.Default, TestContext.Current.CancellationToken);
            Assert.Equal(10, info.Width);
        }

        using var forwardOnly = new ForwardOnlyStream(data);
        ImageException failure = await Assert.ThrowsAnyAsync<ImageException>(async () =>
            await PngHeaderReader.ReadAsync(HeaderReader.FromStream(forwardOnly, 4096), IdentifyOptions.Default, TestContext.Current.CancellationToken));
        Assert.Equal(IdentifyStatus.ExceedsLimits, failure.Status);
    }

    [Fact]
    public async Task A_non_seekable_stream_is_read_when_the_budget_allows()
    {
        byte[] data = Png(Ihdr(10, 10, 8, 2), Chunk("eXIf", ExifWithOrientation(3)), Idat());
        using var forwardOnly = new ForwardOnlyStream(data);

        ImageInfo info = await PngHeaderReader.ReadAsync(HeaderReader.FromStream(forwardOnly, Budget), IdentifyOptions.Default, TestContext.Current.CancellationToken);

        Assert.Equal(3, info.Orientation);
    }

    [Fact]
    public void Reading_from_memory_completes_synchronously()
    {
        byte[] data = Png(Ihdr(10, 10, 8, 2), Chunk("tEXt", new byte[10]), Idat());

        ValueTask<ImageInfo> task = PngHeaderReader.ReadAsync(HeaderReader.FromMemory(data, Budget), IdentifyOptions.Default, TestContext.Current.CancellationToken);

        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public void Failing_from_memory_completes_synchronously()
    {
        byte[] data = Png(Ihdr(10, 10, 8, 2), Chunk("IEND", []));

        ValueTask<ImageInfo> task = PngHeaderReader.ReadAsync(HeaderReader.FromMemory(data, Budget), IdentifyOptions.Default, TestContext.Current.CancellationToken);

        Assert.True(task.IsCompleted);
        Assert.True(task.AsTask().IsFaulted);
    }

    private static async Task<ImageInfo> ReadMemoryAsync(byte[] data, int budget = Budget) =>
        await PngHeaderReader.ReadAsync(HeaderReader.FromMemory(data, budget), IdentifyOptions.Default, TestContext.Current.CancellationToken);

    private static async Task<ImageException> FailMemoryAsync(byte[] data) =>
        await Assert.ThrowsAnyAsync<ImageException>(async () => await ReadMemoryAsync(data));

    private static byte[] Png(params byte[][] chunks)
    {
        var bytes = new List<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        foreach (byte[] chunk in chunks)
        {
            bytes.AddRange(chunk);
        }

        return [.. bytes];
    }

    private static byte[] Idat() => Chunk("IDAT", new byte[4]);

    private static byte[] Ihdr(uint width, uint height, int bitDepth, int colourType, int compression = 0, int filter = 0, int interlace = 0)
    {
        byte[] data = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(data, width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), height);
        data[8] = (byte)bitDepth;
        data[9] = (byte)colourType;
        data[10] = (byte)compression;
        data[11] = (byte)filter;
        data[12] = (byte)interlace;
        return Chunk("IHDR", data);
    }

    private static byte[] Actl(uint frames)
    {
        byte[] data = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(data, frames);
        return data;
    }

    // A chunk with a correct checksum.
    private static byte[] Chunk(string type, byte[] data)
    {
        byte[] chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        System.Text.Encoding.ASCII.GetBytes(type, chunk.AsSpan(4));
        data.CopyTo(chunk, 8);
        uint crc = Crc32.Compute(chunk.AsSpan(4, 4 + data.Length));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), crc);
        return chunk;
    }

    // A big-endian TIFF block with one IFD entry: Orientation.
    private static byte[] ExifWithOrientation(int orientation) =>
    [
        0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
        0x00, 0x01,
        0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, (byte)orientation, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
    ];

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
