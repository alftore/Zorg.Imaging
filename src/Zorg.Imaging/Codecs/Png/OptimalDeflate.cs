using System;
using System.Collections.Generic;
using System.Numerics;

namespace Zorg.Imaging.Codecs.Png;

/// <summary>
/// A deflate encoder (RFC 1951) that spends time for size, in the manner of Zopfli: every match length at every
/// position is weighed by its cost in bits, and the costs come from the Huffman codes the previous attempt would have
/// used, over several attempts. Slow; for <see cref="PngEncoderOptions.Optimize"/>.
/// </summary>
/// <remarks>
/// <para>
/// The input is cut into blocks of 128 KiB. For each, the matches at every position are found once (hash chains, up to
/// <see cref="MaxChain"/> candidates, keeping for each length the nearest distance) and cached as at most eight
/// (length, distance) steps; dropping a step only means using a farther distance that also matches. A forward dynamic
/// programme then finds the cheapest way through the block, literals and matches, for the current costs. The first
/// costs are the fixed Huffman code's; each later attempt uses the code lengths the last parse's symbols give. The
/// cheapest attempt is written as one dynamic-Huffman block.
/// </para>
/// <para>
/// A band ends as zlib's sync flush ends it (an empty stored block, so the next band starts on a byte), or with the
/// final block when it is the last. The output is the same on every run and on any number of cores.
/// </para>
/// </remarks>
internal static class OptimalDeflate
{
    private const int Window = 32768;
    private const int MinMatch = 3;
    private const int MaxMatch = 258;
    private const int MaxChain = 1024;
    private const int BlockBytes = 128 * 1024;
    private const int Attempts = 7;
    private const int Steps = 8;
    private const int HashBits = 15;

    // RFC 1951, 3.2.5: each length code's base and extra bits, and each distance code's.
    private static readonly ushort[] LengthBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly byte[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly ushort[] DistanceBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly byte[] DistanceExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];

    // The order the code-length code's lengths are sent in (3.2.7).
    private static readonly byte[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    private static readonly byte[] LengthSymbol = BuildLengthSymbols();

    /// <summary>Compresses <paramref name="data"/> as raw deflate blocks, ended as a sync flush or, when last, finally.</summary>
    public static (byte[] Bytes, int Length) Compress(ReadOnlySpan<byte> data, bool last)
    {
        var writer = new BitWriter((data.Length / 2) + 1024);
        var finder = new MatchFinder(data);
        for (int start = 0; start < data.Length || start == 0; start += BlockBytes)
        {
            int end = Math.Min(start + BlockBytes, data.Length);
            List<Token> tokens = Parse(data, start, end, finder);
            List<(int From, int To)> blocks = Split(tokens);
            for (int b = 0; b < blocks.Count; b++)
            {
                (int from, int to) = blocks[b];
                WriteBlock(writer, tokens.GetRange(from, to - from), final: last && end == data.Length && b == blocks.Count - 1);
            }
            if (data.Length == 0)
            {
                break;
            }
        }

        if (!last)
        {
            // An empty stored block: BFINAL 0, BTYPE 00, then to a byte boundary, LEN 0 and NLEN 0xFFFF.
            writer.Write(0, 3);
            writer.Align();
            writer.Write(0x0000, 16);
            writer.Write(0xFFFF, 16);
        }

        writer.Align();
        return (writer.Buffer, writer.Length);
    }

    /// <summary>A literal (length 0) or a match.</summary>
    private readonly record struct Token(ushort Length, ushort DistanceOrLiteral);

    // Each attempt's parse is costed in bits with the code its own symbols give; the cheapest is kept.
    private static List<Token> Parse(ReadOnlySpan<byte> data, int start, int end, MatchFinder finder)
    {
        int n = end - start;
        var steps = new StepCache(n);
        for (int i = 0; i < n; i++)
        {
            finder.Find(start + i, end, steps, i);
        }

        Costs costs = Costs.Fixed();
        List<Token>? best = null;
        long bestBits = long.MaxValue;
        float[] cost = new float[n + 1];
        ushort[] length = new ushort[n + 1];
        ushort[] distance = new ushort[n + 1];
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            List<Token> tokens = Cheapest(data, start, n, steps, costs, cost, length, distance);
            (int[] literals, int[] distances) = Histogram(tokens);
            (byte[] literalLengths, byte[] distanceLengths) = (Lengths(literals, 15), Lengths(distances, 15));
            long bits = BlockBits(tokens, literalLengths, distanceLengths);
            if (bits < bestBits)
            {
                bestBits = bits;
                best = tokens;
            }
            else if (attempt > 2)
            {
                break;
            }

            costs = Costs.From(literalLengths, distanceLengths);
        }

        return best!;
    }

