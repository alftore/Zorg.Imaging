using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Imaging.Formats;

/// <summary>
/// A read-only, forward-only stream that returns bytes already read from another stream, then the rest of that stream.
/// Returned by <see cref="ImageFormats.PeekAsync"/>. It doesn't own the inner stream.
/// </summary>
internal sealed class ReplayStream : Stream
{
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The caller owns the inner stream, as documented on PeekAsync.")]
    private readonly Stream _inner;
    private ReadOnlyMemory<byte> _prefix;

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

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (TryReadPrefix(buffer, out int copied))
        {
            return copied;
        }

        return _inner.Read(buffer);
    }

    public override int ReadByte()
    {
        if (!_prefix.IsEmpty)
        {
            byte value = _prefix.Span[0];
            _prefix = _prefix[1..];
            return value;
        }

        return _inner.ReadByte();
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
            return new ValueTask<int>(copied);
        }

        return _inner.ReadAsync(buffer, cancellationToken);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("This stream can't seek.");

    public override void SetLength(long value) => throw new NotSupportedException("This stream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("This stream is read-only.");

    private bool TryReadPrefix(Span<byte> buffer, out int copied)
    {
        copied = 0;
        if (_prefix.IsEmpty)
        {
            return false;
        }

        copied = Math.Min(buffer.Length, _prefix.Length);
        _prefix.Span[..copied].CopyTo(buffer);
        _prefix = _prefix[copied..];
        return true;
    }
}
