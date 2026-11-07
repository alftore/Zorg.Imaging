using System;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Codecs.Webp;

/// <summary>
/// A canonical prefix (Huffman) code of VP8L (RFC 9649, section 3.7.2): codes assigned in order of length, then of
/// symbol, as in DEFLATE, and read one bit at a time with the code's first bit first.
/// </summary>
/// <remarks>
/// Codes of up to <see cref="FastBits"/> bits are found with one lookup, indexed by the next bits as they arrive (so by
/// the code's bits reversed); longer ones are walked a length at a time. A code with one symbol uses no bits. A set of
/// lengths that doesn't make exactly one complete code is refused, as libwebp refuses it.
/// </remarks>
internal sealed class Vp8lPrefixCode
{
    public const int MaxLength = 15;
    private const int FastBits = 8;

    private readonly int _single = -1;
    private readonly int[] _fast = [];
    private readonly ushort[] _counts = new ushort[MaxLength + 1];
    private readonly ushort[] _sorted = [];

    private Vp8lPrefixCode(int single) => _single = single;

    private Vp8lPrefixCode(ReadOnlySpan<int> lengths)
    {
        _sorted = new ushort[lengths.Length];
        foreach (int length in lengths)
        {
            _counts[length]++;
        }

        _counts[0] = 0;

        // Kraft: the lengths must fill the code space exactly.
        long left = 1;
        for (int length = 1; length <= MaxLength; length++)
        {
            left = (left << 1) - _counts[length];
            if (left < 0)
            {
                throw ImageErrors.Corrupt("A WebP lossless prefix code has more codes than fit in their lengths.");
            }
        }

        if (left != 0)
        {
            throw ImageErrors.Corrupt("A WebP lossless prefix code is incomplete.");
        }

        Span<int> offsets = stackalloc int[MaxLength + 2];
        for (int length = 1; length <= MaxLength; length++)
        {
            offsets[length + 1] = offsets[length] + _counts[length];
        }

        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            if (lengths[symbol] != 0)
            {
                _sorted[offsets[lengths[symbol]]++] = (ushort)symbol;
            }
        }

        // The fast table: each code of FastBits or fewer, reversed, at every index that starts with it.
        _fast = new int[1 << FastBits];
        int code = 0;
        int k = 0;
        for (int length = 1; length <= FastBits; length++)
        {
            for (int i = 0; i < _counts[length]; i++, k++, code++)
            {
                int reversed = Reverse(code, length);
                for (int j = reversed; j < _fast.Length; j += 1 << length)
                {
                    _fast[j] = (length << 16) | _sorted[k];
                }
            }

            code <<= 1;
        }
    }

    /// <summary>Reads a prefix code's description (section 3.7.2.1) for an alphabet of <paramref name="alphabetSize"/> symbols.</summary>
    public static Vp8lPrefixCode Read(Vp8lBitReader bits, int alphabetSize)
    {
        if (bits.ReadBit())
        {
            // Simple code: one or two symbols, of up to 8 bits (the first may be 1 bit).
            int count = (int)bits.Read(1) + 1;
            int first = (int)bits.Read(bits.ReadBit() ? 8 : 1);
            if (first >= alphabetSize)
            {
                throw ImageErrors.Corrupt("A WebP lossless prefix code has a symbol outside its alphabet.");
            }

            if (count == 1)
            {
                return new Vp8lPrefixCode(first);
            }

            int second = (int)bits.Read(8);
            if (second >= alphabetSize || second == first)
            {
                throw ImageErrors.Corrupt("A WebP lossless prefix code has an invalid second symbol.");
            }

            int[] two = new int[alphabetSize];
            two[first] = 1;
            two[second] = 1;
            return new Vp8lPrefixCode(two);
        }

        // Normal code: the code lengths are themselves prefix-coded, with a code-length code of 19 symbols.
        ReadOnlySpan<byte> order = [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
        int[] lengthLengths = new int[19];
        int lengthCount = (int)bits.Read(4) + 4;
        for (int i = 0; i < lengthCount; i++)
        {
            lengthLengths[order[i]] = (int)bits.Read(3);
        }

        Vp8lPrefixCode lengthCode = FromLengths(lengthLengths);
        int max = alphabetSize;
        if (bits.ReadBit())
        {
            int lengthBits = 2 + (2 * (int)bits.Read(3));
            max = 2 + (int)bits.Read(lengthBits);
            if (max > alphabetSize)
            {
                throw ImageErrors.Corrupt("A WebP lossless prefix code has more code lengths than symbols.");
            }
        }

        int[] lengths = new int[alphabetSize];
        int previous = 8;
        int symbol = 0;
        while (symbol < alphabetSize && max-- > 0)
        {
            int length = lengthCode.Decode(bits);
            if (length < 16)
            {
                lengths[symbol++] = length;
                if (length != 0)
                {
                    previous = length;
                }

                continue;
            }

            (int repeat, int value) = length switch
            {
                16 => (3 + (int)bits.Read(2), previous),
                17 => (3 + (int)bits.Read(3), 0),
                _ => (11 + (int)bits.Read(7), 0),
            };
            if (symbol + repeat > alphabetSize)
            {
                throw ImageErrors.Corrupt("A WebP lossless prefix code repeats past its alphabet.");
            }

            lengths.AsSpan(symbol, repeat).Fill(value);
            symbol += repeat;
        }

        return FromLengths(lengths);
    }

    /// <summary>The single symbol when the code has only one, or -1.</summary>
    public int Single => _single;

    public int Decode(Vp8lBitReader bits)
    {
        if (_single >= 0)
        {
            return _single;
        }

        int peek = bits.Peek16();
        int fast = _fast[peek & ((1 << FastBits) - 1)];
        if (fast != 0)
        {
            bits.Skip(fast >> 16);
            return fast & 0xFFFF;
        }

        // Longer than FastBits: walk the lengths, a bit at a time (as zlib's puff does).
        int code = 0;
        int first = 0;
        int index = 0;
        for (int length = 1; length <= MaxLength; length++)
        {
            code |= (peek >> (length - 1)) & 1;
            int count = _counts[length];
            if (code - first < count)
            {
                bits.Skip(length);
                return _sorted[index + code - first];
            }

            index += count;
            first = (first + count) << 1;
            code <<= 1;
        }

        throw ImageErrors.Corrupt("The WebP lossless data has a bit pattern that isn't a prefix code.");
    }

    private static Vp8lPrefixCode FromLengths(int[] lengths)
    {
        int used = 0;
        int last = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            if (lengths[i] > MaxLength)
            {
                throw ImageErrors.Corrupt("A WebP lossless prefix code is longer than 15 bits.");
            }

            if (lengths[i] != 0)
            {
                used++;
                last = i;
            }
        }

        return used switch
        {
            0 => throw ImageErrors.Corrupt("A WebP lossless prefix code has no symbols."),
            1 => new Vp8lPrefixCode(last),
            _ => new Vp8lPrefixCode(lengths),
        };
    }

    private static int Reverse(int code, int length)
    {
        int reversed = 0;
        for (int i = 0; i < length; i++)
        {
            reversed = (reversed << 1) | ((code >> i) & 1);
        }

        return reversed;
    }
}
