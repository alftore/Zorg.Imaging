using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Imaging.Formats;

/// <summary>
/// Forward-only reading of image headers from memory or a stream, within a byte budget
/// (<see cref="ImageLimits.MaxHeaderBytes"/>).
/// </summary>
/// <remarks>
/// <para>
/// Every problem is thrown as an <see cref="ImageException"/>: reading past the end is
/// <see cref="IdentifyStatus.Truncated"/>, going over the budget is <see cref="IdentifyStatus.ExceedsLimits"/>.
/// Readers never need to check lengths themselves.
/// </para>
/// <para>
/// The budget counts bytes read, and bytes skipped on a stream that can't seek. Skipping over memory or a seekable
/// stream is free, because nothing is read.
/// </para>
/// <para>
/// Over memory every method completes synchronously, which <see cref="Image.Identify"/> relies on. Readers must only
/// await this class (and each other), never anything that could yield.
/// </para>
/// </remarks>
internal sealed class HeaderReader
{
    private const int SkipChunk = 4096;

    private readonly ReadOnlyMemory<byte> _memory;
    private readonly Stream? _stream;
    private readonly int _budget;
    private long _charged;
    private byte[] _lookahead = [];
    private int _lookaheadStart;
    private int _lookaheadEnd;
    private byte[]? _skipBuffer;
    private byte[]? _oneByte;
    private bool _endOfStream;

    private HeaderReader(ReadOnlyMemory<byte> memory, Stream? stream, int budget)
    {
        _memory = memory;
        _stream = stream;
        _budget = budget;
    }

    /// <summary>The offset from the start of the file of the next byte to read.</summary>
    public long Position { get; private set; }

    /// <summary>Whether skipping is free (memory, or a seekable stream).</summary>
    public bool CanSeek => _stream is null || _stream.CanSeek;

    /// <summary>Total bytes counted against the budget so far. For tests.</summary>
    public long BytesCharged => _charged;

    /// <summary>The format's display name, used in messages. Set by <see cref="Identifier"/> once the format is known.</summary>
    public string FormatName { get; set; } = "image";

    /// <summary>Reads over a buffer that holds the file, or its start.</summary>
    /// <param name="data">The file.</param>
    /// <param name="budget">The most bytes that may be read.</param>
    /// <returns>The reader.</returns>
    public static HeaderReader FromMemory(ReadOnlyMemory<byte> data, int budget) => new(data, stream: null, budget);

    /// <summary>Reads from a stream at its current position.</summary>
    /// <param name="stream">A readable stream.</param>
    /// <param name="budget">The most bytes that may be read (or skipped, when the stream can't seek).</param>
    /// <returns>The reader.</returns>
    public static HeaderReader FromStream(Stream stream, int budget) => new(ReadOnlyMemory<byte>.Empty, stream, budget);

