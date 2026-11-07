using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Imaging.Codecs.Png;

/// <summary>A write-only stream that wraps what is written to it in IDAT chunks of up to 64 KiB.</summary>
internal sealed class PngChunkWriter : Stream
{
    private const int ChunkData = 64 * 1024;

    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The caller owns the output stream.")]
    private readonly Stream _output;
    private readonly bool _sync;
    private readonly byte[] _chunk = new byte[8 + ChunkData + 4];
    private int _filled;

    /// <summary>Wraps a stream.</summary>
    /// <param name="output">Where the chunks go.</param>
    /// <param name="sync">Whether writes to <paramref name="output"/> are synchronous.</param>
    public PngChunkWriter(Stream output, bool sync)
    {
        _output = output;
        _sync = sync;
    }

    /// <inheritdoc/>
    public override bool CanRead => false;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => true;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Writes the length, type and CRC around a chunk's data, which is already in place after 8 bytes.</summary>
    /// <param name="chunk">The chunk: 8 bytes of room, the data, 4 bytes of room.</param>
    /// <param name="type">The four-letter type.</param>
    /// <param name="length">The data length.</param>
    public static void Frame(Span<byte> chunk, ReadOnlySpan<byte> type, int length)
    {
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)length);
        type.CopyTo(chunk[4..]);
        BinaryPrimitives.WriteUInt32BigEndian(chunk[(8 + length)..], Crc32.Compute(chunk.Slice(4, 4 + length)));
    }

    /// <summary>Writes what is left as a last, shorter chunk.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when it's written.</returns>
    public ValueTask FinishAsync(CancellationToken cancellationToken) =>
        _filled > 0 ? EmitAsync(cancellationToken) : ValueTask.CompletedTask;

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            int take = Append(buffer);
            buffer = buffer[take..];
            if (_filled == ChunkData)
            {
                Frame(_chunk, "IDAT"u8, _filled);
                _output.Write(_chunk, 0, 8 + _filled + 4);
                _filled = 0;
            }
        }
    }

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (!buffer.IsEmpty)
        {
            int take = Append(buffer.Span);
            buffer = buffer[take..];
            if (_filled == ChunkData)
            {
                await EmitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc/>
    public override void Flush()
    {
        // Chunks are written when full, or by FinishAsync; a flush from zlib doesn't end a chunk early.
    }

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    private int Append(ReadOnlySpan<byte> data)
    {
        int take = Math.Min(data.Length, ChunkData - _filled);
        data[..take].CopyTo(_chunk.AsSpan(8 + _filled));
        _filled += take;
        return take;
    }

    private async ValueTask EmitAsync(CancellationToken cancellationToken)
    {
        Frame(_chunk, "IDAT"u8, _filled);
        int length = 8 + _filled + 4;
        _filled = 0;
        await PngEncoder.WriteAsync(_output, _chunk.AsMemory(0, length), _sync, cancellationToken).ConfigureAwait(false);
    }
}
