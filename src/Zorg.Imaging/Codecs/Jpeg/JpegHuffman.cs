using System;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Codecs.Jpeg;

/// <summary>One Huffman table, built from a DHT segment's code counts and values (ITU-T T.81, Annex C and F.2.2.3).</summary>
/// <remarks>
/// Codes of up to <see cref="FastBits"/> bits are decoded with one table lookup; longer ones by comparing against the
/// largest code of each length, as in T.81's DECODE procedure.
/// </remarks>
internal sealed class JpegHuffman
{
    /// <summary>Codes this long or shorter are found with one lookup.</summary>
    public const int FastBits = 9;

    private readonly byte[] _values;

    // For each code length 1 to 16: the largest code (or -1 for none), and what to add to a code to index _values.
    private readonly int[] _maxCode = new int[17];
    private readonly int[] _valueOffset = new int[17];

    // Indexed by the next FastBits bits: the code length in the high byte and the value in the low; 0 when longer.
    private readonly ushort[] _fast = new ushort[1 << FastBits];

    private JpegHuffman(byte[] values) => _values = values;

    /// <summary>
    /// For AC tables, indexed by the next <see cref="FastBits"/> bits: when a code and the value bits after it both fit,
    /// the value in the top 16 bits, the zero run in bits 8 to 11 and the bits used in the low byte; otherwise 0. One
    /// lookup then decodes most coefficients, as libjpeg-turbo's decoder does.
    /// </summary>
    public int[] FastAc { get; } = new int[1 << FastBits];

    /// <summary>Builds a table.</summary>
    /// <param name="counts">How many codes there are of each length, 1 to 16.</param>
    /// <param name="values">The values, in code order.</param>
    /// <returns>The table.</returns>
    /// <exception cref="ImageFormatException">The counts describe more codes than fit in their lengths.</exception>
    public static JpegHuffman Create(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> values)
    {
        var table = new JpegHuffman(values.ToArray());
        int code = 0;
        int k = 0;
        for (int length = 1; length <= 16; length++)
        {
            int count = counts[length - 1];
            table._valueOffset[length] = k - code;
            for (int i = 0; i < count; i++, k++, code++)
            {
                if (length <= FastBits)
                {
                    int shift = FastBits - length;
                    ushort entry = (ushort)((length << 8) | values[k]);
                    table._fast.AsSpan(code << shift, 1 << shift).Fill(entry);
                }
            }

            table._maxCode[length] = count > 0 ? code - 1 : -1;
            if (code > (1 << length))
            {
                throw ImageErrors.Corrupt("A JPEG Huffman table has more codes than fit in their lengths.");
            }

            code <<= 1;
        }

        table.BuildFastAc();
        return table;
    }

    private void BuildFastAc()
    {
        for (int prefix = 0; prefix < _fast.Length; prefix++)
        {
            ushort entry = _fast[prefix];
            if (entry == 0)
            {
                continue;
            }

            int length = entry >> 8;
            int run = (entry >> 4) & 15;
            int size = entry & 15;
            if (size == 0 || length + size > FastBits)
            {
                continue;
            }

            int bits = (prefix >> (FastBits - length - size)) & ((1 << size) - 1);
            int value = bits < (1 << (size - 1)) ? bits - (1 << size) + 1 : bits;
            FastAc[prefix] = (value << 16) | (run << 8) | (length + size);
        }
    }

    /// <summary>Decodes one value.</summary>
    /// <param name="bits">The bit reader.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ImageFormatException">The bits aren't a code in this table.</exception>
    public int Decode(JpegBitReader bits)
    {
        int peek = bits.Peek16();
        ushort fast = _fast[peek >> (16 - FastBits)];
        if (fast != 0)
        {
            bits.Skip(fast >> 8);
            return fast & 0xFF;
        }

        for (int length = FastBits + 1; length <= 16; length++)
        {
            int code = peek >> (16 - length);
            if (code <= _maxCode[length])
            {
                bits.Skip(length);
                return _values[code + _valueOffset[length]];
            }
        }

        throw ImageErrors.Corrupt("The JPEG image data has a bit pattern that isn't a Huffman code.");
    }
}
