using System;
using System.Buffers;

namespace Zorg.Imaging.Codecs.Gif;

/// <summary>GIF's variable-length LZW (GIF89a, appendix F): codes from 3 to 12 bits, least significant bit first.</summary>
internal static class GifLzw
{
    /// <summary>The largest code: tables have 4096 entries.</summary>
    public const int MaxCode = 4095;

    /// <summary>Decodes colour indices into <paramref name="output"/>, which the image fills exactly.</summary>
    /// <param name="data">The image data, its sub-blocks joined.</param>
    /// <param name="minimumCodeSize">The LZW minimum code size from the file, 1 to 8.</param>
    /// <param name="output">Where the indices go.</param>
    /// <returns>
    /// How many indices were written. Fewer than the output holds when the data ends early or has a code that can't be
    /// right: decoding stops there, as browsers do, and the rest of the frame is left as it was.
    /// </returns>
    /// <remarks>
    /// Every string in the table has already been written to the output once: a new code is the previous code's
    /// string, where it was written, one index longer. So the table holds only where each string starts in the output
    /// and how long it is, and writing a code is one copy from earlier in the output. When the table is full, codes
    /// stay 12 bits and nothing is added until a clear code (the "deferred clear" that encoders use).
    /// </remarks>
    public static int Decode(ReadOnlySpan<byte> data, int minimumCodeSize, Span<byte> output)
    {
        Span<int> start = stackalloc int[MaxCode + 1];
        Span<int> length = stackalloc int[MaxCode + 1];

        int clear = 1 << minimumCodeSize;
        int end = clear + 1;
        int codeSize = minimumCodeSize + 1;
        int mask = (1 << codeSize) - 1;
        int next = clear + 2;
        int previous = -1;
        int previousStart = 0;
        int position = 0;
        ulong bits = 0;
        int bitCount = 0;
        int at = 0;

        while (position < output.Length)
        {
            if (bitCount < codeSize)
            {
                // Refill with whole bytes; near the end of the data, as many as there are.
                if (at + 8 <= data.Length)
                {
                    bits |= System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data[at..]) << bitCount;
                    int taken = (63 - bitCount) >> 3;
                    at += taken;
                    bitCount += taken * 8;
                }
                else
                {
                    while (bitCount < codeSize && at < data.Length)
                    {
                        bits |= (ulong)data[at++] << bitCount;
                        bitCount += 8;
                    }

                    if (bitCount < codeSize)
                    {
                        return position;
                    }
                }
            }

            int code = (int)bits & mask;
            bits >>= codeSize;
            bitCount -= codeSize;

            if (code < clear)
            {
                // A single index.
                output[position] = (byte)code;
                if (previous >= 0 && next <= MaxCode)
                {
                    start[next] = previousStart;
                    length[next++] = length[previous] + 1;
                }

                length[code] = 1;
                (previous, previousStart) = (code, position);
                position++;
            }
            else if (code == clear)
            {
                codeSize = minimumCodeSize + 1;
                mask = (1 << codeSize) - 1;
                next = clear + 2;
                previous = -1;
                continue;
            }
            else if (code == end || previous < 0)
            {
                // The end, or a string code with no table yet, which can't be decoded.
                return position;
            }
            else if (code < next)
            {
                int count = Math.Min(length[code], output.Length - position);
                output.Slice(start[code], count).CopyTo(output[position..]);
                if (next <= MaxCode)
                {
                    start[next] = previousStart;
                    length[next++] = length[previous] + 1;
                }

                (previous, previousStart) = (code, position);
                position += count;
            }
            else if (code == next && next <= MaxCode)
            {
                // The string being defined: the previous one and its own first index, written just before.
                int count = length[previous];
                start[next] = previousStart;
                length[next++] = count + 1;
                int room = output.Length - position;
                output.Slice(previousStart, Math.Min(count, room)).CopyTo(output[position..]);
                if (count < room)
                {
                    output[position + count] = output[previousStart];
                }

                (previous, previousStart) = (code, position);
                position += Math.Min(count + 1, room);
            }
            else
            {
                return position;
            }

            if (next > mask && codeSize < 12)
            {
                codeSize++;
                mask = (1 << codeSize) - 1;
            }
        }

