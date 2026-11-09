using System;

namespace Zorg.Imaging.Codecs.Tiff;

/// <summary>
/// TIFF's LZW (TIFF 6.0, section 13): codes of 9 to 12 bits, most significant bit first, 256 to clear and 257 to end,
/// the code width growing one code early as every writer since libtiff does.
/// </summary>
/// <remarks>
/// Each table entry is its previous entry and one more byte, so a string is written back to front from its length;
/// the output stops at the end of the target, and a code that can't be right ends the chunk, leaving the rest as it
/// was.
/// </remarks>
internal static class TiffLzw
{
    private const int Clear = 256;
    private const int End = 257;

    public static void Decode(ReadOnlySpan<byte> source, Span<byte> target)
    {
        // The old, least-significant-bit-first variant starts with a clear code read the other way round.
        if (source.Length >= 2 && source[0] == 0x00 && (source[1] & 0x01) != 0)
        {
            return;
        }

        short[] prefix = new short[4096];
        byte[] suffix = new byte[4096];
        byte[] first = new byte[4096];
        short[] length = new short[4096];
        for (int i = 0; i < 256; i++)
        {
            prefix[i] = -1;
            suffix[i] = (byte)i;
            first[i] = (byte)i;
            length[i] = 1;
        }

        int next = 258;
        int width = 9;
        int previous = -1;
        int written = 0;
        int bit = 0;
        long bits = (long)source.Length * 8;
        while (written < target.Length && bit + width <= bits)
        {
            int code = 0;
            for (int k = 0; k < width; k++, bit++)
            {
                code = (code << 1) | ((source[bit >> 3] >> (7 - (bit & 7))) & 1);
            }

            if (code == End)
            {
                return;
            }

            if (code == Clear)
            {
                next = 258;
                width = 9;
                previous = -1;
                continue;
            }

            int entry;
            if (code < next)
            {
                entry = code;
                if (previous >= 0 && next < 4096)
                {
                    Add(previous, first[code]);
                }
            }
            else if (code == next && previous >= 0 && next < 4096)
            {
                Add(previous, first[previous]);
                entry = code;
            }
            else
            {
                return;
            }

            int count = length[entry];
            int end = written + count;
            for (int at = entry, k = end - 1; at >= 0; at = prefix[at], k--)
            {
                if (k < target.Length)
                {
                    target[k] = suffix[at];
                }
            }

            written = Math.Min(end, target.Length);
            previous = entry;

            // The early change: a code wider by a bit from the one that fills the current width, less one.
            if (next + 1 >= (1 << width) && width < 12)
            {
                width++;
            }
        }

        void Add(int from, byte value)
        {
            prefix[next] = (short)from;
            suffix[next] = value;
            first[next] = first[from];
            length[next] = (short)Math.Min(length[from] + 1, short.MaxValue);
            next++;
        }
    }
}
