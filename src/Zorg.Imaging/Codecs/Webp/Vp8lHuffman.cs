using System;
using System.Collections.Generic;

namespace Zorg.Imaging.Codecs.Webp;

/// <summary>
/// A prefix code for writing VP8L (RFC 9649, section 3.7.2): lengths limited to 15 bits, canonical codes stored bit
/// reversed (the stream is read first bit first), and the code written into the stream in its simple or normal form.
/// </summary>
internal sealed class Vp8lHuffman
{
    // Section 3.7.2.1.2: the order in which the code-length code's own lengths are written.
    private static ReadOnlySpan<byte> CodeLengthOrder => [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    private readonly byte[] _lengths;
    private readonly ushort[] _codes;

    private Vp8lHuffman(byte[] lengths)
    {
        _lengths = lengths;
        _codes = new ushort[lengths.Length];
        int used = 0;
        foreach (byte length in lengths)
        {
            used += length == 0 ? 0 : 1;
        }

        // A code with one symbol takes no bits in the stream; nothing needs a code then.
        if (used <= 1)
        {
            Array.Clear(_lengths);
            return;
        }

        Span<int> counts = stackalloc int[16];
        foreach (byte length in lengths)
        {
            counts[length]++;
        }

        counts[0] = 0;
        Span<int> next = stackalloc int[16];
        int code = 0;
        for (int length = 1; length < 16; length++)
        {
            code = (code + counts[length - 1]) << 1;
            next[length] = code;
        }

        for (int symbol = 0; symbol < lengths.Length; symbol++)
        {
            int length = lengths[symbol];
            if (length != 0)
            {
                _codes[symbol] = (ushort)Reverse(next[length]++, length);
            }
        }
    }

    /// <summary>Writes one symbol.</summary>
    public void Write(Vp8lBitWriter bits, int symbol) => bits.Write(_codes[symbol], _lengths[symbol]);

    /// <summary>How many bits a symbol takes.</summary>
    public int Length(int symbol) => _lengths[symbol];

    /// <summary>Builds the best code of at most 15 bits for <paramref name="counts"/>, writes it, and returns it.</summary>
    public static Vp8lHuffman WriteCode(Vp8lBitWriter bits, ReadOnlySpan<int> counts)
    {
        Span<int> used = stackalloc int[3];
        int usedCount = 0;
        for (int symbol = 0; symbol < counts.Length && usedCount < 3; symbol++)
        {
            if (counts[symbol] != 0)
            {
                used[usedCount++] = symbol;
            }
        }

        byte[] lengths = new byte[counts.Length];

        // Section 3.7.2.1.1: one or two symbols below 256 have a short form. No symbols is coded as symbol 0 alone.
        if (usedCount <= 2 && (usedCount == 0 || used[usedCount - 1] < 256))
        {
            int first = usedCount == 0 ? 0 : used[0];
            bits.Write(1, 1);
            bits.Write((uint)Math.Max(usedCount - 1, 0), 1);
            if (first < 2)
            {
                bits.Write(0, 1);
                bits.Write((uint)first, 1);
            }
            else
            {
                bits.Write(1, 1);
                bits.Write((uint)first, 8);
            }

            lengths[first] = 1;
            if (usedCount == 2)
            {
                bits.Write((uint)used[1], 8);
                lengths[used[1]] = 1;
            }

            return new Vp8lHuffman(lengths);
        }

        Lengths(counts, 15, lengths);
        if (usedCount == 1)
        {
            lengths[used[0]] = 1;
        }

        bits.Write(0, 1);
        WriteLengths(bits, lengths);
        return new Vp8lHuffman(lengths);
    }

    /// <summary>
    /// Code lengths of at most <paramref name="limit"/> bits for <paramref name="counts"/>: a Huffman code, with the
    /// deepest levels folded up as JPEG's Annex K.3 folds them when it is too deep. Unused symbols get 0.
    /// </summary>
    public static void Lengths(ReadOnlySpan<int> counts, int limit, Span<byte> lengths)
    {
        lengths.Clear();
        var symbols = new List<int>();
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] != 0)
            {
                symbols.Add(i);
            }
        }

        if (symbols.Count == 0)
        {
            return;
        }

        if (symbols.Count == 1)
        {
            lengths[symbols[0]] = 1;
            return;
        }

        // Huffman's tree, leaves first in the node arrays; ties broken by node index, so the result is the same each run.
        int n = symbols.Count;
        long[] weight = new long[(2 * n) - 1];
        int[] parent = new int[(2 * n) - 1];
        var queue = new PriorityQueue<int, (long Weight, int Index)>(n);
        for (int i = 0; i < n; i++)
        {
            weight[i] = counts[symbols[i]];
            queue.Enqueue(i, (weight[i], i));
        }

        int next = n;
        while (queue.Count > 1)
        {
            int a = queue.Dequeue();
            int b = queue.Dequeue();
            weight[next] = weight[a] + weight[b];
            parent[a] = next;
            parent[b] = next;
            queue.Enqueue(next, (weight[next], next));
            next++;
        }

        // Depths, from the root down (parents always come after their children).
        int root = next - 1;
        int[] depth = new int[next];
        int[] perLength = new int[Math.Max(limit, n) + 1];
        for (int node = root - 1; node >= 0; node--)
        {
            depth[node] = depth[parent[node]] + 1;
        }

        int deepest = 0;
        for (int i = 0; i < n; i++)
        {
            deepest = Math.Max(deepest, depth[i]);
        }

        if (deepest >= perLength.Length)
        {
            Array.Resize(ref perLength, deepest + 1);
        }

        for (int i = 0; i < n; i++)
        {
            perLength[depth[i]]++;
        }

        // Annex K.3's adjustment: take two leaves from the deepest level, put one a level up and pair the other with a
        // leaf moved down from the deepest shorter level that has one. The code stays complete.
        for (int length = deepest; length > limit; length--)
        {
            while (perLength[length] > 0)
            {
                int j = length - 2;
                while (perLength[j] == 0)
                {
                    j--;
                }

                perLength[length] -= 2;
                perLength[length - 1]++;
                perLength[j + 1] += 2;
                perLength[j]--;
            }
        }

        // The most frequent symbols take the shortest lengths.
        long[] order = new long[n];
        for (int i = 0; i < n; i++)
        {
            order[i] = ((long)(int.MaxValue - counts[symbols[i]]) << 16) | (uint)symbols[i];
        }

        Array.Sort(order);
        int k = 0;
        for (int length = 1; length <= limit; length++)
        {
            for (int i = 0; i < perLength[length]; i++)
            {
                lengths[(int)(order[k++] & 0xFFFF)] = (byte)length;
            }
        }
    }

    // Section 3.7.2.1.2: the lengths, run-length coded with 16 (repeat the previous length), 17 and 18 (runs of zeros),
    // then coded with a prefix code of their own of at most 7 bits.
    private static void WriteLengths(Vp8lBitWriter bits, ReadOnlySpan<byte> lengths)
    {
        var tokens = new List<(int Symbol, int Extra)>(lengths.Length);
        for (int i = 0; i < lengths.Length;)
        {
            int value = lengths[i];
            int run = 1;
            while (i + run < lengths.Length && lengths[i + run] == value)
            {
                run++;
            }

            i += run;
            if (value == 0)
            {
                while (run >= 11)
                {
                    int take = Math.Min(run, 138);
                    tokens.Add((18, take - 11));
                    run -= take;
                }

                if (run >= 3)
                {
                    tokens.Add((17, run - 3));
                    run = 0;
                }

                for (; run > 0; run--)
                {
                    tokens.Add((0, 0));
                }

                continue;
            }

            // The value once, then repeats of it in 3 to 6.
            tokens.Add((value, 0));
            run--;
            while (run >= 3)
            {
                int take = Math.Min(run, 6);
                tokens.Add((16, take - 3));
                run -= take;
            }

            for (; run > 0; run--)
            {
                tokens.Add((value, 0));
            }
        }

        Span<int> counts = stackalloc int[19];
        counts.Clear();
        foreach ((int symbol, _) in tokens)
        {
            counts[symbol]++;
        }

        byte[] codeLengths = new byte[19];
        Lengths(counts, 7, codeLengths);
        int written = 19;
        while (written > 4 && codeLengths[CodeLengthOrder[written - 1]] == 0)
        {
            written--;
        }

        bits.Write((uint)(written - 4), 4);
        for (int i = 0; i < written; i++)
        {
            bits.Write(codeLengths[CodeLengthOrder[i]], 3);
        }

        // Every symbol's length is written, so no maximum symbol count.
        bits.Write(0, 1);
        var code = new Vp8lHuffman(codeLengths);
        foreach ((int symbol, int extra) in tokens)
        {
            code.Write(bits, symbol);
            switch (symbol)
            {
                case 16:
                    bits.Write((uint)extra, 2);
                    break;
                case 17:
                    bits.Write((uint)extra, 3);
                    break;
                case 18:
                    bits.Write((uint)extra, 7);
                    break;
            }
        }
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
