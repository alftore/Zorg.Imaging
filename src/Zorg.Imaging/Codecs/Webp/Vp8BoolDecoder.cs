using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Codecs.Webp;

/// <summary>The boolean entropy decoder of VP8 (RFC 6386, section 7.3).</summary>
/// <remarks>
/// <para>
/// The RFC's arithmetic with a 64-bit window: the bytes ahead are loaded seven at a time, and the comparison uses the
/// window's top bits at <c>_bits</c>. The range is kept as range - 1, which makes the split <c>(range - 1) * p / 256</c>
/// and the test <c>value &gt; split</c> the RFC's <c>value &gt;= 1 + (range - 1) * p / 256</c> exactly; the shift that
/// renormalises comes from the range's highest set bit instead of a loop.
/// </para>
/// <para>
/// A correct partition is never read past its end. One byte of zeros past it is allowed, as libwebp allows it; needing
/// more throws a truncated <see cref="ImageFormatException"/>.
/// </para>
/// </remarks>
internal sealed class Vp8BoolDecoder
{
    private readonly byte[] _data;
    private readonly int _end;
    private int _position;
    private ulong _value;
    private int _bits = -8;
    private uint _range = 255 - 1;
    private bool _pastEnd;

    public Vp8BoolDecoder(byte[] data, int start, int length)
    {
        _data = data;
        _position = start;
        _end = start + length;
        Load();
    }

    /// <summary>
    /// The decoder's state, for a hot loop to copy into locals (where the JIT keeps it in registers), read with
    /// <see cref="Read"/>, and give back with <see cref="Store"/>.
    /// </summary>
    public (ulong Value, int Bits, uint Range) State => (_value, _bits, _range);

    public void Store(ulong value, int bits, uint range)
    {
        _value = value;
        _bits = bits;
        _range = range;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ReadBool(int probability)
    {
        ulong value = _value;
        int bits = _bits;
        uint range = _range;
        bool bit = Read(ref value, ref bits, ref range, probability);
        _value = value;
        _bits = bits;
        _range = range;
        return bit;
    }

    /// <summary><see cref="ReadBool"/> on a hot loop's copy of <see cref="State"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Read(ref ulong value, ref int bits, ref uint range, int probability)
    {
        if (bits < 0)
        {
            (value, bits) = Refill(value, bits);
        }

        int position = bits;
        uint split = (range * (uint)probability) >> 8;
        bool bit = (uint)(value >> position) > split;
        uint next;
        if (bit)
        {
            next = range - split;
            value -= (ulong)(split + 1) << position;
        }
        else
        {
            next = split + 1;
        }

        int shift = 7 ^ BitOperations.Log2(next);
        range = (next << shift) - 1;
        bits = position - shift;
        return bit;
    }

    // The bytes ahead, on a hot loop's locals: kept out of line, as it runs once for every seven bytes.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private (ulong Value, int Bits) Refill(ulong value, int bits)
    {
        _value = value;
        _bits = bits;
        Load();
        return (_value, _bits);
    }

    public bool ReadFlag() => ReadBool(128);

    /// <summary>An unsigned value of <paramref name="bits"/> bits, most significant first, each at probability 1/2.</summary>
    public int ReadLiteral(int bits)
    {
        int value = 0;
        while (bits-- > 0)
        {
            value = (value << 1) | (ReadBool(128) ? 1 : 0);
        }

        return value;
    }

    /// <summary>A magnitude of <paramref name="bits"/> bits then a sign bit, as the frame header codes deltas.</summary>
    public int ReadSigned(int bits)
    {
        int magnitude = ReadLiteral(bits);
        return ReadFlag() ? -magnitude : magnitude;
    }

    /// <summary>A flag, and when set a signed value: an optional header field.</summary>
    public int ReadOptionalSigned(int bits) => ReadFlag() ? ReadSigned(bits) : 0;

    /// <summary>Reads a value with a tree and its probabilities (section 8.1).</summary>
    public int ReadTree(ReadOnlySpan<sbyte> tree, ReadOnlySpan<byte> probabilities)
    {
        int i = 0;
        while ((i = tree[i + (ReadBool(probabilities[i >> 1]) ? 1 : 0)]) > 0)
        {
        }

        return -i;
    }

    // Seven bytes at once while they last, then one at a time, then zeros once.
    private void Load()
    {
        if (_position + 8 <= _end)
        {
            ulong next = BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(_position, 8)) >> 8;
            _position += 7;
            _value = (_value << 56) | next;
            _bits += 56;
        }
        else if (_position < _end)
        {
            _value = (_value << 8) | _data[_position++];
            _bits += 8;
        }
        else if (!_pastEnd)
        {
            _value <<= 8;
            _bits += 8;
            _pastEnd = true;
        }
        else
        {
            throw ImageErrors.Truncated("The WebP lossy data ends early.");
        }
    }
}
