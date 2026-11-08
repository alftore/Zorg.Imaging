using System;

namespace Zorg.Imaging.Codecs.Webp;

/// <summary>Writes a VP8L bit stream: least significant bit first (RFC 9649, section 3.1).</summary>
internal sealed class Vp8lBitWriter(int capacity)
{
    private byte[] _data = new byte[Math.Max(capacity, 64)];
    private ulong _buffer;
    private int _count;

    public int Length { get; private set; }

    /// <summary>Writes the low <paramref name="bits"/> bits of <paramref name="value"/>, at most 32.</summary>
    public void Write(uint value, int bits)
    {
        if (bits == 0)
        {
            return;
        }

        _buffer |= (ulong)(value & (uint)((1UL << bits) - 1)) << _count;
        _count += bits;
        if (_count >= 32)
        {
            if (Length + 8 > _data.Length)
            {
                Array.Resize(ref _data, (int)Math.Min((long)_data.Length * 2, Array.MaxLength));
            }

            _data[Length++] = (byte)_buffer;
            _data[Length++] = (byte)(_buffer >> 8);
            _data[Length++] = (byte)(_buffer >> 16);
            _data[Length++] = (byte)(_buffer >> 24);
            _buffer >>= 32;
            _count -= 32;
        }
    }

    /// <summary>Writes a prefix code: its bits are read first-bit-first, so they go out reversed.</summary>
    public void WriteCode(int code, int length) => Write(Reverse(code, length), length);

    /// <summary>Writes out the last partial byte, padded with zeros, and returns the stream.</summary>
    public ReadOnlySpan<byte> Finish()
    {
        while (_count > 0)
        {
            if (Length + 1 > _data.Length)
            {
                Array.Resize(ref _data, _data.Length + 8);
            }

            _data[Length++] = (byte)_buffer;
            _buffer >>= 8;
            _count -= Math.Min(8, _count);
        }

        return _data.AsSpan(0, Length);
    }

    private static uint Reverse(int code, int length)
    {
        uint reversed = 0;
        for (int i = 0; i < length; i++)
        {
            reversed = (reversed << 1) | (uint)((code >> i) & 1);
        }

        return reversed;
    }
}
