using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Codecs.Png;

/// <summary>
/// The data of a run of consecutive IDAT chunks as one stream, for <see cref="System.IO.Compression.ZLibStream"/>.
/// Each chunk's CRC is checked as its end is reached. The stream ends at the first chunk that isn't IDAT, whose
/// header is kept for the decoder.
/// </summary>
internal sealed class IdatStream : Stream
{
    private const uint IdatType = 0x49444154;

    private readonly DataReader _reader;
    private readonly bool _sync;
    private readonly byte[] _head = new byte[8];
    private long _remaining;
    private uint _crc;
    private bool _ended;
    private (long Length, uint Type) _next;

    /// <summary>Starts at the data of the first IDAT chunk, whose header has been read.</summary>
    /// <param name="reader">The reader, positioned at the chunk's data.</param>
    /// <param name="length">The first chunk's data length.</param>
    /// <param name="sync">Whether the reader is over memory, so the synchronous read path may be used.</param>
    public IdatStream(DataReader reader, long length, bool sync)
    {
        _reader = reader;
        _sync = sync;
        StartChunk(length);
    }

    /// <inheritdoc/>
    public override bool CanRead => true;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Reads past whatever image data is left, checking CRCs but not inflating it, and returns the header of the chunk
    /// after the IDAT run.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The next chunk's length and type.</returns>
    public async ValueTask<(long Length, uint Type)> SkipRestAsync(CancellationToken cancellationToken)
    {
        byte[] scratch = new byte[4096];
        while (await ReadCoreAsync(scratch, cancellationToken).ConfigureAwait(false) > 0)
        {
        }

        return _next;
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        if (!_sync)
        {
            throw new NotSupportedException("Only the asynchronous path reads a stream.");
        }

        ValueTask<int> task = ReadCoreAsync(buffer.AsMemory(offset, count), CancellationToken.None);
        Debug.Assert(task.IsCompleted, "A reader over memory completes at once.");
        return task.GetAwaiter().GetResult();
    }

    /// <inheritdoc/>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ReadCoreAsync(buffer, cancellationToken);

    /// <inheritdoc/>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadCoreAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc/>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (_remaining == 0)
        {
            if (_ended)
            {
                return 0;
            }

            await _reader.ReadExactlyAsync(_head.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
            if (_crc != BinaryPrimitives.ReadUInt32BigEndian(_head))
            {
                throw ImageErrors.Corrupt("The PNG file's IDAT chunk fails its CRC check.");
            }

            (long length, uint type) = await PngDecoder.ReadChunkHeaderAsync(_reader, _head, cancellationToken).ConfigureAwait(false);
            if (type != IdatType)
            {
                _ended = true;
                _next = (length, type);
                return 0;
            }

            StartChunk(length);
        }

        int read = await _reader.ReadSomeAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            throw _reader.Truncated();
        }

        _crc = Crc32.Append(_crc, buffer.Span[..read]);
        _remaining -= read;
        return read;
    }

    private void StartChunk(long length)
    {
        _remaining = length;
        _crc = Crc32.Compute("IDAT"u8);
    }
}
