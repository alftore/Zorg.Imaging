using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Imaging.Formats;

/// <summary>
/// A read-only, forward-only stream that returns bytes already read from another stream, then the rest of that stream.
/// Returned by <see cref="ImageFormats.PeekAsync"/>. It doesn't own the inner stream.
/// </summary>
/// <remarks>
/// The library's own readers look at it without using it up: <see cref="Record"/> keeps every byte handed out until the
/// returned scope ends, then puts them back in front. A caller can therefore peek, identify and decode the same stream
/// in turn. The recording holds only what the reader read, which <see cref="ImageLimits.MaxHeaderBytes"/> bounds.
/// </remarks>
internal sealed class ReplayStream : Stream
{
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The caller owns the inner stream, as documented on PeekAsync.")]
    private readonly Stream _inner;
    private ReadOnlyMemory<byte> _prefix;
    private ArrayBufferWriter<byte>? _recording;

    public ReplayStream(ReadOnlyMemory<byte> prefix, Stream inner)
    {
        _prefix = prefix;
        _inner = inner;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException("This stream can't seek.");

    public override long Position
    {
        get => throw new NotSupportedException("This stream can't seek.");
        set => throw new NotSupportedException("This stream can't seek.");
    }

    /// <summary>The bytes waiting to be replayed, for tests.</summary>
    internal int PendingLength => _prefix.Length;

    /// <summary>
    /// Starts keeping every byte read. Disposing the scope puts them back, so the next read returns them again; it must
    /// be disposed on every path, including failure and cancellation.
    /// </summary>
    /// <returns>The scope.</returns>
    /// <exception cref="InvalidOperationException">A recording is already running: the stream is in use elsewhere.</exception>
    public Rewind Record()
    {
        if (_recording is not null)
        {
            throw new InvalidOperationException("The stream is already being read by another operation.");
        }

        _recording = new ArrayBufferWriter<byte>();
        return new Rewind(this);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (!TryReadPrefix(buffer, out int read))
        {
            read = _inner.Read(buffer);
        }

        Keep(buffer[..read]);
        return read;
    }

    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 1 ? one[0] : -1;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryReadPrefix(buffer.Span, out int copied))
        {
            Keep(buffer.Span[..copied]);
            return new ValueTask<int>(copied);
        }

        if (_recording is null)
        {
            return _inner.ReadAsync(buffer, cancellationToken);
        }

        return ReadInnerAndKeepAsync(buffer, cancellationToken);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("This stream can't seek.");

    public override void SetLength(long value) => throw new NotSupportedException("This stream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("This stream is read-only.");

    private async ValueTask<int> ReadInnerAndKeepAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Keep(buffer.Span[..read]);
        return read;
    }

    private void Keep(ReadOnlySpan<byte> read) => _recording?.Write(read);

    private void PutBack()
    {
        ArrayBufferWriter<byte>? recording = _recording;
        _recording = null;
        if (recording is null || recording.WrittenCount == 0)
        {
            return;
        }

        byte[] combined = new byte[recording.WrittenCount + _prefix.Length];
        recording.WrittenSpan.CopyTo(combined);
        _prefix.Span.CopyTo(combined.AsSpan(recording.WrittenCount));
        _prefix = combined;
    }

    private bool TryReadPrefix(Span<byte> buffer, out int copied)
    {
        copied = 0;
        if (_prefix.IsEmpty)
        {
            return false;
        }

        copied = Math.Min(buffer.Length, _prefix.Length);
        _prefix.Span[..copied].CopyTo(buffer);
        // Once replayed, let go of the array so a large header doesn't stay in memory for the whole decode.
        _prefix = copied == _prefix.Length ? ReadOnlyMemory<byte>.Empty : _prefix[copied..];
        return true;
    }

    /// <summary>Puts the recorded bytes back when disposed. Returned by <see cref="Record"/>.</summary>
    internal readonly struct Rewind : IDisposable
    {
        private readonly ReplayStream _stream;

        public Rewind(ReplayStream stream) => _stream = stream;

        public void Dispose() => _stream?.PutBack();
    }
}
