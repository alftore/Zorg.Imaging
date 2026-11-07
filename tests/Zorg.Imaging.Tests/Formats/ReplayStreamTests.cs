#pragma warning disable CA2213 // Test stream wrappers leave the wrapped stream to the test.
#pragma warning disable CA1849, CA1835 // The read helpers exercise every read overload on purpose.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Tests.Formats;

public class ReplayStreamTests
{
    public enum ReadMode
    {
        ByteArray,
        Span,
        MemoryAsync,
        ByteArrayAsync,
        ByteAtATime,
    }

    public static TheoryData<ReadMode, int> ModesAndChunks
    {
        get
        {
            TheoryData<ReadMode, int> data = [];
            foreach (ReadMode mode in Enum.GetValues<ReadMode>())
            {
                foreach (int chunk in new[] { 1, 3, 7, 64, 1000 })
                {
                    data.Add(mode, chunk);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ModesAndChunks))]
    public async Task Yields_the_prefix_then_the_rest_of_the_inner_stream(ReadMode mode, int chunk)
    {
        byte[] all = Pattern(300);
        using MemoryStream inner = new(all, 40, all.Length - 40);
        using ReplayStream stream = new(all.AsMemory(0, 40), inner);

        byte[] read = await ReadAllAsync(stream, mode, chunk);

        Assert.Equal(all, read);
    }

    [Theory]
    [MemberData(nameof(ModesAndChunks))]
    public async Task Works_over_an_inner_stream_that_returns_short_reads(ReadMode mode, int chunk)
    {
        byte[] all = Pattern(200);
        using MemoryStream source = new(all, 10, all.Length - 10);
        using TrickleStream inner = new(source, 2);
        using ReplayStream stream = new(all.AsMemory(0, 10), inner);

        byte[] read = await ReadAllAsync(stream, mode, chunk);

        Assert.Equal(all, read);
    }

    [Fact]
    public async Task Empty_prefix_passes_straight_through()
    {
        byte[] all = Pattern(50);
        using MemoryStream inner = new(all);
        using ReplayStream stream = new(ReadOnlyMemory<byte>.Empty, inner);

        Assert.Equal(all, await ReadAllAsync(stream, ReadMode.MemoryAsync, 16));
    }

    [Fact]
    public async Task Empty_prefix_and_empty_inner_reads_nothing()
    {
        using MemoryStream inner = new();
        using ReplayStream stream = new(ReadOnlyMemory<byte>.Empty, inner);

        Assert.Equal(-1, stream.ReadByte());
        Assert.Empty(await ReadAllAsync(stream, ReadMode.ByteArray, 8));
    }

    [Fact]
    public void Prefix_only_stream_reports_the_end_after_the_prefix()
    {
        using MemoryStream inner = new();
        using ReplayStream stream = new(new byte[] { 1, 2, 3 }, inner);
        byte[] buffer = new byte[10];

        Assert.Equal(3, stream.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, stream.Read(buffer, 0, buffer.Length));
        Assert.Equal(-1, stream.ReadByte());
    }

    [Fact]
    public void A_read_never_returns_more_than_asked_from_the_prefix()
    {
        byte[] all = Pattern(20);
        using MemoryStream inner = new(all, 10, 10);
        using ReplayStream stream = new(all.AsMemory(0, 10), inner);
        byte[] buffer = new byte[4];

        Assert.Equal(4, stream.Read(buffer, 0, 4));
        Assert.Equal(all[..4], buffer);
        Assert.Equal(4, stream.Read(buffer, 0, 4));
        Assert.Equal(all[4..8], buffer);
    }

    [Fact]
    public void Read_into_an_empty_buffer_returns_zero_and_consumes_nothing()
    {
        byte[] all = Pattern(10);
        using MemoryStream inner = new(all, 4, 6);
        using ReplayStream stream = new(all.AsMemory(0, 4), inner);

        Assert.Equal(0, stream.Read([]));
        Assert.Equal(all[0], stream.ReadByte());
    }

    [Fact]
    public void Reports_that_it_is_a_read_only_forward_only_stream()
    {
        using MemoryStream inner = new([1]);
        using ReplayStream stream = new(new byte[] { 1 }, inner);

        Assert.True(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
    }

    [Fact]
    public void Seeking_writing_and_length_are_not_supported()
    {
        using MemoryStream inner = new([1]);
        using ReplayStream stream = new(new byte[] { 1 }, inner);

        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.WriteByte(1));
    }

    [Fact]
    public void Flush_does_nothing()
    {
        using MemoryStream inner = new();
        using ReplayStream stream = new(ReadOnlyMemory<byte>.Empty, inner);

        stream.Flush();
    }

    [Fact]
    public void Invalid_buffer_arguments_are_rejected()
    {
        using MemoryStream inner = new([1, 2, 3]);
        using ReplayStream stream = new(new byte[] { 1 }, inner);
        byte[] buffer = new byte[4];

        Assert.Throws<ArgumentNullException>(() => stream.Read(null!, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(buffer, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Read(buffer, 0, 5));
        Assert.ThrowsAny<ArgumentException>(() => stream.Read(buffer, 3, 2));
    }

    [Fact]
    public async Task Invalid_buffer_arguments_are_rejected_by_ReadAsync()
    {
        using MemoryStream inner = new([1, 2, 3]);
        using ReplayStream stream = new(new byte[] { 1 }, inner);
        byte[] buffer = new byte[4];

        await Assert.ThrowsAsync<ArgumentNullException>(() => stream.ReadAsync(null!, 0, 1, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => stream.ReadAsync(buffer, 3, 2, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_cancelled_token_cancels_ReadAsync()
    {
        using MemoryStream inner = new([1, 2, 3]);
        using ReplayStream stream = new(new byte[] { 1 }, inner);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.ReadExactlyAsync(new byte[2], cts.Token));
    }

    [Fact]
    public void Disposing_does_not_dispose_the_inner_stream()
    {
        using MemoryStream inner = new([1, 2, 3]);
        ReplayStream stream = new(new byte[] { 9 }, inner);

        stream.Dispose();

        Assert.True(inner.CanRead);
        Assert.Equal(1, inner.ReadByte());
    }

    [Fact]
    public void CopyTo_returns_every_byte()
    {
        byte[] all = Pattern(5000);
        using MemoryStream inner1 = new(all, 33, all.Length - 33);
        using ReplayStream stream1 = new(all.AsMemory(0, 33), inner1);
        using MemoryStream output1 = new();
        stream1.CopyTo(output1);
        Assert.Equal(all, output1.ToArray());
    }

    [Fact]
    public async Task CopyToAsync_returns_every_byte()
    {
        byte[] all = Pattern(5000);
        using MemoryStream inner2 = new(all, 33, all.Length - 33);
        using ReplayStream stream2 = new(all.AsMemory(0, 33), inner2);
        using MemoryStream output2 = new();
        await stream2.CopyToAsync(output2, TestContext.Current.CancellationToken);
        Assert.Equal(all, output2.ToArray());
    }

    private static byte[] Pattern(int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 7) + 3);
        }

        return bytes;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, ReadMode mode, int chunk)
    {
        using MemoryStream result = new();
        byte[] buffer = new byte[chunk];
        CancellationToken token = TestContext.Current.CancellationToken;
        while (true)
        {
            int read;
            switch (mode)
            {
                case ReadMode.ByteArray:
                    read = stream.Read(buffer, 0, buffer.Length);
                    break;
                case ReadMode.Span:
                    read = stream.Read(buffer.AsSpan());
                    break;
                case ReadMode.MemoryAsync:
                    read = await stream.ReadAsync(buffer.AsMemory(), token);
                    break;
                case ReadMode.ByteArrayAsync:
                    read = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                    break;
                default:
                    int value = stream.ReadByte();
                    if (value < 0)
                    {
                        return result.ToArray();
                    }

                    result.WriteByte((byte)value);
                    continue;
            }

            if (read == 0)
            {
                return result.ToArray();
            }

            result.Write(buffer, 0, read);
        }
    }

    /// <summary>A stream that returns at most a few bytes per read, like a socket.</summary>
    private sealed class TrickleStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _maxChunk;

        public TrickleStream(Stream inner, int maxChunk)
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
}