    /// <summary>
    /// Copies up to <paramref name="destination"/>.Length bytes at <see cref="Position"/> without consuming them. Fewer
    /// are copied only at the end of the data. Use at most <see cref="ImageFormats.MaxSignatureLength"/> bytes.
    /// </summary>
    /// <param name="destination">Where to copy.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The number of bytes copied.</returns>
    public ValueTask<int> PeekAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            // Not charged: nothing is consumed, and the bytes are charged when they are read.
            int available = (int)Math.Min(destination.Length, _memory.Length - Position);
            _memory.Span.Slice((int)Position, available).CopyTo(destination.Span);
            return new ValueTask<int>(available);
        }

        return PeekStreamAsync(_stream, destination, cancellationToken);
    }

    /// <summary>Reads exactly <paramref name="destination"/>.Length bytes.</summary>
    /// <param name="destination">Where to read to.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes when the bytes are read.</returns>
    /// <exception cref="ImageException">The data ends first, or the budget runs out.</exception>
    public ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            Charge(destination.Length);
            RequireMemory(destination.Length);
            _memory.Span.Slice((int)Position, destination.Length).CopyTo(destination.Span);
            Position += destination.Length;
            return ValueTask.CompletedTask;
        }

        return ReadStreamExactlyAsync(_stream, destination, cancellationToken);
    }

    /// <summary>Reads one byte.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The byte.</returns>
    /// <exception cref="ImageException">The data ends first, or the budget runs out.</exception>
    public ValueTask<byte> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            Charge(1);
            RequireMemory(1);
            byte value = _memory.Span[(int)Position];
            Position++;
            return new ValueTask<byte>(value);
        }

        if (_lookaheadStart < _lookaheadEnd)
        {
            Position++;
            return new ValueTask<byte>(_lookahead[_lookaheadStart++]);
        }

        return ReadStreamByteAsync(cancellationToken);
    }

    /// <summary>Moves forward <paramref name="count"/> bytes without looking at them.</summary>
    /// <param name="count">How many bytes to skip. Zero is allowed.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task that completes when the bytes are skipped.</returns>
    /// <exception cref="ImageException">
    /// The data ends first, the budget runs out (only when the stream can't seek), or <paramref name="count"/> is negative.
    /// </exception>
    public ValueTask SkipAsync(long count, CancellationToken cancellationToken)
    {
        if (count < 0)
        {
            throw ImageErrors.Corrupt(Format("The {0} file has a negative length."));
        }

        if (_stream is null)
        {
            RequireMemory(count);
            Position += count;
            return ValueTask.CompletedTask;
        }

        int fromLookahead = (int)Math.Min(count, _lookaheadEnd - _lookaheadStart);
        _lookaheadStart += fromLookahead;
        Position += fromLookahead;
        long rest = count - fromLookahead;
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

    private async ValueTask<int> PeekStreamAsync(Stream source, Memory<byte> destination, CancellationToken cancellationToken)
    {
        int buffered = _lookaheadEnd - _lookaheadStart;
        // Once the stream has ended, a peek is answered from what is buffered: nothing more is read or charged.
        if (buffered < destination.Length && !_endOfStream)
        {
            int wanted = destination.Length - buffered;
            Charge(wanted);
            byte[] grown = _lookahead.Length >= destination.Length ? _lookahead : new byte[destination.Length];
            _lookahead.AsSpan(_lookaheadStart, buffered).CopyTo(grown);
            int read = await source.ReadAtLeastAsync(grown.AsMemory(buffered, wanted), wanted, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            _endOfStream = read < wanted;
            _lookahead = grown;
            _lookaheadStart = 0;
            _lookaheadEnd = buffered + read;
            buffered = _lookaheadEnd;
        }

        int count = Math.Min(destination.Length, buffered);
        _lookahead.AsMemory(_lookaheadStart, count).CopyTo(destination);
        return count;
    }

    private async ValueTask ReadStreamExactlyAsync(Stream source, Memory<byte> destination, CancellationToken cancellationToken)
    {
        int fromLookahead = Math.Min(destination.Length, _lookaheadEnd - _lookaheadStart);
        _lookahead.AsMemory(_lookaheadStart, fromLookahead).CopyTo(destination);
        _lookaheadStart += fromLookahead;
        Position += fromLookahead;

        Memory<byte> rest = destination[fromLookahead..];
        if (rest.IsEmpty)
        {
            return;
        }

        Charge(rest.Length);
        int read = await source.ReadAtLeastAsync(rest, rest.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        Position += read;
        if (read < rest.Length)
        {
            throw Truncated();
        }
    }

    private async ValueTask<byte> ReadStreamByteAsync(CancellationToken cancellationToken)
    {
        _oneByte ??= new byte[1];
        await ReadExactlyAsync(_oneByte, cancellationToken).ConfigureAwait(false);
        return _oneByte[0];
    }

    private async ValueTask SkipByReadingAsync(Stream source, long count, CancellationToken cancellationToken)
    {
        Charge(count);
        _skipBuffer ??= new byte[SkipChunk];
        while (count > 0)
        {
            int read = await source.ReadAsync(_skipBuffer.AsMemory(0, (int)Math.Min(count, SkipChunk)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw Truncated();
            }

            Position += read;
            count -= read;
        }
    }

    private void Charge(long count)
    {
        if (_charged + count > _budget)
        {
            throw ImageErrors.ExceedsLimits(string.Create(
                CultureInfo.InvariantCulture,
                $"The {FormatName} file's headers are longer than the {_budget}-byte limit (ImageLimits.MaxHeaderBytes)."));
        }

        _charged += count;
    }

    private void RequireMemory(long count)
    {
        if (_memory.Length - Position < count)
        {
            throw Truncated();
        }
    }

    private ImageFormatException Truncated() => ImageErrors.Truncated(Format("The {0} file ends before its headers do."));

    private string Format(string template) => string.Format(CultureInfo.InvariantCulture, template, FormatName);
}
