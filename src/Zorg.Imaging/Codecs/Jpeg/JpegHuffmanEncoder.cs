using System;

namespace Zorg.Imaging.Codecs.Jpeg;

/// <summary>
/// A Huffman table for encoding, built to suit the symbols an image actually uses (ITU-T T.81, Annex K.2), with codes
/// of at most 16 bits.
/// </summary>
internal sealed class JpegHuffmanEncoder
{
    private readonly ushort[] _codes = new ushort[256];
    private readonly byte[] _lengths = new byte[256];

    private JpegHuffmanEncoder(byte[] counts, byte[] values)
    {
        Counts = counts;
        Values = values;

        // Canonical codes in order of length (Annex C), as the decoder rebuilds them from the same counts.
        int code = 0;
        int k = 0;
        for (int length = 1; length <= 16; length++)
        {
            for (int i = 0; i < counts[length - 1]; i++, k++, code++)
            {
                _codes[values[k]] = (ushort)code;
                _lengths[values[k]] = (byte)length;
            }

            code <<= 1;
        }
    }

    /// <summary>How many codes there are of each length, 1 to 16, as a DHT segment lists them.</summary>
    public byte[] Counts { get; }

    /// <summary>The symbols in code order.</summary>
    public byte[] Values { get; }

    public ushort Code(int symbol) => _codes[symbol];

    public int Length(int symbol) => _lengths[symbol];

    /// <summary>Builds the table that codes these symbol counts in the fewest bits.</summary>
    /// <param name="frequencies">How often each of the 256 symbols occurs; any may be 0.</param>
    /// <returns>The table.</returns>
    public static JpegHuffmanEncoder Optimal(ReadOnlySpan<long> frequencies)
    {
        // Figure K.1: Huffman's merging, with one extra symbol (256) of frequency 1 so no real code is all ones.
        long[] freq = new long[257];
        frequencies[..256].CopyTo(freq);
        freq[256] = 1;
        int[] size = new int[257];
        int[] others = new int[257];
        Array.Fill(others, -1);
        while (true)
        {
            int v1 = -1;
            int v2 = -1;
            for (int i = 0; i < 257; i++)
            {
                if (freq[i] == 0)
                {
                    continue;
                }

                // The least frequent, and on ties the larger symbol (K.2), so the result doesn't depend on order.
                if (v1 < 0 || freq[i] <= freq[v1])
                {
                    v2 = v1;
                    v1 = i;
                }
                else if (v2 < 0 || freq[i] <= freq[v2])
                {
                    v2 = i;
                }
            }

            if (v2 < 0)
            {
                break;
            }

            freq[v1] += freq[v2];
            freq[v2] = 0;
            size[v1]++;
            while (others[v1] >= 0)
            {
                v1 = others[v1];
                size[v1]++;
            }

            others[v1] = v2;
            size[v2]++;
            while (others[v2] >= 0)
            {
                v2 = others[v2];
                size[v2]++;
            }
        }

        // Figure K.2 and K.3: count codes of each length, then move codes longer than 16 bits up the tree.
        int[] bits = new int[33];
        for (int i = 0; i < 257; i++)
        {
            if (size[i] > 0)
            {
                bits[Math.Min(size[i], 32)]++;
            }
        }

        for (int i = 32; i > 16; i--)
        {
            while (bits[i] > 0)
            {
                int j = i - 2;
                while (bits[j] == 0)
                {
                    j--;
                }

                bits[i] -= 2;
                bits[i - 1]++;
                bits[j + 1] += 2;
                bits[j]--;
            }
        }

        // Remove the reserved symbol's code, the longest one.
        int longest = 16;
        while (bits[longest] == 0)
        {
            longest--;
        }

        bits[longest]--;

        // Figure K.4: symbols in order of their merged depth, then of value; the adjusted counts above then give each
        // its final length in that order. Depths past 32 (possible only for vast images) are counted as 32 above.
        byte[] counts = new byte[16];
        for (int i = 1; i <= 16; i++)
        {
            counts[i - 1] = (byte)bits[i];
        }

        int total = 0;
        foreach (byte count in counts)
        {
            total += count;
        }

        byte[] values = new byte[total];
        int at = 0;
        for (int length = 1; length <= 256 && at < total; length++)
        {
            for (int symbol = 0; symbol < 256 && at < total; symbol++)
            {
                if (size[symbol] == length)
                {
                    values[at++] = (byte)symbol;
                }
            }
        }

        return new JpegHuffmanEncoder(counts, values);
    }
}