        return position;
    }

    /// <summary>Encodes colour indices, each below 2^<paramref name="minimumCodeSize"/>, as an LZW stream.</summary>
    /// <param name="indices">The indices, row by row.</param>
    /// <param name="minimumCodeSize">The LZW minimum code size, 2 to 8.</param>
    /// <param name="output">Where the codes go, in sub-blocks ending with an empty one, as a GIF stores them.</param>
    /// <remarks>
    /// Starts with a clear code and clears again whenever the table fills, so a decoder never needs deferred clears.
    /// The table maps a string's code and the next index to the longer string's code. It is a hash table of 16384
    /// words, each the 20-bit key and the 12-bit code, so it stays in the processor's fastest cache.
    /// </remarks>
    public static void Encode(ReadOnlySpan<byte> indices, int minimumCodeSize, ArrayBufferWriter<byte> output)
    {
        const int TableBits = 14;
        const int TableMask = (1 << TableBits) - 1;
        Span<uint> table = stackalloc uint[1 << TableBits];
        table.Clear();

        int clear = 1 << minimumCodeSize;
        int end = clear + 1;
        var bits = new BitSink(output);
        bits.Start();
        int codeSize = minimumCodeSize + 1;
        int next = clear + 2;
        bits.Write(clear, codeSize);
        if (indices.IsEmpty)
        {
            bits.Write(end, codeSize);
            bits.Flush();
            return;
        }

        int prefix = indices[0];
        for (int i = 1; i < indices.Length; i++)
        {
            byte index = indices[i];
            uint key = ((uint)prefix << 8) | index;
            int slot = (int)((key * 0x9E3779B1u) >> (32 - TableBits));
            uint entry;
            while ((entry = table[slot]) != 0 && entry >> 12 != key)
            {
                slot = (slot + 1) & TableMask;
            }

            if (entry != 0)
            {
                prefix = (int)(entry & MaxCode);
                continue;
            }

            bits.Write(prefix, codeSize);
            if (next <= MaxCode)
            {
                table[slot] = (key << 12) | (uint)next;
                next++;
                if (next > 1 << codeSize && codeSize < 12)
                {
                    codeSize++;
                }
            }
            else
            {
                bits.Write(clear, codeSize);
                table.Clear();
                codeSize = minimumCodeSize + 1;
                next = clear + 2;
            }

            prefix = index;
        }

        bits.Write(prefix, codeSize);
        bits.Write(end, codeSize);
        bits.Flush();
    }

    // Codes packed least significant bit first, in sub-blocks of 255 bytes, each after its length, and the empty
    // block that ends them.
    private ref struct BitSink(ArrayBufferWriter<byte> output)
    {
        private Span<byte> _block;
        private int _used;
        private ulong _bits;
        private int _count;

        public void Write(int code, int size)
        {
            _bits |= (ulong)code << _count;
            _count += size;
            if (_count >= 32)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(_block[(1 + _used)..], (uint)_bits);
                _used += 4;
                _bits >>= 32;
                _count -= 32;
                if (_used >= 255)
                {
                    Spill();
                }
            }
        }

        public void Start() => _block = output.GetSpan(1 + 255 + 4);

        public void Flush()
        {
            while (_count > 0)
            {
                if (_used == 255)
                {
                    Spill();
                }

                _block[1 + _used++] = (byte)_bits;
                _bits >>= 8;
                _count -= 8;
            }

            if (_used > 0)
            {
                _block[0] = (byte)_used;
                output.Advance(1 + _used);
            }

            output.GetSpan(1)[0] = 0;
            output.Advance(1);
        }

        // Ends the block in hand at 255 bytes and starts the next with what was written past them. The block is
        // taken from the output with room for a length byte, 255 bytes and 4 more.
        private void Spill()
        {
            int over = _used - 255;
            _block[0] = 255;
            uint extra = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(_block[(1 + 255)..]);
            output.Advance(1 + 255);
            _block = output.GetSpan(1 + 255 + 4);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(_block[1..], extra);
            _used = over;
        }
    }
}