    // The forward dynamic programme: cost[i] is the cheapest way to code the first i bytes of the block.
    private static List<Token> Cheapest(ReadOnlySpan<byte> data, int start, int n, StepCache steps, Costs costs, float[] cost, ushort[] length, ushort[] distance)
    {
        Array.Fill(cost, float.MaxValue, 0, n + 1);
        cost[0] = 0;
        float[] literal = costs.Literal;
        float[] lengthCost = costs.ByLength;
        for (int i = 0; i < n; i++)
        {
            float here = cost[i];
            float viaLiteral = here + literal[data[start + i]];
            if (viaLiteral < cost[i + 1])
            {
                cost[i + 1] = viaLiteral;
                length[i + 1] = 0;
            }

            int count = steps.Count(i);
            int previous = MinMatch - 1;
            for (int s = 0; s < count; s++)
            {
                (int stepLength, int stepDistance) = steps.Get(i, s);
                int limit = Math.Min(stepLength, n - i);
                float viaDistance = here + costs.DistanceCost(stepDistance);
                for (int l = previous + 1; l <= limit; l++)
                {
                    float total = viaDistance + lengthCost[l];
                    if (total < cost[i + l])
                    {
                        cost[i + l] = total;
                        length[i + l] = (ushort)l;
                        distance[i + l] = (ushort)stepDistance;
                    }
                }

                previous = stepLength;
                if (previous >= n - i)
                {
                    break;
                }
            }
        }

        // Back from the end.
        var tokens = new List<Token>();
        for (int at = n; at > 0;)
        {
            int l = length[at];
            if (l == 0)
            {
                tokens.Add(new Token(0, data[start + at - 1]));
                at--;
            }
            else
            {
                tokens.Add(new Token((ushort)l, distance[at]));
                at -= l;
            }
        }

        tokens.Reverse();
        return tokens;
    }

    // Block splitting: a block whose symbols change character codes better as two, each with its own Huffman codes.
    // The split point is the one that saves the most bits, header included, tried at sixteenths of the block; each
    // half is split again while splitting saves anything. Blocks stay above MinSplitTokens tokens.
    private const int MinSplitTokens = 1024;

    private static List<(int From, int To)> Split(List<Token> tokens)
    {
        var blocks = new List<(int, int)>();
        SplitInto(tokens, 0, tokens.Count, Cost(tokens, 0, tokens.Count), blocks);
        return blocks;
    }

    private static void SplitInto(List<Token> tokens, int from, int to, long whole, List<(int, int)> blocks)
    {
        long bestCost = whole;
        int bestAt = -1;
        long bestLeft = 0;
        long bestRight = 0;
        if (to - from >= 2 * MinSplitTokens)
        {
            for (int k = 1; k < 16; k++)
            {
                int at = from + (int)((long)(to - from) * k / 16);
                if (at - from < MinSplitTokens || to - at < MinSplitTokens)
                {
                    continue;
                }

                long left = Cost(tokens, from, at);
                long right = Cost(tokens, at, to);
                if (left + right < bestCost)
                {
                    (bestCost, bestAt, bestLeft, bestRight) = (left + right, at, left, right);
                }
            }
        }

        if (bestAt < 0)
        {
            blocks.Add((from, to));
            return;
        }

        SplitInto(tokens, from, bestAt, bestLeft, blocks);
        SplitInto(tokens, bestAt, to, bestRight, blocks);
    }

