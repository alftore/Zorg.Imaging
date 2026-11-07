#pragma warning disable CA2213 // Test stream wrappers leave the wrapped stream to the test.
#pragma warning disable CA2012 // Some tests look at a ValueTask without awaiting it, to check it completed synchronously.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Tests.Formats;

public class HeaderReaderTests
{
    private const string MemoryKind = "memory";
    private const string SeekableKind = "seekable";
    private const string ForwardKind = "forward";

    public static TheoryData<string> AllKinds { get; } = [MemoryKind, SeekableKind, ForwardKind];

    public static TheoryData<string> FreeSkipKinds { get; } = [MemoryKind, SeekableKind];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Peek_does_not_consume(string kind)
    {
        byte[] data = Pattern(100);
        HeaderReader reader = Create(kind, data, 1000);
        byte[] peeked = new byte[8];
        byte[] again = new byte[8];

        Assert.Equal(8, await reader.PeekAsync(peeked, Token));
        Assert.Equal(8, await reader.PeekAsync(again, Token));
        Assert.Equal(0, reader.Position);
        Assert.Equal(data[..8], peeked);
        Assert.Equal(data[..8], again);

        byte[] read = new byte[8];
        await reader.ReadExactlyAsync(read, Token);
        Assert.Equal(data[..8], read);
        Assert.Equal(8, reader.Position);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Peek_of_a_larger_span_after_a_smaller_one_returns_the_first_bytes_both_times(string kind)
    {
        byte[] data = Pattern(100);
        HeaderReader reader = Create(kind, data, 1000);
        byte[] small = new byte[4];
        byte[] large = new byte[64];

        Assert.Equal(4, await reader.PeekAsync(small, Token));
        Assert.Equal(64, await reader.PeekAsync(large, Token));
        Assert.Equal(data[..4], small);
        Assert.Equal(data[..64], large);

        byte[] all = new byte[100];
        await reader.ReadExactlyAsync(all, Token);
        Assert.Equal(data, all);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Peek_past_the_end_copies_only_what_is_there(string kind)
    {
        byte[] data = Pattern(5);
        HeaderReader reader = Create(kind, data, 1000);
        byte[] peeked = new byte[8];

        Assert.Equal(5, await reader.PeekAsync(peeked, Token));
        Assert.Equal(data, peeked[..5]);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Peek_of_empty_data_returns_zero(string kind)
    {
        HeaderReader reader = Create(kind, [], 1000);

        Assert.Equal(0, await reader.PeekAsync(new byte[8], Token));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Peek_follows_the_current_position(string kind)
    {
        byte[] data = Pattern(100);
        HeaderReader reader = Create(kind, data, 1000);
        await reader.SkipAsync(10, Token);
        byte[] peeked = new byte[4];

        Assert.Equal(4, await reader.PeekAsync(peeked, Token));
        Assert.Equal(data[10..14], peeked);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task ReadExactly_reads_consecutive_blocks(string kind)
    {
        byte[] data = Pattern(100);
        HeaderReader reader = Create(kind, data, 1000);
        byte[] first = new byte[10];
        byte[] second = new byte[20];

        await reader.ReadExactlyAsync(first, Token);
        await reader.ReadExactlyAsync(second, Token);

        Assert.Equal(data[..10], first);
        Assert.Equal(data[10..30], second);
        Assert.Equal(30, reader.Position);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task ReadExactly_of_zero_bytes_succeeds(string kind)
    {
        HeaderReader reader = Create(kind, Pattern(4), 1000);

        await reader.ReadExactlyAsync(Memory<byte>.Empty, Token);

        Assert.Equal(0, reader.Position);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task ReadByte_reads_consecutive_bytes(string kind)
    {
        byte[] data = Pattern(10);
        HeaderReader reader = Create(kind, data, 1000);

        for (int i = 0; i < data.Length; i++)
        {
            Assert.Equal(data[i], await reader.ReadByteAsync(Token));
            Assert.Equal(i + 1, reader.Position);
        }
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task ReadByte_after_Peek_reads_the_peeked_bytes_first(string kind)
    {
        byte[] data = Pattern(100);
        HeaderReader reader = Create(kind, data, 1000);
        await reader.PeekAsync(new byte[8], Token);

        for (int i = 0; i < 12; i++)
        {
            Assert.Equal(data[i], await reader.ReadByteAsync(Token));
        }

        Assert.Equal(12, reader.Position);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task ReadExactly_after_Peek_continues_into_the_stream(string kind)
    {
        byte[] data = Pattern(100);
        HeaderReader reader = Create(kind, data, 1000);
        await reader.PeekAsync(new byte[8], Token);
        byte[] read = new byte[20];

        await reader.ReadExactlyAsync(read, Token);

        Assert.Equal(data[..20], read);
        Assert.Equal(20, reader.Position);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Skip_moves_the_position_and_the_next_read_follows(string kind)
    {
        byte[] data = Pattern(100);
        HeaderReader reader = Create(kind, data, 1000);

        await reader.SkipAsync(30, Token);
        Assert.Equal(30, reader.Position);
        Assert.Equal(data[30], await reader.ReadByteAsync(Token));

        await reader.SkipAsync(0, Token);
        Assert.Equal(31, reader.Position);
        Assert.Equal(data[31], await reader.ReadByteAsync(Token));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Skip_within_and_beyond_peeked_bytes_lands_on_the_right_byte(string kind)
    {
        byte[] data = Pattern(100);
        HeaderReader reader = Create(kind, data, 1000);
        await reader.PeekAsync(new byte[8], Token);

        await reader.SkipAsync(3, Token);
        Assert.Equal(data[3], await reader.ReadByteAsync(Token));

        await reader.SkipAsync(20, Token);
        Assert.Equal(24, reader.Position);
        Assert.Equal(data[24], await reader.ReadByteAsync(Token));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Skip_to_exactly_the_end_succeeds_and_one_more_is_truncated(string kind)
    {
        HeaderReader reader = Create(kind, Pattern(10), 1000);

        await reader.SkipAsync(10, Token);
        Assert.Equal(10, reader.Position);

        IdentifyFailure failure = await Assert.ThrowsAsync<IdentifyFailure>(async () => await reader.SkipAsync(1, Token));
        Assert.Equal(IdentifyStatus.Truncated, failure.Status);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Stream_readers_start_at_the_streams_current_position(string kind)
    {
        byte[] data = Pattern(100);
        byte[] padded = new byte[110];
        data.CopyTo(padded, 10);
        HeaderReader reader = kind switch
        {
            MemoryKind => HeaderReader.FromMemory(data, 1000),
            SeekableKind => HeaderReader.FromStream(StartedAt(new MemoryStream(padded), 10), 1000),
            _ => HeaderReader.FromStream(StartedAt(new NonSeekableStream(new MemoryStream(padded), 5), 10), 1000),
        };

        Assert.Equal(0, reader.Position);
        Assert.Equal(data[0], await reader.ReadByteAsync(Token));
        await reader.SkipAsync(9, Token);
        Assert.Equal(data[10], await reader.ReadByteAsync(Token));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task ReadExactly_past_the_end_is_truncated(string kind)
    {
        HeaderReader reader = Create(kind, Pattern(5), 1000);

        IdentifyFailure failure = await Assert.ThrowsAsync<IdentifyFailure>(async () => await reader.ReadExactlyAsync(new byte[10], Token));

        Assert.Equal(IdentifyStatus.Truncated, failure.Status);
        Assert.False(string.IsNullOrWhiteSpace(failure.Message));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task ReadByte_at_the_end_is_truncated(string kind)
    {
        HeaderReader reader = Create(kind, Pattern(2), 1000);
        await reader.ReadByteAsync(Token);
        await reader.ReadByteAsync(Token);

        IdentifyFailure failure = await Assert.ThrowsAsync<IdentifyFailure>(async () => await reader.ReadByteAsync(Token));

        Assert.Equal(IdentifyStatus.Truncated, failure.Status);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task ReadByte_on_empty_data_is_truncated(string kind)
    {
        HeaderReader reader = Create(kind, [], 1000);

        IdentifyFailure failure = await Assert.ThrowsAsync<IdentifyFailure>(async () => await reader.ReadByteAsync(Token));

        Assert.Equal(IdentifyStatus.Truncated, failure.Status);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Skip_past_the_end_is_truncated(string kind)
    {
        HeaderReader reader = Create(kind, Pattern(10), 1000);

        IdentifyFailure failure = await Assert.ThrowsAsync<IdentifyFailure>(async () => await reader.SkipAsync(11, Token));

        Assert.Equal(IdentifyStatus.Truncated, failure.Status);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Skip_of_a_huge_count_is_truncated_without_overflow(string kind)
    {
        HeaderReader reader = Create(kind, Pattern(10), int.MaxValue);

        IdentifyFailure failure = await Assert.ThrowsAsync<IdentifyFailure>(async () => await reader.SkipAsync(long.MaxValue, Token));

        // A forward-only stream is charged for the skip before it runs out of data, so it reports the budget instead.
        Assert.Contains(failure.Status, kind == ForwardKind
            ? new[] { IdentifyStatus.Truncated, IdentifyStatus.ExceedsLimits }
            : new[] { IdentifyStatus.Truncated });
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Negative_skip_is_corrupt(string kind)
    {
        HeaderReader reader = Create(kind, Pattern(10), 1000);

        IdentifyFailure failure = await Assert.ThrowsAsync<IdentifyFailure>(async () => await reader.SkipAsync(-1, Token));

        Assert.Equal(IdentifyStatus.Corrupt, failure.Status);
        Assert.Equal(0, reader.Position);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Reading_over_the_budget_exceeds_limits(string kind)
    {
        HeaderReader reader = Create(kind, Pattern(100), 4);

        IdentifyFailure failure = await Assert.ThrowsAsync<IdentifyFailure>(async () => await reader.ReadExactlyAsync(new byte[5], Token));

        Assert.Equal(IdentifyStatus.ExceedsLimits, failure.Status);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Reading_exactly_the_budget_is_allowed_and_one_more_byte_is_not(string kind)
    {
        HeaderReader reader = Create(kind, Pattern(100), 4);

        await reader.ReadExactlyAsync(new byte[4], Token);
        Assert.Equal(4, reader.BytesCharged);

        IdentifyFailure failure = await Assert.ThrowsAsync<IdentifyFailure>(async () => await reader.ReadByteAsync(Token));
        Assert.Equal(IdentifyStatus.ExceedsLimits, failure.Status);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task Reading_bytes_one_at_a_time_is_charged_for_each(string kind)
    {
        HeaderReader reader = Create(kind, Pattern(100), 3);

        await reader.ReadByteAsync(Token);
        await reader.ReadByteAsync(Token);
        await reader.ReadByteAsync(Token);

        IdentifyFailure failure = await Assert.ThrowsAsync<IdentifyFailure>(async () => await reader.ReadByteAsync(Token));
        Assert.Equal(IdentifyStatus.ExceedsLimits, failure.Status);
        Assert.Equal(3, reader.BytesCharged);
    }

    [Theory]
    [MemberData(nameof(FreeSkipKinds))]
    public async Task Skipping_on_memory_or_a_seekable_stream_is_not_charged(string kind)
    {
        HeaderReader reader = Create(kind, Pattern(200), 4);

        await reader.SkipAsync(150, Token);

        Assert.Equal(0, reader.BytesCharged);
        Assert.Equal(150, reader.Position);
        Assert.True(reader.CanSeek);
        Assert.Equal(Pattern(200)[150], await reader.ReadByteAsync(Token));
        Assert.Equal(1, reader.BytesCharged);
    }

    [Fact]
    public async Task Skipping_on_a_forward_only_stream_is_charged()
    {
        HeaderReader reader = Create(ForwardKind, Pattern(200), 10);
        Assert.False(reader.CanSeek);

        await reader.SkipAsync(10, Token);

        Assert.Equal(10, reader.BytesCharged);
        Assert.Equal(10, reader.Position);
        IdentifyFailure failure = await Assert.ThrowsAsync<IdentifyFailure>(async () => await reader.SkipAsync(1, Token));
        Assert.Equal(IdentifyStatus.ExceedsLimits, failure.Status);
    }

    [Fact]
    public async Task A_big_skip_on_a_forward_only_stream_exceeds_the_budget_before_reading()
    {
        byte[] data = Pattern(200);
        using RecordingStream recording = new(new NonSeekableStream(new MemoryStream(data), 16));
        HeaderReader reader = HeaderReader.FromStream(recording, 50);

        IdentifyFailure failure = await Assert.ThrowsAsync<IdentifyFailure>(async () => await reader.SkipAsync(100, Token));

        Assert.Equal(IdentifyStatus.ExceedsLimits, failure.Status);
        Assert.Equal(0, recording.BytesRead);
    }

    [Fact]
    public async Task Skipping_a_forward_only_stream_in_pieces_lands_on_the_right_byte()
    {
        byte[] data = Pattern(20_000);
        HeaderReader reader = Create(ForwardKind, data, 100_000);

        await reader.SkipAsync(10_000, Token);
        await reader.SkipAsync(5_000, Token);

        Assert.Equal(15_000, reader.Position);
        Assert.Equal(data[15_000], await reader.ReadByteAsync(Token));
    }

    [Fact]
    public async Task Skipping_a_seekable_stream_does_not_read_it()
    {
        byte[] data = Pattern(200);
        using RecordingStream recording = new(new MemoryStream(data));
        HeaderReader reader = HeaderReader.FromStream(recording, 4);

        await reader.SkipAsync(150, Token);

        Assert.Equal(0, recording.BytesRead);
        Assert.Equal(data[150], await reader.ReadByteAsync(Token));
    }

    [Fact]
    public async Task Peeking_is_charged_on_a_stream_but_not_on_memory()
    {
        HeaderReader memory = Create(MemoryKind, Pattern(100), 1000);
        HeaderReader stream = Create(ForwardKind, Pattern(100), 1000);

        await memory.PeekAsync(new byte[8], Token);
        await stream.PeekAsync(new byte[8], Token);

        Assert.Equal(0, memory.BytesCharged);
        Assert.Equal(8, stream.BytesCharged);
    }

    [Fact]
    public async Task Peeked_bytes_are_not_charged_again_when_read()
    {
        HeaderReader reader = Create(ForwardKind, Pattern(100), 1000);

        await reader.PeekAsync(new byte[8], Token);
        await reader.ReadExactlyAsync(new byte[8], Token);

        Assert.Equal(8, reader.BytesCharged);
    }

    [Fact]
    public async Task Peeking_over_the_budget_exceeds_limits_on_a_stream()
    {
        HeaderReader reader = Create(ForwardKind, Pattern(100), 4);

        IdentifyFailure failure = await Assert.ThrowsAsync<IdentifyFailure>(async () => await reader.PeekAsync(new byte[8], Token));

        Assert.Equal(IdentifyStatus.ExceedsLimits, failure.Status);
    }

    [Fact]
    public void Can_seek_is_true_for_memory_and_seekable_streams_only()
    {
        Assert.True(Create(MemoryKind, [], 1).CanSeek);
        Assert.True(Create(SeekableKind, [], 1).CanSeek);
        Assert.False(Create(ForwardKind, [], 1).CanSeek);
    }

    [Fact]
    public void FormatName_defaults_to_image_and_appears_in_messages()
    {
        HeaderReader reader = Create(MemoryKind, Pattern(2), 100);
        Assert.Equal("image", reader.FormatName);
        reader.FormatName = "PNG";

        IdentifyFailure failure = Assert.Throws<IdentifyFailure>(() => reader.ReadExactlyAsync(new byte[5], Token));

        Assert.Contains("PNG", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Memory_methods_complete_synchronously()
    {
        HeaderReader reader = Create(MemoryKind, Pattern(100), 1000);
        byte[] buffer = new byte[8];

        ValueTask<int> peek = reader.PeekAsync(buffer, Token);
        Assert.True(peek.IsCompletedSuccessfully);

        ValueTask read = reader.ReadExactlyAsync(buffer, Token);
        Assert.True(read.IsCompletedSuccessfully);

        ValueTask<byte> single = reader.ReadByteAsync(Token);
        Assert.True(single.IsCompletedSuccessfully);

        ValueTask skip = reader.SkipAsync(10, Token);
        Assert.True(skip.IsCompletedSuccessfully);
    }

    [Fact]
    public void Memory_failures_are_thrown_synchronously()
    {
        HeaderReader reader = Create(MemoryKind, Pattern(4), 1000);

        Assert.Throws<IdentifyFailure>(() => reader.ReadExactlyAsync(new byte[8], Token));
        Assert.Throws<IdentifyFailure>(() => reader.SkipAsync(8, Token));
        Assert.Throws<IdentifyFailure>(() => reader.SkipAsync(-1, Token));
    }

    [Fact]
    public async Task Memory_readers_over_a_prefix_of_the_file_report_truncation_at_its_end()
    {
        HeaderReader reader = HeaderReader.FromMemory(Pattern(100).AsMemory(0, 20), 1000);
        await reader.SkipAsync(20, Token);

        IdentifyFailure failure = await Assert.ThrowsAsync<IdentifyFailure>(async () => await reader.ReadByteAsync(Token));

        Assert.Equal(IdentifyStatus.Truncated, failure.Status);
    }

    [Fact]
    public async Task A_stream_that_returns_a_byte_at_a_time_is_read_fully()
    {
        byte[] data = Pattern(64);
        HeaderReader reader = HeaderReader.FromStream(new NonSeekableStream(new MemoryStream(data), 1), 1000);
        byte[] peeked = new byte[64];
        byte[] read = new byte[64];

        Assert.Equal(64, await reader.PeekAsync(peeked, Token));
        await reader.ReadExactlyAsync(read, Token);

        Assert.Equal(data, peeked);
        Assert.Equal(data, read);
    }

    private static HeaderReader Create(string kind, byte[] data, int budget) => kind switch
    {
        MemoryKind => HeaderReader.FromMemory(data, budget),
        SeekableKind => HeaderReader.FromStream(new MemoryStream(data), budget),
        ForwardKind => HeaderReader.FromStream(new NonSeekableStream(new MemoryStream(data), 3), budget),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static MemoryStream StartedAt(MemoryStream stream, long position)
    {
        stream.Position = position;
        return stream;
    }

    private static NonSeekableStream StartedAt(NonSeekableStream stream, int skip)
    {
        byte[] discard = new byte[skip];
        stream.ReadExactly(discard);
        return stream;
    }

    private static byte[] Pattern(int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 13) + 1);
        }

        return bytes;
    }

    /// <summary>A read-only stream that can't seek and returns at most <c>maxChunk</c> bytes per read.</summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _maxChunk;

        public NonSeekableStream(Stream inner, int maxChunk)
        {
            _inner = inner;
            _maxChunk = maxChunk;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, _maxChunk));

        public override int Read(Span<byte> buffer) => _inner.Read(buffer[..Math.Min(buffer.Length, _maxChunk)]);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer[..Math.Min(buffer.Length, _maxChunk)], cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Counts the bytes read from the wrapped stream.</summary>
    private sealed class RecordingStream : Stream
    {
        private readonly Stream _inner;

        public RecordingStream(Stream inner) => _inner = inner;

        public long BytesRead { get; private set; }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = _inner.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await _inner.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
