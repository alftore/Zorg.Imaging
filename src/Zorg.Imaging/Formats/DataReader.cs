using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Imaging.Formats;

/// <summary>Forward-only reading of a whole file for a decoder, from memory or a stream.</summary>
/// <remarks>
/// <para>
/// Reading past the end throws a truncated <see cref="ImageFormatException"/>, so decoders never check lengths
/// themselves. Unlike <see cref="HeaderReader"/> there is no byte budget: a decoder's work is bounded by the image
/// size, which it checks against <see cref="ImageLimits"/> first, and by the length of the input.
/// </para>
/// <para>
/// Over memory every method completes synchronously, which <see cref="Image.Decode"/> relies on. A stream is read in
/// blocks through an internal buffer, so small reads don't each reach the stream.
/// </para>
/// </remarks>
internal sealed class DataReader
{
    private const int BlockSize = 16 * 1024;

    private readonly ReadOnlyMemory<byte> _memory;
    private readonly Stream? _stream;
    private readonly byte[] _block = [];
    private int _blockStart;
    private int _blockEnd;
    private bool _endOfStream;

    private DataReader(ReadOnlyMemory<byte> memory, Stream? stream)
    {
        _memory = memory;
        _stream = stream;
        if (stream is not null)
        {
            _block = new byte[BlockSize];
        }
    }

    /// <summary>The offset from the start of the file of the next byte to read.</summary>
    public long Position { get; private set; }

    /// <summary>The format's display name, used in messages.</summary>
    public string FormatName { get; set; } = "image";

    /// <summary>Reads over a buffer that holds the whole file.</summary>
    /// <param name="data">The file.</param>
    /// <returns>The reader.</returns>
    public static DataReader FromMemory(ReadOnlyMemory<byte> data) => new(data, stream: null);

    /// <summary>Reads from a stream at its current position.</summary>
    /// <param name="stream">A readable stream.</param>
    /// <returns>The reader.</returns>
    public static DataReader FromStream(Stream stream) => new(ReadOnlyMemory<byte>.Empty, stream);

    /// <summary>Looks at up to <paramref name="count"/> bytes without consuming them; fewer only at the end of the data.</summary>
    /// <param name="count">How many bytes to look at, at most 4096.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes, valid until the next call.</returns>
    public async ValueTask<ReadOnlyMemory<byte>> PeekAsync(int count, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            return _memory.Slice((int)Position, (int)Math.Min(count, _memory.Length - Position));
        }

        while (_blockEnd - _blockStart < count && !_endOfStream)
        {
            if (_blockStart > 0)
            {
                _block.AsSpan(_blockStart, _blockEnd - _blockStart).CopyTo(_block);
                _blockEnd -= _blockStart;
                _blockStart = 0;
            }

            int read = await _stream.ReadAsync(_block.AsMemory(_blockEnd), cancellationToken).ConfigureAwait(false);
            _blockEnd += read;
            _endOfStream = read == 0;
        }

