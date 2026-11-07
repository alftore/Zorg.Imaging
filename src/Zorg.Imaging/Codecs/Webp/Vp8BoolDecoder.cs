using System;
using System.Numerics;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Codecs.Webp;

/// <summary>The boolean entropy decoder of VP8 (RFC 6386, section 7.3).</summary>
/// <remarks>
/// The same arithmetic as the RFC's byte-at-a-time decoder, with the renormalising shifts done at once. A correct
/// partition is never read past its end; a few zero bytes past it are allowed (the decoder looks two bytes ahead), and
/// more throw a truncated <see cref="ImageFormatException"/>.
/// </remarks>
internal sealed class Vp8BoolDecoder
{
    private readonly byte[] _data;
    private readonly int _end;
    private int _position;
    private uint _value;
    private uint _range = 255;
    private int _bitCount;

    public Vp8BoolDecoder(byte[] data, int start, int length)
    {
        _data = data;
        _position = start;
        _end = start + length;
        _value = (uint)((NextByte() << 8) | NextByte());
    }

    public bool ReadBool(int probability)
    {
        uint split = 1 + (((_range - 1) * (uint)probability) >> 8);
        uint bigSplit = split << 8;
        bool bit;
        if (_value >= bigSplit)
        {
            bit = true;
            _range -= split;
            _value -= bigSplit;
        }
        else
        {
            bit = false;
            _range = split;
        }

        if (_range < 128)
        {
            int shift = BitOperations.LeadingZeroCount(_range) - 24;
            _range <<= shift;
            _value <<= shift;
            _bitCount += shift;
            if (_bitCount >= 8)
            {
                _bitCount -= 8;
                _value |= (uint)NextByte() << _bitCount;
            }
        }

        return bit;
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

    private int NextByte()
    {
        if (_position < _end)
        {
            return _data[_position++];
        }

        if (++_position > _end + 2)
        {
            throw ImageErrors.Truncated("The WebP lossy data ends early.");
        }

        return 0;
    }
}