    // A dynamic block's size in bits for these tokens: header (code lengths, run-length coded) and data.
    private static long Cost(List<Token> tokens, int from, int to)
    {
        List<Token> range = tokens.GetRange(from, to - from);
        (int[] literals, int[] distances) = Histogram(range);
        byte[] literalLengths = Lengths(literals, 15);
        byte[] distanceLengths = Lengths(distances, 15);
        long bits = BlockBits(range, literalLengths, distanceLengths);

        int hlit = 286;
        while (hlit > 257 && literalLengths[hlit - 1] == 0)
        {
            hlit--;
        }

        int hdist = 30;
        while (hdist > 1 && distanceLengths[hdist - 1] == 0)
        {
            hdist--;
        }

        byte[] all = [.. literalLengths.AsSpan(0, hlit), .. distanceLengths.AsSpan(0, hdist)];
        List<(int Symbol, int Extra, int ExtraBits)> runs = RunLengths(all);
        int[] counts = new int[19];
        foreach ((int symbol, _, _) in runs)
        {
            counts[symbol]++;
        }

        byte[] codeLengthLengths = Lengths(counts, 7);
        bits += 3 + 5 + 5 + 4 + (19 * 3);
        foreach ((int symbol, _, int extraBits) in runs)
        {
            bits += codeLengthLengths[symbol] + extraBits;
        }

        return bits;
    }

    private static (int[] Literals, int[] Distances) Histogram(List<Token> tokens)
    {
        int[] literals = new int[286];
        int[] distances = new int[30];
        foreach (Token token in tokens)
        {
            if (token.Length == 0)
            {
                literals[token.DistanceOrLiteral]++;
            }
            else
            {
                literals[257 + LengthSymbol[token.Length]]++;
                distances[DistanceSymbol(token.DistanceOrLiteral)]++;
            }
        }

        literals[256] = 1;
        return (literals, distances);
    }

    private static long BlockBits(List<Token> tokens, byte[] literalLengths, byte[] distanceLengths)
    {
        long bits = literalLengths[256];
        foreach (Token token in tokens)
        {
            if (token.Length == 0)
            {
                bits += literalLengths[token.DistanceOrLiteral];
            }
            else
            {
                int ls = LengthSymbol[token.Length];
                int ds = DistanceSymbol(token.DistanceOrLiteral);
                bits += literalLengths[257 + ls] + LengthExtra[ls] + distanceLengths[ds] + DistanceExtra[ds];
            }
        }

        return bits;
    }

    private static void WriteBlock(BitWriter writer, List<Token> tokens, bool final)
    {
        (int[] literals, int[] distances) = Histogram(tokens);
        byte[] literalLengths = Lengths(literals, 15);
        byte[] distanceLengths = Lengths(distances, 15);

        // At least one distance code, and two so neither is a lone code of length 0 (inflaters accept either).
        if (Array.TrueForAll(distanceLengths, l => l == 0))
        {
            distanceLengths[0] = 1;
            distanceLengths[1] = 1;
        }

        int hlit = 286;
        while (hlit > 257 && literalLengths[hlit - 1] == 0)
        {
            hlit--;
        }

        int hdist = 30;
        while (hdist > 1 && distanceLengths[hdist - 1] == 0)
        {
            hdist--;
        }

        // The two code lengths in a row, run-length coded with 16, 17 and 18 (3.2.7).
        byte[] all = [.. literalLengths.AsSpan(0, hlit), .. distanceLengths.AsSpan(0, hdist)];
        List<(int Symbol, int Extra, int ExtraBits)> runs = RunLengths(all);
        int[] codeLengthCounts = new int[19];
        foreach ((int symbol, _, _) in runs)
        {
            codeLengthCounts[symbol]++;
        }

        byte[] codeLengthLengths = Lengths(codeLengthCounts, 7);
        int hclen = 19;
        while (hclen > 4 && codeLengthLengths[CodeLengthOrder[hclen - 1]] == 0)
        {
            hclen--;
        }

        writer.Write(final ? 1 : 0, 1);
        writer.Write(2, 2);
        writer.Write(hlit - 257, 5);
        writer.Write(hdist - 1, 5);
        writer.Write(hclen - 4, 4);
        for (int i = 0; i < hclen; i++)
        {
            writer.Write(codeLengthLengths[CodeLengthOrder[i]], 3);
        }

        ushort[] codeLengthCodes = Codes(codeLengthLengths);
        foreach ((int symbol, int extra, int extraBits) in runs)
        {
            writer.Write(codeLengthCodes[symbol], codeLengthLengths[symbol]);
            writer.Write(extra, extraBits);
        }

        ushort[] literalCodes = Codes(literalLengths);
        ushort[] distanceCodes = Codes(distanceLengths);
        foreach (Token token in tokens)
        {
            if (token.Length == 0)
            {
                writer.Write(literalCodes[token.DistanceOrLiteral], literalLengths[token.DistanceOrLiteral]);
                continue;
            }

            int ls = LengthSymbol[token.Length];
            writer.Write(literalCodes[257 + ls], literalLengths[257 + ls]);
            writer.Write(token.Length - LengthBase[ls], LengthExtra[ls]);
            int ds = DistanceSymbol(token.DistanceOrLiteral);
            writer.Write(distanceCodes[ds], distanceLengths[ds]);
            writer.Write(token.DistanceOrLiteral - DistanceBase[ds], DistanceExtra[ds]);
        }

        writer.Write(literalCodes[256], literalLengths[256]);
    }