        return _block.AsMemory(_blockStart, Math.Min(count, _blockEnd - _blockStart));
    }

    /// <summary>Reads exactly <paramref name="destination"/>.Length bytes.</summary>
    /// <param name="destination">Where to read to.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes when the bytes are read.</returns>
    /// <exception cref="ImageFormatException">The data ends first.</exception>
    public ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            RequireMemory(destination.Length);
            _memory.Span.Slice((int)Position, destination.Length).CopyTo(destination.Span);
            Position += destination.Length;
            return ValueTask.CompletedTask;
        }

        int buffered = _blockEnd - _blockStart;
        if (buffered >= destination.Length)
        {
            _block.AsSpan(_blockStart, destination.Length).CopyTo(destination.Span);
            _blockStart += destination.Length;
            Position += destination.Length;
            return ValueTask.CompletedTask;
        }

        return ReadStreamExactlyAsync(_stream, destination, cancellationToken);
    }

    /// <summary>Reads up to <paramref name="destination"/>.Length bytes, at least one unless the data has ended.</summary>
    /// <param name="destination">Where to read to.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of bytes read; 0 only at the end of the data.</returns>
    public ValueTask<int> ReadSomeAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            int count = (int)Math.Min(destination.Length, _memory.Length - Position);
            _memory.Span.Slice((int)Position, count).CopyTo(destination.Span);
            Position += count;
            return new ValueTask<int>(count);
        }

        if (_blockStart < _blockEnd || destination.IsEmpty)
        {
            return new ValueTask<int>(TakeBuffered(destination.Span));
        }

        return ReadStreamSomeAsync(_stream, destination, cancellationToken);
    }

    /// <summary>Moves forward <paramref name="count"/> bytes without looking at them.</summary>
    /// <param name="count">How many bytes to skip.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes when the bytes are skipped.</returns>
    /// <exception cref="ImageFormatException">The data ends first.</exception>
    public ValueTask SkipAsync(long count, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            RequireMemory(count);
            Position += count;
            return ValueTask.CompletedTask;
        }

        int fromBlock = (int)Math.Min(count, _blockEnd - _blockStart);
        _blockStart += fromBlock;
        Position += fromBlock;
        long rest = count - fromBlock;
        if (rest == 0)
        {
            return ValueTask.CompletedTask;
        }

        if (_stream.CanSeek)
        {
            if (_stream.Length - _stream.Position < rest)
            {
                throw Truncated();
            }

            _stream.Seek(rest, SeekOrigin.Current);
            Position += rest;
            return ValueTask.CompletedTask;
        }

        return SkipByReadingAsync(_stream, rest, cancellationToken);
    }

    /// <summary>Reads the rest of the data, for a codec that needs the whole file.</summary>
    /// <param name="max">The most bytes allowed (<see cref="ImageLimits.MaxFileBytes"/>).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes from <see cref="Position"/> to the end.</returns>
    /// <exception cref="ImageLimitException">There are more than <paramref name="max"/> bytes.</exception>
    public async ValueTask<ReadOnlyMemory<byte>> ReadToEndAsync(long max, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            ReadOnlyMemory<byte> rest = _memory[(int)Position..];
            if (rest.Length > max)
            {
                throw FileTooLong(max);
            }

            Position += rest.Length;
            return rest;
        }

        long cap = Math.Min(max + 1, Array.MaxLength);
        byte[] buffer = new byte[(int)Math.Min(cap, 64 * 1024)];
        int length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                Array.Resize(ref buffer, (int)Math.Min(cap, (long)buffer.Length * 2));
            }

            int read = await ReadSomeAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.AsMemory(0, length);
            }

            length += read;
            if (length > max)
            {
                throw FileTooLong(max);
            }
        }
    }

    /// <summary>
    /// Gives back what was read ahead into the internal buffer, when the stream can seek, so the stream ends up just
    /// after the last byte the decoder used.
    /// </summary>
    public void Finish()
    {
        int unread = _blockEnd - _blockStart;
        if (_stream is { CanSeek: true } && unread > 0)
        {
            _stream.Seek(-unread, SeekOrigin.Current);
            _blockStart = _blockEnd;
        }
    }

    /// <summary>The exception for data that ends early.</summary>
    /// <returns>The exception to throw.</returns>
    public ImageFormatException Truncated() =>
        ImageErrors.Truncated(string.Create(CultureInfo.InvariantCulture, $"The {FormatName} file ends early, at byte {Position}."));

    private ImageLimitException FileTooLong(long max) =>
        ImageErrors.ExceedsLimits(string.Create(CultureInfo.InvariantCulture, $"The {FormatName} file is longer than {max} bytes, the limit (ImageLimits.MaxFileBytes)."));

    private async ValueTask ReadStreamExactlyAsync(Stream source, Memory<byte> destination, CancellationToken cancellationToken)
    {
        while (!destination.IsEmpty)
        {
            if (_blockStart == _blockEnd && !await FillAsync(source, cancellationToken).ConfigureAwait(false))
            {
                throw Truncated();
            }

            int taken = TakeBuffered(destination.Span);
            destination = destination[taken..];
        }
    }

    private async ValueTask<int> ReadStreamSomeAsync(Stream source, Memory<byte> destination, CancellationToken cancellationToken) =>
        await FillAsync(source, cancellationToken).ConfigureAwait(false) ? TakeBuffered(destination.Span) : 0;

    private async ValueTask SkipByReadingAsync(Stream source, long count, CancellationToken cancellationToken)
    {
        while (count > 0)
        {
            if (!await FillAsync(source, cancellationToken).ConfigureAwait(false))
            {
                throw Truncated();
            }

            int taken = (int)Math.Min(count, _blockEnd - _blockStart);
            _blockStart += taken;
            Position += taken;
            count -= taken;
        }
    }

    private async ValueTask<bool> FillAsync(Stream source, CancellationToken cancellationToken)
    {
        // Only called with the buffer empty.
        if (_endOfStream)
        {
            return false;
        }

        int read = await source.ReadAsync(_block, cancellationToken).ConfigureAwait(false);
        _blockStart = 0;
        _blockEnd = read;
        _endOfStream = read == 0;
        return read > 0;
    }

    private int TakeBuffered(Span<byte> destination)
    {
        int count = Math.Min(destination.Length, _blockEnd - _blockStart);
        _block.AsSpan(_blockStart, count).CopyTo(destination);
        _blockStart += count;
        Position += count;
        return count;
    }

    private void RequireMemory(long count)
    {
        if (_memory.Length - Position < count)
        {
            throw Truncated();
        }
    }
}
