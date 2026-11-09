using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Zorg.Imaging.Codecs.Webp;

/// <summary>
/// The boolean entropy encoder of VP8 (RFC 6386, section 7): each bool is coded with an 8-bit probability that it is
/// 0, into the interval [bottom, bottom + range). A carry out of the 32-bit low end ripples back through the bytes
/// already written.
/// </summary>
internal sealed class Vp8BoolEncoder(int capacity)
{
    private byte[] _data = new byte[Math.Max(capacity, 64)];
    private uint _range = 255;
    private uint _bottom;
    private int _bitCount = 24;

    public int Length { get; private set; }

    /// <summary>Writes one bool that is 0 with probability <paramref name="probability"/>/256.</summary>
    public void WriteBool(int probability, bool value)
    {
        uint split = 1 + (((_range - 1) * (uint)probability) >> 8);
        if (value)
        {
            _bottom += split;
            _range -= split;
        }
        else
        {
            _range = split;
        }

        // Doubled until it is at least 128. Mostly that stays within the current byte; crossing a byte boundary is
        // left to Shift, out of line, so this stays small where it is inlined into the token walk.
        int shift = BitOperations.LeadingZeroCount(_range) - 24;
        if (shift <= 0)
        {
            return;
        }

        _range <<= shift;
        if (shift < _bitCount)
        {
            if ((_bottom >> (32 - shift)) != 0)
            {
                Carry();
            }

            _bottom <<= shift;
            _bitCount -= shift;
            return;
        }

        Shift(shift);
    }

    // Shifts bottom by shift bits, in as few steps as there are byte boundaries to cross: a carry out of the bits
    // shifted past the top ripples into the bytes written, and each finished byte is written.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Shift(int shift)
    {
        while (shift > 0)
        {
            int step = Math.Min(shift, _bitCount);
            if ((_bottom >> (32 - step)) != 0)
            {
                Carry();
            }

            _bottom <<= step;
            shift -= step;
            _bitCount -= step;
            if (_bitCount == 0)
            {
                Put((byte)(_bottom >> 24));
                _bottom &= (1u << 24) - 1;
                _bitCount = 8;
            }
        }
    }

    /// <summary>Writes a bool with even odds.</summary>
    public void WriteFlag(bool value) => WriteBool(128, value);

    /// <summary>Writes an unsigned value of <paramref name="bits"/> bits, most significant first, at even odds.</summary>
    public void WriteLiteral(int value, int bits)
    {
        for (int bit = bits - 1; bit >= 0; bit--)
        {
            WriteFlag(((value >> bit) & 1) != 0);
        }
    }

    /// <summary>Writes a magnitude of <paramref name="bits"/> bits then a sign, as the frame header's signed fields.</summary>
    public void WriteSigned(int value, int bits)
    {
        WriteLiteral(Math.Abs(value), bits);
        WriteFlag(value < 0);
    }

    /// <summary>Writes a flag, then the signed value when it isn't 0.</summary>
    public void WriteOptionalSigned(int value, int bits)
    {
        WriteFlag(value != 0);
        if (value != 0)
        {
            WriteSigned(value, bits);
        }
    }

    /// <summary>Pads out the last bits and returns the data.</summary>
    public ReadOnlySpan<byte> Finish()
    {
        // Enough zeros at even odds to push every pending bit of `bottom` out.
        for (int i = 0; i < 32; i++)
        {
            WriteFlag(false);
        }

        return _data.AsSpan(0, Length);
    }

    private void Carry()
    {
        int at = Length - 1;
        while (at >= 0 && _data[at] == 0xFF)
        {
            _data[at--] = 0;
        }

        if (at >= 0)
        {
            _data[at]++;
        }
    }

    private void Put(byte value)
    {
        if (Length == _data.Length)
        {
            Array.Resize(ref _data, _data.Length * 2);
        }

        _data[Length++] = value;
    }
}