    private static List<(int Symbol, int Extra, int ExtraBits)> RunLengths(byte[] lengths)
    {
        var runs = new List<(int, int, int)>();
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
                    runs.Add((18, take - 11, 7));
                    run -= take;
                }

                if (run >= 3)
                {
                    runs.Add((17, run - 3, 3));
                    run = 0;
                }
            }
            else
            {
                runs.Add((value, 0, 0));
                run--;
                while (run >= 3)
                {
                    int take = Math.Min(run, 6);
                    runs.Add((16, take - 3, 2));
                    run -= take;
                }
            }

            for (; run > 0; run--)
            {
                runs.Add((value, 0, 0));
            }
        }

        return runs;
    }

    /// <summary>Huffman code lengths for the counts, none longer than <paramref name="limit"/>; 0 for unused symbols.</summary>
    internal static byte[] Lengths(int[] counts, int limit)
    {
        byte[] lengths = new byte[counts.Length];
        var used = new List<int>();
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] > 0)
            {
                used.Add(i);
            }
        }

        if (used.Count == 0)
        {
            return lengths;
        }

        if (used.Count == 1)
        {
            lengths[used[0]] = 1;
            return lengths;
        }

        // Huffman's merging over a queue of (weight, tie-break) pairs; depths from parent links.
        int leaves = used.Count;
        int[] parent = new int[(2 * leaves) - 1];
        var queue = new PriorityQueue<int, (long Weight, int Order)>();
        for (int i = 0; i < leaves; i++)
        {
            queue.Enqueue(i, (counts[used[i]], i));
        }

        int next = leaves;
        while (queue.Count > 1)
        {
            queue.TryDequeue(out int a, out (long Weight, int Order) wa);
            queue.TryDequeue(out int b, out (long Weight, int Order) wb);
            parent[a] = next;
            parent[b] = next;
            queue.Enqueue(next, (wa.Weight + wb.Weight, next));
            next++;
        }

        int root = next - 1;
        int[] depth = new int[next];
        for (int node = root - 1; node >= 0; node--)
        {
            depth[node] = depth[parent[node]] + 1;
        }

        // Too long: the usual repair. Count leaves at each depth, move the deepest up two at a time as their parent
        // takes a shorter leaf's place, then give the shortest codes to the most frequent symbols.
        int deepest = 0;
        for (int i = 0; i < leaves; i++)
        {
            deepest = Math.Max(deepest, depth[i]);
        }

        int[] perLength = new int[Math.Max(limit, deepest) + 1];
        for (int i = 0; i < leaves; i++)
        {
            perLength[depth[i]]++;
        }

        for (int bits = deepest; bits > limit; bits--)
        {
            while (perLength[bits] > 0)
            {
                int j = bits - 2;
                while (perLength[j] == 0)
                {
                    j--;
                }

                perLength[bits] -= 2;
                perLength[bits - 1]++;
                perLength[j + 1] += 2;
                perLength[j]--;
            }
        }

        used.Sort((x, y) => counts[y] != counts[x] ? counts[y].CompareTo(counts[x]) : x.CompareTo(y));
        int at = 0;
        for (int bits = 1; bits <= limit; bits++)
        {
            for (int k = 0; k < perLength[bits]; k++)
            {
                lengths[used[at++]] = (byte)bits;
            }
        }

        return lengths;
    }

    // Canonical codes from lengths (3.2.2), bit-reversed for writing least significant bit first.
    private static ushort[] Codes(byte[] lengths)
    {
        int[] perLength = new int[16];
        foreach (byte l in lengths)
        {
            perLength[l]++;
        }

        perLength[0] = 0;
        int[] next = new int[16];
        int code = 0;
        for (int bits = 1; bits < 16; bits++)
        {
            code = (code + perLength[bits - 1]) << 1;
            next[bits] = code;
        }

        ushort[] codes = new ushort[lengths.Length];
        for (int i = 0; i < lengths.Length; i++)
        {
            int l = lengths[i];
            if (l > 0)
            {
                codes[i] = Reverse(next[l]++, l);
            }
        }

        return codes;
    }

    private static ushort Reverse(int code, int bits)
    {
        int reversed = 0;
        for (int i = 0; i < bits; i++)
        {
            reversed = (reversed << 1) | ((code >> i) & 1);
        }

        return (ushort)reversed;
    }

    private static byte[] BuildLengthSymbols()
    {
        byte[] symbols = new byte[MaxMatch + 1];
        for (int code = 0; code < LengthBase.Length; code++)
        {
            int top = code + 1 < LengthBase.Length ? LengthBase[code + 1] - 1 : MaxMatch;
            for (int l = LengthBase[code]; l <= top && l <= MaxMatch; l++)
            {
                symbols[l] = (byte)code;
            }
        }

        symbols[MaxMatch] = 28;
        return symbols;
    }

    private static int DistanceSymbol(int distance)
    {
        if (distance <= 4)
        {
            return distance - 1;
        }

        int bits = 31 - BitOperations.LeadingZeroCount((uint)(distance - 1));
        return (2 * bits) + ((distance - 1) >> (bits - 1) & 1);
    }

    /// <summary>The cost model: bits for each literal, each match length (code and extra bits) and each distance code.</summary>
    private sealed class Costs(float[] literal, float[] byLength, float[] distance)
    {
        public float[] Literal { get; } = literal;

        public float[] ByLength { get; } = byLength;

        public float DistanceCost(int d)
        {
            int symbol = DistanceSymbol(d);
            return distance[symbol] + DistanceExtra[symbol];
        }

        public static Costs Fixed()
        {
            byte[] literalLengths = new byte[288];
            for (int i = 0; i < 288; i++)
            {
                literalLengths[i] = (byte)(i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8);
            }

            byte[] distanceLengths = new byte[30];
            Array.Fill(distanceLengths, (byte)5);
            return From(literalLengths, distanceLengths);
        }

        // Unused symbols get a cost a little above the longest code, so the next parse can still choose them.
        public static Costs From(byte[] literalLengths, byte[] distanceLengths)
        {
            float[] literal = new float[256];
            for (int i = 0; i < 256; i++)
            {
                literal[i] = literalLengths[i] == 0 ? 16 : literalLengths[i];
            }

            float[] byLength = new float[MaxMatch + 1];
            for (int l = MinMatch; l <= MaxMatch; l++)
            {
                int symbol = LengthSymbol[l];
                byte code = literalLengths[257 + symbol];
                byLength[l] = (code == 0 ? 16 : code) + LengthExtra[symbol];
            }

            float[] distance = new float[30];
            for (int i = 0; i < 30; i++)
            {
                distance[i] = distanceLengths[i] == 0 ? 16 : distanceLengths[i];
            }

            return new Costs(literal, byLength, distance);
        }
    }

    /// <summary>Each position's match steps: (longest length at this distance, distance), lengths and distances increasing.</summary>
    private sealed class StepCache(int positions)
    {
        private readonly byte[] _count = new byte[positions];
        private readonly ushort[] _length = new ushort[positions * Steps];
        private readonly ushort[] _distance = new ushort[positions * Steps];

        public int Count(int position) => _count[position];

        public (int Length, int Distance) Get(int position, int step) =>
            (_length[(position * Steps) + step], _distance[(position * Steps) + step]);

        public void Set(int position, ReadOnlySpan<int> lengths, ReadOnlySpan<int> distances, int count)
        {
            // More steps than room: drop the middle ones, letting a farther distance cover their lengths, which is valid
            // because a match at a distance also matches every shorter length there.
            int keep = Math.Min(count, Steps);
            for (int s = 0; s < keep; s++)
            {
                int from = count <= Steps ? s : (int)((long)s * (count - 1) / (Steps - 1));
                _length[(position * Steps) + s] = (ushort)lengths[from];
                _distance[(position * Steps) + s] = (ushort)distances[from];
            }

            _count[position] = (byte)keep;
        }
    }

    /// <summary>Hash chains over three-byte prefixes, filled as positions are visited in order.</summary>
    private sealed class MatchFinder
    {
        private readonly byte[] _data;
        private readonly int[] _head = new int[1 << HashBits];
        private readonly int[] _previous;
        private int _inserted;

        public MatchFinder(ReadOnlySpan<byte> data)
        {
            _data = data.ToArray();
            _previous = new int[Math.Max(_data.Length, 1)];
            Array.Fill(_head, -1);
        }

        /// <summary>Finds the steps at <paramref name="position"/>, matches ending before <paramref name="end"/>.</summary>
        public void Find(int position, int end, StepCache cache, int index)
        {
            Insert(position);
            Span<int> lengths = stackalloc int[64];
            Span<int> distances = stackalloc int[64];
            int count = 0;
            int limit = Math.Min(MaxMatch, end - position);
            if (limit >= MinMatch)
            {
                ReadOnlySpan<byte> data = _data;
                ReadOnlySpan<byte> here = data.Slice(position, limit);
                int best = MinMatch - 1;
                int candidate = _previous[position];
                for (int chain = 0; candidate >= 0 && position - candidate <= Window && chain < MaxChain; chain++)
                {
                    // The byte past the best so far must match before a longer match is possible.
                    if (data[candidate + best] == here[best] || best == MinMatch - 1)
                    {
                        int length = here.CommonPrefixLength(data.Slice(candidate, limit));
                        if (length > best)
                        {
                            best = length;
                            if (count == lengths.Length)
                            {
                                // Keep the newest (longest) step in the last slot.
                                count--;
                            }

                            lengths[count] = length;
                            distances[count] = position - candidate;
                            count++;
                            if (length == limit)
                            {
                                break;
                            }
                        }
                    }

                    candidate = _previous[candidate];
                }
            }

            cache.Set(index, lengths, distances, count);
        }

        private void Insert(int upTo)
        {
            for (; _inserted <= upTo; _inserted++)
            {
                if (_inserted + MinMatch > _data.Length)
                {
                    _previous[_inserted] = -1;
                    continue;
                }

                int hash = ((_data[_inserted] << 10) ^ (_data[_inserted + 1] << 5) ^ _data[_inserted + 2]) & ((1 << HashBits) - 1);
                _previous[_inserted] = _head[hash];
                _head[hash] = _inserted;
            }
        }
    }

    /// <summary>Writes bits least significant first, as deflate packs them.</summary>
    private sealed class BitWriter(int capacity)
    {
        private ulong _buffer;
        private int _count;

        public byte[] Buffer { get; private set; } = new byte[capacity];

        public int Length { get; private set; }

        public void Write(int value, int bits)
        {
            if (bits == 0)
            {
                return;
            }

            _buffer |= (ulong)(uint)value << _count;
            _count += bits;
            while (_count >= 8)
            {
                if (Length == Buffer.Length)
                {
                    byte[] larger = new byte[Buffer.Length * 2];
                    Buffer.CopyTo(larger, 0);
                    Buffer = larger;
                }

                Buffer[Length++] = (byte)_buffer;
                _buffer >>= 8;
                _count -= 8;
            }
        }

        public void Align()
        {
            if (_count > 0)
            {
                Write(0, 8 - _count);
            }
        }
    }
}
