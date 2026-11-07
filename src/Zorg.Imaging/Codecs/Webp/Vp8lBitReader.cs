using System;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Codecs.Webp;

/// <summary>Reads a VP8L bit stream: least significant bit first (RFC 9649, section 3.1).</summary>
/// <remarks>
/// Up to eight bytes past the end read as zeros, which a correct stream never uses; using them throws a truncated
/// <see cref="ImageFormatException"/>.
/// </remarks>
internal sealed class Vp8lBitReader(ReadOnlyMemory<byte> data)
{
    private readonly ReadOnlyMemory<byte> _data = data;
    private int _position;
    private ulong _buffer;
    private int _count;

    /// <summary>Reads 0 to 32 bits as an unsigned number.</summary>
    public uint Read(int bits)
    {
        if (bits == 0)
        {
            return 0;
        }

        if (_count < bits)
        {
            Fill();
        }

        uint value = (uint)(_buffer & ((1UL << bits) - 1));
        Skip(bits);
        return value;
    }

    public bool ReadBit() => Read(1) != 0;

    /// <summary>Looks at the next 16 bits, least significant first.</summary>
    public int Peek16()
    {
        if (_count < 16)
        {
            Fill();
        }

        return (int)(_buffer & 0xFFFF);
    }

    public void Skip(int bits)
    {
        _buffer >>= bits;
        _count -= bits;
        if (_position > _data.Length && ((long)_position * 8) - _count > (long)_data.Length * 8)
        {
            throw ImageErrors.Truncated("The WebP lossless data ends early.");
        }
    }

    private void Fill()
    {
        ReadOnlySpan<byte> data = _data.Span;
        while (_count <= 56)
        {
            ulong value = 0;
            if (_position < data.Length)
            {
                value = data[_position];
            }
            else if (_position > data.Length + 8)
            {
                throw ImageErrors.Truncated("The WebP lossless data ends early.");
            }

            _position++;
            _buffer |= value << _count;
            _count += 8;
        }
    }
}
