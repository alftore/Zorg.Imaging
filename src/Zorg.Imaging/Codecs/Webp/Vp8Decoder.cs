using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Zorg.Imaging.Formats;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Codecs.Webp;

/// <summary>Decodes a VP8 key frame, as a lossy WebP holds one, into Y, U and V planes (RFC 6386).</summary>
/// <remarks>
/// <para>
/// Sections 9 and 19 (frame header), 11 (modes), 13 (coefficient tokens), 12 (intra prediction), 14 (dequantisation and
/// the inverse WHT and DCT) and 15 (loop filter), with the reference decoder in section 20 where the prose leaves room.
/// Every macroblock is reconstructed first and the loop filter then runs over the frame in raster order, as the
/// reference decoder does, so prediction always sees unfiltered pixels.
/// </para>
/// <para>
/// The planes have a border: one row of 127 above, one column of 129 on the left, and four spare columns on the right of
/// the luma plane for the above-right pixels of the last macroblock in each row (section 12.3).
/// </para>
/// </remarks>
internal sealed class Vp8Decoder
{
    internal const int DcPred = 0;
    internal const int VPred = 1;
    internal const int HPred = 2;
    internal const int TmPred = 3;
    internal const int BPred = 4;

    internal const int BDcPred = 0;
    internal const int BTmPred = 1;
    internal const int BVePred = 2;
    internal const int BHePred = 3;
    internal const int BLdPred = 4;
    internal const int BRdPred = 5;
    internal const int BVrPred = 6;
    internal const int BVlPred = 7;
    internal const int BHdPred = 8;
    internal const int BHuPred = 9;

    private readonly byte[] _coefficientProbabilities = Vp8Tables.DefaultCoefficientProbabilities.ToArray();
    private readonly int[] _segmentQuantiser = new int[4];
    private readonly int[] _segmentFilter = new int[4];
    private readonly byte[] _segmentProbabilities = [255, 255, 255];
    private readonly int[] _referenceDelta = new int[4];
    private readonly int[] _modeDelta = new int[4];
    private readonly short[] _dequant = new short[4 * 6];
    private readonly int[] _edge = new int[13];

    private Vp8BoolDecoder _header = null!;
    private Vp8BoolDecoder[] _partitions = [];
    private bool _segmentation;
    private bool _updateSegmentMap;
    private bool _segmentAbsolute;
    private bool _simpleFilter;
    private int _filterLevel;
    private int _sharpness;
    private bool _filterDeltas;
    private bool _skipEnabled;
    private int _skipProbability;

    private int _mbWide;
    private int _mbHigh;
    private Plane _y = null!;
    private Plane _u = null!;
    private Plane _v = null!;

    private Vp8Decoder()
    {
    }

    // Section 11.2 to 11.4: the key-frame mode trees, as tree_index arrays (leaves are negated values).
    internal static ReadOnlySpan<sbyte> YModeTree => [-BPred, 2, 4, 6, -DcPred, -VPred, -HPred, -TmPred];

    internal static ReadOnlySpan<byte> YModeProbabilities => [145, 156, 163, 128];

    internal static ReadOnlySpan<sbyte> UvModeTree => [-DcPred, 2, -VPred, 4, -HPred, -TmPred];

    internal static ReadOnlySpan<byte> UvModeProbabilities => [142, 114, 183];

    internal static ReadOnlySpan<sbyte> SubblockModeTree =>
        [-BDcPred, 2, -BTmPred, 4, -BVePred, 6, 8, 12, -BHePred, 10, -BRdPred, -BVrPred, -BLdPred, 14, -BVlPred, 16, -BHdPred, -BHuPred];

    internal static ReadOnlySpan<byte> Bands => [0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 0];

    internal static ReadOnlySpan<byte> ZigZag => [0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15];

    internal static ReadOnlySpan<byte> Cat3 => [173, 148, 140];

    internal static ReadOnlySpan<byte> Cat4 => [176, 155, 140, 135];

    internal static ReadOnlySpan<byte> Cat5 => [180, 157, 141, 134, 130];

    internal static ReadOnlySpan<byte> Cat6 => [254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129];

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Reads the frame size from the start of a VP8 chunk (section 9.1).</summary>
    public static (int Width, int Height) ReadSize(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10)
        {
            throw ImageErrors.Corrupt("The WebP lossy data is too short for its frame header.");
        }

        if ((data[0] & 1) != 0)
        {
            throw ImageErrors.Corrupt("The WebP lossy data isn't a key frame.");
        }

        if (data[3] != 0x9D || data[4] != 0x01 || data[5] != 0x2A)
        {
            throw ImageErrors.Corrupt("The WebP lossy data has no VP8 start code.");
        }

        int width = (data[6] | (data[7] << 8)) & 0x3FFF;
        int height = (data[8] | (data[9] << 8)) & 0x3FFF;
        if (width == 0 || height == 0)
        {
            throw ImageErrors.Corrupt("The WebP lossy image has a width or height of 0.");
        }

        return (width, height);
    }

    /// <summary>Decodes a whole VP8 chunk. The caller has checked the size against the limits.</summary>
    /// <param name="data">The chunk.</param>
    /// <param name="convertRow">
    /// Called once for each macroblock row, in order, as soon as that row's pixels are final, to convert them.
    /// </param>
    public static Vp8Decoder Decode(byte[] data, Action<Vp8Decoder, int> convertRow)
    {
        var decoder = new Vp8Decoder();
        (decoder.Width, decoder.Height) = ReadSize(data);
        int firstPartition = (data[0] | (data[1] << 8) | (data[2] << 16)) >> 5;
        if (10L + firstPartition > data.Length)
        {
            throw ImageErrors.Corrupt("The WebP lossy data's first partition reaches past its end.");
        }

        decoder._header = new Vp8BoolDecoder(data, 10, firstPartition);
        decoder.ReadFrameHeader(data, 10 + firstPartition);
        new RowPipeline(decoder, convertRow).Run();
        return decoder;
    }

    /// <summary>Y, U and V for one output row, the chroma at half resolution: the rows the caller converts to RGB.</summary>
    public ReadOnlySpan<byte> LumaRow(int y) => _y.Row(y).Slice(0, Width);

    public ReadOnlySpan<byte> URow(int y) => _u.Row(y).Slice(0, (Width + 1) / 2);

    public ReadOnlySpan<byte> VRow(int y) => _v.Row(y).Slice(0, (Width + 1) / 2);

    // Section 9.2 to 9.11 and 19.2.
    private void ReadFrameHeader(byte[] data, int partitionsStart)
    {
        Vp8BoolDecoder h = _header;
        h.ReadFlag();   // colour space: only one is defined
        h.ReadFlag();   // clamping type: we always clamp

        _segmentation = h.ReadFlag();
        if (_segmentation)
        {
            _updateSegmentMap = h.ReadFlag();
            bool updateData = h.ReadFlag();
            if (updateData)
            {
                _segmentAbsolute = h.ReadFlag();
                for (int i = 0; i < 4; i++)
                {
                    _segmentQuantiser[i] = h.ReadOptionalSigned(7);
                }

                for (int i = 0; i < 4; i++)
                {
                    _segmentFilter[i] = h.ReadOptionalSigned(6);
                }
            }

            if (_updateSegmentMap)
            {
                for (int i = 0; i < 3; i++)
                {
                    _segmentProbabilities[i] = (byte)(h.ReadFlag() ? h.ReadLiteral(8) : 255);
                }
            }
        }

        _simpleFilter = h.ReadFlag();
        _filterLevel = h.ReadLiteral(6);
        _sharpness = h.ReadLiteral(3);
        _filterDeltas = h.ReadFlag();
        if (_filterDeltas && h.ReadFlag())
        {
            for (int i = 0; i < 4; i++)
            {
                if (h.ReadFlag())
                {
                    _referenceDelta[i] = h.ReadSigned(6);
                }
            }

            for (int i = 0; i < 4; i++)
            {
                if (h.ReadFlag())
                {
                    _modeDelta[i] = h.ReadSigned(6);
                }
            }
        }

        // Section 9.5: the token partitions, their sizes as 3-byte values before them, the last taking the rest.
        int count = 1 << h.ReadLiteral(2);
        int sizes = partitionsStart;
        int at = partitionsStart + (3 * (count - 1));
        if (at > data.Length)
        {
            throw ImageErrors.Corrupt("The WebP lossy data's partition sizes reach past its end.");
        }

        _partitions = new Vp8BoolDecoder[count];
        for (int i = 0; i < count; i++)
        {
            int size = i < count - 1 ? data[sizes + (3 * i)] | (data[sizes + (3 * i) + 1] << 8) | (data[sizes + (3 * i) + 2] << 16) : data.Length - at;
            if (size < 0 || (long)at + size > data.Length)
            {
                throw ImageErrors.Corrupt("A WebP lossy partition reaches past the end of its data.");
            }

            _partitions[i] = new Vp8BoolDecoder(data, at, size);
            at += size;
        }

        // Section 9.6: the quantiser indexes, then the factors for each segment (section 14.1, dequant_init).
        int baseIndex = h.ReadLiteral(7);
        int yDc = h.ReadOptionalSigned(4);
        int y2Dc = h.ReadOptionalSigned(4);
        int y2Ac = h.ReadOptionalSigned(4);
        int uvDc = h.ReadOptionalSigned(4);
        int uvAc = h.ReadOptionalSigned(4);
        for (int s = 0; s < 4; s++)
        {
            int q = !_segmentation ? baseIndex : _segmentAbsolute ? _segmentQuantiser[s] : baseIndex + _segmentQuantiser[s];
            _dequant[(s * 6) + 0] = Dc(q + yDc);
            _dequant[(s * 6) + 1] = Ac(q);
            _dequant[(s * 6) + 2] = (short)(Dc(q + y2Dc) * 2);
            _dequant[(s * 6) + 3] = (short)Math.Max(Ac(q + y2Ac) * 155 / 100, 8);
            _dequant[(s * 6) + 4] = (short)Math.Min((int)Dc(q + uvDc), 132);
            _dequant[(s * 6) + 5] = Ac(q + uvAc);
        }

        h.ReadFlag();   // refresh_entropy_probs: one frame only

        // Section 13.4: token probability updates.
        ReadOnlySpan<byte> update = Vp8Tables.CoefficientUpdateProbabilities;
        for (int i = 0; i < _coefficientProbabilities.Length; i++)
        {
            if (h.ReadBool(update[i]))
            {
                _coefficientProbabilities[i] = (byte)h.ReadLiteral(8);
            }
        }

        _skipEnabled = h.ReadFlag();
        if (_skipEnabled)
        {
            _skipProbability = h.ReadLiteral(8);
        }

        _mbWide = (Width + 15) / 16;
        _mbHigh = (Height + 15) / 16;
        _y = new Plane(_mbWide * 16, _mbHigh * 16, extra: 4);
        _u = new Plane(_mbWide * 8, _mbHigh * 8, extra: 0);
        _v = new Plane(_mbWide * 8, _mbHigh * 8, extra: 0);
        _macroblocks = new Macroblock[_mbWide * _mbHigh];
        PrepareRows();
    }

    /// <summary>The number of macroblock rows: the units the row pipeline works in.</summary>
    public int MacroblockRows => _mbHigh;

    private static short Dc(int index) => (short)Vp8Tables.DcQuantiser[Math.Clamp(index, 0, 127)];

    private static short Ac(int index) => (short)Vp8Tables.AcQuantiser[Math.Clamp(index, 0, 127)];

    private Macroblock[] _macroblocks = [];

    private struct Macroblock
    {
        public int YMode;
        public int UvMode;
        public int Segment;
        public bool HasCoefficients;
    }

    // Rows parsed but not yet reconstructed are kept in a ring this deep.
    private const int Ring = 4;

    private byte[] _aboveModes = [];
    private byte[] _aboveNonZero = [];
    private short[][] _ringCoefficients = [];
    private ulong[][] _ringNonZero = [];
    private byte[][] _ringModes = [];

    private void PrepareRows()
    {
        _aboveModes = new byte[_mbWide * 4];
        _aboveNonZero = new byte[_mbWide * 9];
        _ringCoefficients = new short[Ring][];
        _ringNonZero = new ulong[Ring][];
        _ringModes = new byte[Ring][];
        for (int i = 0; i < Ring; i++)
        {
            _ringCoefficients[i] = new short[_mbWide * 25 * 16];
            _ringNonZero[i] = new ulong[_mbWide];
            _ringModes[i] = new byte[_mbWide * 16];
        }
    }

    // Stage 1: one row's modes (first partition) and coefficients (its token partition), into the ring.
    private void ParseRow(int my)
    {
        // Contexts: the subblock modes above (4 per macroblock) and to the left, and for tokens whether the blocks
        // above and to the left had coefficients: 4 Y, 2 U, 2 V and the Y2 block (section 13.3).
        Span<byte> leftModes = stackalloc byte[4];
        Span<byte> leftNonZero = stackalloc byte[9];
        leftModes.Clear();
        leftNonZero.Clear();
        Vp8BoolDecoder tokens = _partitions[my % _partitions.Length];
        int slot = my % Ring;
        short[] rowCoefficients = _ringCoefficients[slot];
        Array.Clear(rowCoefficients);

        for (int mx = 0; mx < _mbWide; mx++)
        {
            ref Macroblock mb = ref _macroblocks[(my * _mbWide) + mx];
            Span<byte> modes = _ringModes[slot].AsSpan(mx * 16, 16);
            Vp8BoolDecoder h = _header;
            if (_updateSegmentMap)
            {
                mb.Segment = !h.ReadBool(_segmentProbabilities[0])
                    ? (h.ReadBool(_segmentProbabilities[1]) ? 1 : 0)
                    : (h.ReadBool(_segmentProbabilities[2]) ? 3 : 2);
            }

            bool skip = _skipEnabled && h.ReadBool(_skipProbability);
            mb.YMode = h.ReadTree(YModeTree, YModeProbabilities);
            Span<byte> above = _aboveModes.AsSpan(mx * 4, 4);
            if (mb.YMode == BPred)
            {
                ReadOnlySpan<byte> table = Vp8Tables.KeyFrameSubblockModeProbabilities;
                for (int i = 0; i < 16; i++)
                {
                    int a = i < 4 ? above[i] : modes[i - 4];
                    int l = (i & 3) == 0 ? leftModes[i >> 2] : modes[i - 1];
                    modes[i] = (byte)h.ReadTree(SubblockModeTree, table.Slice(((a * 10) + l) * 9, 9));
                }

                modes.Slice(12, 4).CopyTo(above);
                for (int r = 0; r < 4; r++)
                {
                    leftModes[r] = modes[(r * 4) + 3];
                }
            }
            else
            {
                byte implied = mb.YMode switch { VPred => BVePred, HPred => BHePred, TmPred => BTmPred, _ => BDcPred };
                above.Fill(implied);
                leftModes.Fill(implied);
            }

            mb.UvMode = h.ReadTree(UvModeTree, UvModeProbabilities);

            Span<byte> aboveTokens = _aboveNonZero.AsSpan(mx * 9, 9);
            ulong nonZero = 0;
            if (!skip)
            {
                nonZero = ReadCoefficients(tokens, mb, rowCoefficients.AsSpan(mx * 25 * 16, 25 * 16), leftNonZero, aboveTokens);
            }
            else
            {
                // A skipped macroblock leaves no coefficients; the Y2 context survives only where there's no Y2.
                leftNonZero[..8].Clear();
                aboveTokens[..8].Clear();
                if (mb.YMode != BPred)
                {
                    leftNonZero[8] = 0;
                    aboveTokens[8] = 0;
                }
            }

            mb.HasCoefficients = nonZero != 0;
            _ringNonZero[slot][mx] = nonZero;
        }
    }

    // Stage 2: one row's prediction and residuals, from the ring.
    private void ReconstructRow(int my)
    {
        // The above-right pixels of the last macroblock repeat the last pixel of the row above (section 12.3).
        if (my > 0)
        {
            Span<byte> above = _y.Row((my * 16) - 1);
            above.Slice(_mbWide * 16, 4).Fill(above[(_mbWide * 16) - 1]);
        }

        int slot = my % Ring;
        for (int mx = 0; mx < _mbWide; mx++)
        {
            Macroblock mb = _macroblocks[(my * _mbWide) + mx];
            Reconstruct(mx, my, mb.YMode, mb.UvMode, _ringModes[slot].AsSpan(mx * 16, 16), _ringCoefficients[slot].AsSpan(mx * 25 * 16, 25 * 16), _ringNonZero[slot][mx]);
        }
    }

    // Section 13: the residual tokens of a macroblock. Bit i of the result is set when block i has any coefficient.
    private ulong ReadCoefficients(Vp8BoolDecoder bits, Macroblock mb, Span<short> coefficients, Span<byte> left, Span<byte> above)
    {
        ulong nonZero = 0;
        int segment = mb.Segment;
        int firstY = 0;
        if (mb.YMode != BPred)
        {
            // The Y2 block: the DCs of the 16 luma blocks, through the WHT (type 1).
            int context = left[8] + above[8];
            int end = ReadBlock(bits, 1, context, 0, coefficients.Slice(24 * 16, 16), _dequant[(segment * 6) + 2], _dequant[(segment * 6) + 3]);
            left[8] = above[8] = (byte)(end > 0 ? 1 : 0);
            if (end > 0)
            {
                nonZero |= 1UL << 24;
            }

            firstY = 1;
        }

        int yType = firstY == 1 ? 0 : 3;
        for (int i = 0; i < 16; i++)
        {
            int x = i & 3;
            int y = i >> 2;
            int context = left[y] + above[x];
            int end = ReadBlock(bits, yType, context, firstY, coefficients.Slice(i * 16, 16), _dequant[(segment * 6) + 0], _dequant[(segment * 6) + 1]);
            byte flag = (byte)(end > firstY ? 1 : 0);
            left[y] = above[x] = flag;
            if (flag != 0)
            {
                nonZero |= 1UL << i;
            }
        }

        for (int i = 16; i < 24; i++)
        {
            int plane = i < 20 ? 4 : 6;
            int x = (i - 16) & 1;
            int y = ((i - 16) >> 1) & 1;
            int context = left[plane + y] + above[plane + x];
            int end = ReadBlock(bits, 2, context, 0, coefficients.Slice(i * 16, 16), _dequant[(segment * 6) + 4], _dequant[(segment * 6) + 5]);
            byte flag = (byte)(end > 0 ? 1 : 0);
            left[plane + y] = above[plane + x] = flag;
            if (flag != 0)
            {
                nonZero |= 1UL << i;
            }
        }

        return nonZero;
    }

    // One block's tokens (section 13.2, with the reference decoder's tree walk). Returns where the block ended.
    private int ReadBlock(Vp8BoolDecoder bits, int type, int context, int first, Span<short> block, short dc, short ac)
    {
        ReadOnlySpan<byte> probabilities = _coefficientProbabilities.AsSpan(type * 8 * 3 * 11, 8 * 3 * 11);
        int c = first;
        ReadOnlySpan<byte> p = probabilities.Slice(((Bands[c] * 3) + context) * 11, 11);
        if (!bits.ReadBool(p[0]))
        {
            return c;
        }

        while (true)
        {
            if (!bits.ReadBool(p[1]))
            {
                // DCT_0: the next token can't be end-of-block, so its first branch is skipped.
                if (++c == 16)
                {
                    return 16;
                }

                p = probabilities.Slice(Bands[c] * 3 * 11, 11);
                continue;
            }

            int value;
            int next;
            if (!bits.ReadBool(p[2]))
            {
                value = 1;
                next = 1;
            }
            else
            {
                next = 2;
                if (!bits.ReadBool(p[3]))
                {
                    value = !bits.ReadBool(p[4]) ? 2 : 3 + (bits.ReadBool(p[5]) ? 1 : 0);
                }
                else if (!bits.ReadBool(p[6]))
                {
                    value = !bits.ReadBool(p[7])
                        ? 5 + (bits.ReadBool(159) ? 1 : 0)
                        : 7 + (bits.ReadBool(165) ? 2 : 0) + (bits.ReadBool(145) ? 1 : 0);
                }
                else
                {
                    bool high = bits.ReadBool(p[8]);
                    bool odd = bits.ReadBool(p[high ? 10 : 9]);
                    ReadOnlySpan<byte> extra = high ? (odd ? Cat6 : Cat5) : (odd ? Cat4 : Cat3);
                    int baseValue = high ? (odd ? 67 : 35) : (odd ? 19 : 11);
                    int v = 0;
                    foreach (byte probability in extra)
                    {
                        v = (v << 1) | (bits.ReadBool(probability) ? 1 : 0);
                    }

                    value = baseValue + v;
                }
            }

            int signed = bits.ReadFlag() ? -value : value;
            block[ZigZag[c]] = (short)(signed * (c > 0 ? ac : dc));
            if (++c == 16)
            {
                return 16;
            }

            p = probabilities.Slice(((Bands[c] * 3) + next) * 11, 11);
            if (!bits.ReadBool(p[0]))
            {
                return c;
            }
        }
    }

    private void Reconstruct(int mx, int my, int yMode, int uvMode, ReadOnlySpan<byte> modes, Span<short> coefficients, ulong nonZero)
    {
        int x0 = mx * 16;
        int y0 = my * 16;
        if (yMode != BPred)
        {
            if ((nonZero & (1UL << 24)) != 0)
            {
                // Section 14.3: the WHT gives each luma block its DC.
                Span<short> dcs = stackalloc short[16];
                InverseWht(coefficients.Slice(24 * 16, 16), dcs);
                for (int i = 0; i < 16; i++)
                {
                    coefficients[i * 16] = dcs[i];
                    if (dcs[i] != 0)
                    {
                        nonZero |= 1UL << i;
                    }
                }
            }

            PredictBlock(_y, x0, y0, 16, yMode, mx == 0, my == 0);
            for (int i = 0; i < 16; i++)
            {
                AddResidual(_y, x0 + ((i & 3) * 4), y0 + ((i >> 2) * 4), coefficients.Slice(i * 16, 16), (nonZero & (1UL << i)) != 0);
            }
        }
        else
        {
            // Each subblock is predicted from pixels that include the subblocks before it.
            for (int i = 0; i < 16; i++)
            {
                int bx = x0 + ((i & 3) * 4);
                int by = y0 + ((i >> 2) * 4);
                PredictSubblock(_y, _edge, bx, by, modes[i], (i & 3) == 3 && i > 3, x0 + 16, y0 - 1);
                AddResidual(_y, bx, by, coefficients.Slice(i * 16, 16), (nonZero & (1UL << i)) != 0);
            }
        }

        int cx = mx * 8;
        int cy = my * 8;
        PredictBlock(_u, cx, cy, 8, uvMode, mx == 0, my == 0);
        PredictBlock(_v, cx, cy, 8, uvMode, mx == 0, my == 0);
        for (int i = 0; i < 4; i++)
        {
            AddResidual(_u, cx + ((i & 1) * 4), cy + ((i >> 1) * 4), coefficients.Slice((16 + i) * 16, 16), (nonZero & (1UL << (16 + i))) != 0);
            AddResidual(_v, cx + ((i & 1) * 4), cy + ((i >> 1) * 4), coefficients.Slice((20 + i) * 16, 16), (nonZero & (1UL << (20 + i))) != 0);
        }
    }

    // Section 12.2 and 12.3: the whole-block modes, with DC's own rule at the frame's top and left edges.
    internal static void PredictBlock(Plane plane, int x0, int y0, int size, int mode, bool leftEdge, bool topEdge)
    {
        switch (mode)
        {
            case DcPred:
            {
                int sum = 0;
                int shift = size == 16 ? 3 : 2;
                if (!topEdge)
                {
                    ReadOnlySpan<byte> above = plane.Row(y0 - 1).Slice(x0, size);
                    foreach (byte b in above)
                    {
                        sum += b;
                    }

                    shift++;
                }

                if (!leftEdge)
                {
                    for (int y = 0; y < size; y++)
                    {
                        sum += plane.At(x0 - 1, y0 + y);
                    }

                    shift++;
                }

                byte value = topEdge && leftEdge ? (byte)128 : (byte)((sum + (1 << (shift - 1))) >> shift);
                for (int y = 0; y < size; y++)
                {
                    plane.Row(y0 + y).Slice(x0, size).Fill(value);
                }

                break;
            }

            case VPred:
            {
                ReadOnlySpan<byte> above = plane.Row(y0 - 1).Slice(x0, size);
                for (int y = 0; y < size; y++)
                {
                    above.CopyTo(plane.Row(y0 + y).Slice(x0, size));
                }

                break;
            }

            case HPred:
                for (int y = 0; y < size; y++)
                {
                    plane.Row(y0 + y).Slice(x0, size).Fill(plane.At(x0 - 1, y0 + y));
                }

                break;

            default:
            {
                ReadOnlySpan<byte> above = plane.Row(y0 - 1).Slice(x0, size);
                int corner = plane.At(x0 - 1, y0 - 1);
                for (int y = 0; y < size; y++)
                {
                    int left = plane.At(x0 - 1, y0 + y) - corner;
                    Span<byte> row = plane.Row(y0 + y).Slice(x0, size);
                    for (int x = 0; x < size; x++)
                    {
                        row[x] = Clamp(above[x] + left);
                    }
                }

                break;
            }
        }
    }

    // Section 12.3: the ten subblock modes. A is the 8 pixels above (4 above, 4 above-right), L the 4 to the left.
    internal static void PredictSubblock(Plane plane, int[] edge, int x0, int y0, int mode, bool useMacroblockAboveRight, int aboveRightX, int aboveRightY)
    {
        FillSubblockEdge(plane, edge, x0, y0, useMacroblockAboveRight, aboveRightX, aboveRightY);
        Span<byte> b = stackalloc byte[16];
        PredictSubblock(edge, mode, b);
        WriteSubblock(plane, x0, y0, b);
    }

    /// <summary>Copies a 4 x 4 block of samples into the plane.</summary>
    internal static void WriteSubblock(Plane plane, int x0, int y0, ReadOnlySpan<byte> b)
    {
        for (int r = 0; r < 4; r++)
        {
            b.Slice(r * 4, 4).CopyTo(plane.Row(y0 + r).Slice(x0, 4));
        }
    }

    /// <summary>Reads the 13 pixels a subblock's prediction uses into <paramref name="edge"/>; the same for every mode.</summary>
    internal static void FillSubblockEdge(Plane plane, int[] edge, int x0, int y0, bool useMacroblockAboveRight, int aboveRightX, int aboveRightY)
    {

        // edge[0..3] = L[3], L[2], L[1], L[0]; edge[4] = P; edge[5..12] = A[0..7] (the RFC's E, extended by A[4..7]).
        for (int i = 0; i < 4; i++)
        {
            edge[3 - i] = plane.At(x0 - 1, y0 + i);
        }

        edge[4] = plane.At(x0 - 1, y0 - 1);
        ReadOnlySpan<byte> above = plane.Row(y0 - 1);
        for (int i = 0; i < 4; i++)
        {
            edge[5 + i] = above[x0 + i];
        }

        // Right-column subblocks below the first use the pixels above-right of the macroblock (section 12.3).
        ReadOnlySpan<byte> aboveRight = useMacroblockAboveRight ? plane.Row(aboveRightY).Slice(aboveRightX, 4) : above.Slice(x0 + 4, 4);
        for (int i = 0; i < 4; i++)
        {
            edge[9 + i] = aboveRight[i];
        }
    }

    /// <summary>A subblock's prediction in <paramref name="mode"/> from its edge, into 16 bytes, row by row.</summary>
    internal static void PredictSubblock(int[] edge, int mode, Span<byte> b)
    {
        static int Avg3(int x, int y, int z) => (x + y + y + z + 2) >> 2;
        static int Avg2(int x, int y) => (x + y + 1) >> 1;
        int A(int i) => edge[5 + i];        // A[i], A[-1] = P
        int L(int i) => edge[3 - i];        // L[i], L[-1] = P
        int E(int i) => edge[i];            // E[i]

        switch (mode)
        {
            case BDcPred:
            {
                int v = 4;
                for (int i = 0; i < 4; i++)
                {
                    v += A(i) + L(i);
                }

                b.Fill((byte)(v >> 3));
                break;
            }

            case BTmPred:
                for (int r = 0; r < 4; r++)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        b[(r * 4) + c] = Clamp(L(r) + A(c) - A(-1));
                    }
                }

                break;

            case BVePred:
                for (int c = 0; c < 4; c++)
                {
                    byte v = (byte)Avg3(A(c - 1), A(c), A(c + 1));
                    b[c] = b[4 + c] = b[8 + c] = b[12 + c] = v;
                }

                break;

            case BHePred:
            {
                b.Slice(12, 4).Fill((byte)Avg3(L(2), L(3), L(3)));
                for (int r = 0; r < 3; r++)
                {
                    b.Slice(r * 4, 4).Fill((byte)Avg3(L(r - 1), L(r), L(r + 1)));
                }

                break;
            }

            case BLdPred:
                b[0] = (byte)Avg3(A(0), A(1), A(2));
                {
                    byte value = (byte)Avg3(A(1), A(2), A(3));
                    b[1] = value;
                    b[4] = value;
                }
                {
                    byte value = (byte)Avg3(A(2), A(3), A(4));
                    b[2] = value;
                    b[5] = value;
                    b[8] = value;
                }
                {
                    byte value = (byte)Avg3(A(3), A(4), A(5));
                    b[3] = value;
                    b[6] = value;
                    b[9] = value;
                    b[12] = value;
                }
                {
                    byte value = (byte)Avg3(A(4), A(5), A(6));
                    b[7] = value;
                    b[10] = value;
                    b[13] = value;
                }
                {
                    byte value = (byte)Avg3(A(5), A(6), A(7));
                    b[11] = value;
                    b[14] = value;
                }
                b[15] = (byte)Avg3(A(6), A(7), A(7));
                break;

            case BRdPred:
                b[12] = (byte)Avg3(E(0), E(1), E(2));
                {
                    byte value = (byte)Avg3(E(1), E(2), E(3));
                    b[13] = value;
                    b[8] = value;
                }
                {
                    byte value = (byte)Avg3(E(2), E(3), E(4));
                    b[14] = value;
                    b[9] = value;
                    b[4] = value;
                }
                {
                    byte value = (byte)Avg3(E(3), E(4), E(5));
                    b[15] = value;
                    b[10] = value;
                    b[5] = value;
                    b[0] = value;
                }
                {
                    byte value = (byte)Avg3(E(4), E(5), E(6));
                    b[11] = value;
                    b[6] = value;
                    b[1] = value;
                }
                {
                    byte value = (byte)Avg3(E(5), E(6), E(7));
                    b[7] = value;
                    b[2] = value;
                }
                b[3] = (byte)Avg3(E(6), E(7), E(8));
                break;

            case BVrPred:
                b[12] = (byte)Avg3(E(1), E(2), E(3));
                b[8] = (byte)Avg3(E(2), E(3), E(4));
                {
                    byte value = (byte)Avg3(E(3), E(4), E(5));
                    b[13] = value;
                    b[4] = value;
                }
                {
                    byte value = (byte)Avg2(E(4), E(5));
                    b[9] = value;
                    b[0] = value;
                }
                {
                    byte value = (byte)Avg3(E(4), E(5), E(6));
                    b[14] = value;
                    b[5] = value;
                }
                {
                    byte value = (byte)Avg2(E(5), E(6));
                    b[10] = value;
                    b[1] = value;
                }
                {
                    byte value = (byte)Avg3(E(5), E(6), E(7));
                    b[15] = value;
                    b[6] = value;
                }
                {
                    byte value = (byte)Avg2(E(6), E(7));
                    b[11] = value;
                    b[2] = value;
                }
                b[7] = (byte)Avg3(E(6), E(7), E(8));
                b[3] = (byte)Avg2(E(7), E(8));
                break;

            case BVlPred:
                b[0] = (byte)Avg2(A(0), A(1));
                b[4] = (byte)Avg3(A(0), A(1), A(2));
                {
                    byte value = (byte)Avg2(A(1), A(2));
                    b[8] = value;
                    b[1] = value;
                }
                {
                    byte value = (byte)Avg3(A(1), A(2), A(3));
                    b[5] = value;
                    b[12] = value;
                }
                {
                    byte value = (byte)Avg2(A(2), A(3));
                    b[9] = value;
                    b[2] = value;
                }
                {
                    byte value = (byte)Avg3(A(2), A(3), A(4));
                    b[13] = value;
                    b[6] = value;
                }
                {
                    byte value = (byte)Avg2(A(3), A(4));
                    b[10] = value;
                    b[3] = value;
                }
                {
                    byte value = (byte)Avg3(A(3), A(4), A(5));
                    b[14] = value;
                    b[7] = value;
                }
                b[11] = (byte)Avg3(A(4), A(5), A(6));
                b[15] = (byte)Avg3(A(5), A(6), A(7));
                break;

            case BHdPred:
                b[12] = (byte)Avg2(E(0), E(1));
                b[13] = (byte)Avg3(E(0), E(1), E(2));
                {
                    byte value = (byte)Avg2(E(1), E(2));
                    b[8] = value;
                    b[14] = value;
                }
                {
                    byte value = (byte)Avg3(E(1), E(2), E(3));
                    b[9] = value;
                    b[15] = value;
                }
                {
                    byte value = (byte)Avg2(E(2), E(3));
                    b[10] = value;
                    b[4] = value;
                }
                {
                    byte value = (byte)Avg3(E(2), E(3), E(4));
                    b[11] = value;
                    b[5] = value;
                }
                {
                    byte value = (byte)Avg2(E(3), E(4));
                    b[6] = value;
                    b[0] = value;
                }
                {
                    byte value = (byte)Avg3(E(3), E(4), E(5));
                    b[7] = value;
                    b[1] = value;
                }
                b[2] = (byte)Avg3(E(4), E(5), E(6));
                b[3] = (byte)Avg3(E(5), E(6), E(7));
                break;

            default:
                b[0] = (byte)Avg2(L(0), L(1));
                b[1] = (byte)Avg3(L(0), L(1), L(2));
                {
                    byte value = (byte)Avg2(L(1), L(2));
                    b[2] = value;
                    b[4] = value;
                }
                {
                    byte value = (byte)Avg3(L(1), L(2), L(3));
                    b[3] = value;
                    b[5] = value;
                }
                {
                    byte value = (byte)Avg2(L(2), L(3));
                    b[6] = value;
                    b[8] = value;
                }
                {
                    byte value = (byte)Avg3(L(2), L(3), L(3));
                    b[7] = value;
                    b[9] = value;
                }
                {
                    byte value = (byte)L(3);
                    b[10] = value;
                    b[11] = value;
                    b[12] = value;
                    b[13] = value;
                    b[14] = value;
                    b[15] = value;
                }
                break;
        }
    }

    // Section 14.4: the inverse DCT, added to the prediction and clamped (section 14.5).
    internal static void AddResidual(Plane plane, int x0, int y0, ReadOnlySpan<short> input, bool any)
    {
        if (!any)
        {
            return;
        }

        byte[] data = plane.Data;
        int stride = plane.Stride;
        int at = plane.Index(x0, y0);

        // Only the DC: the same value added to all 16 pixels.
        if (MemoryMarshal.Cast<short, ulong>(input[..16])[1..] is var rest && rest[0] == 0 && rest[1] == 0 && rest[2] == 0
            && (MemoryMarshal.Cast<short, ulong>(input[..16])[0] & ~0xFFFFUL) == 0)
        {
            int dc = (input[0] + 4) >> 3;
            for (int r = 0; r < 4; r++, at += stride)
            {
                data[at] = Clamp(data[at] + dc);
                data[at + 1] = Clamp(data[at + 1] + dc);
                data[at + 2] = Clamp(data[at + 2] + dc);
                data[at + 3] = Clamp(data[at + 3] + dc);
            }

            return;
        }

        const int Cos = 20091;
        const int Sin = 35468;
        Span<int> temp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int i0 = input[i];
            int i4 = input[4 + i];
            int i8 = input[8 + i];
            int i12 = input[12 + i];
            int a1 = i0 + i8;
            int b1 = i0 - i8;
            int c1 = ((i4 * Sin) >> 16) - (i12 + ((i12 * Cos) >> 16));
            int d1 = i4 + ((i4 * Cos) >> 16) + ((i12 * Sin) >> 16);
            temp[i] = a1 + d1;
            temp[12 + i] = a1 - d1;
            temp[4 + i] = b1 + c1;
            temp[8 + i] = b1 - c1;
        }

        for (int i = 0; i < 4; i++, at += stride)
        {
            int t0 = temp[i * 4];
            int t1 = temp[(i * 4) + 1];
            int t2 = temp[(i * 4) + 2];
            int t3 = temp[(i * 4) + 3];
            int a1 = t0 + t2;
            int b1 = t0 - t2;
            int c1 = ((t1 * Sin) >> 16) - (t3 + ((t3 * Cos) >> 16));
            int d1 = t1 + ((t1 * Cos) >> 16) + ((t3 * Sin) >> 16);
            data[at] = Clamp(data[at] + ((a1 + d1 + 4) >> 3));
            data[at + 1] = Clamp(data[at + 1] + ((b1 + c1 + 4) >> 3));
            data[at + 2] = Clamp(data[at + 2] + ((b1 - c1 + 4) >> 3));
            data[at + 3] = Clamp(data[at + 3] + ((a1 - d1 + 4) >> 3));
        }
    }

    internal static void InverseWht(ReadOnlySpan<short> input, Span<short> output)
    {
        Span<int> temp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a1 = input[i] + input[12 + i];
            int b1 = input[4 + i] + input[8 + i];
            int c1 = input[4 + i] - input[8 + i];
            int d1 = input[i] - input[12 + i];
            temp[i] = a1 + b1;
            temp[4 + i] = c1 + d1;
            temp[8 + i] = a1 - b1;
            temp[12 + i] = d1 - c1;
        }

        for (int i = 0; i < 4; i++)
        {
            ReadOnlySpan<int> ip = temp.Slice(i * 4, 4);
            int a1 = ip[0] + ip[3];
            int b1 = ip[1] + ip[2];
            int c1 = ip[1] - ip[2];
            int d1 = ip[0] - ip[3];
            output[(i * 4) + 0] = (short)((a1 + b1 + 3) >> 3);
            output[(i * 4) + 1] = (short)((c1 + d1 + 3) >> 3);
            output[(i * 4) + 2] = (short)((a1 - b1 + 3) >> 3);
            output[(i * 4) + 3] = (short)((d1 - c1 + 3) >> 3);
        }
    }

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);

    // Section 15 and the reference dixie_loopfilter.c: macroblocks in raster order, each its left edge, inner vertical
    // edges, top edge and inner horizontal edges.
    // Stage 3: one row's loop filter (section 15), macroblock by macroblock in the reference order.
    private void FilterRow(int my)
    {
        // A frame level of 0 turns the filter off, whatever the segments say (as libvpx and libwebp do).
        if (_filterLevel == 0)
        {
            return;
        }

        {
            for (int mx = 0; mx < _mbWide; mx++)
            {
                Macroblock mb = _macroblocks[(my * _mbWide) + mx];
                int level = _filterLevel;
                if (_segmentation)
                {
                    level = _segmentAbsolute ? _segmentFilter[mb.Segment] : level + _segmentFilter[mb.Segment];
                }

                level = Math.Clamp(level, 0, 63);
                if (_filterDeltas)
                {
                    level += _referenceDelta[0];
                    if (mb.YMode == BPred)
                    {
                        level += _modeDelta[0];
                    }
                }

                level = Math.Clamp(level, 0, 63);
                if (level == 0)
                {
                    continue;
                }

                int interior = level;
                if (_sharpness > 0)
                {
                    interior >>= _sharpness > 4 ? 2 : 1;
                    interior = Math.Min(interior, 9 - _sharpness);
                }

                interior = Math.Max(interior, 1);
                int hev = (level >= 15 ? 1 : 0) + (level >= 40 ? 1 : 0);
                bool inner = mb.HasCoefficients || mb.YMode == BPred;
                if (_simpleFilter)
                {
                    FilterSimple(mx, my, level, interior, inner);
                }
                else
                {
                    FilterNormal(_y, mx * 16, my * 16, 16, mx, my, level, interior, hev, inner);
                    FilterNormal(_u, mx * 8, my * 8, 8, mx, my, level, interior, hev, inner);
                    FilterNormal(_v, mx * 8, my * 8, 8, mx, my, level, interior, hev, inner);
                }
            }
        }
    }

    private void FilterSimple(int mx, int my, int level, int interior, bool inner)
    {
        int mbLimit = ((level + 2) * 2) + interior;
        int subLimit = (level * 2) + interior;
        int x0 = mx * 16;
        int y0 = my * 16;
        Plane p = _y;
        if (mx > 0)
        {
            SimpleEdge(p, x0, y0, 1, mbLimit);
        }

        if (inner)
        {
            SimpleEdge(p, x0 + 4, y0, 1, subLimit);
            SimpleEdge(p, x0 + 8, y0, 1, subLimit);
            SimpleEdge(p, x0 + 12, y0, 1, subLimit);
        }

        if (my > 0)
        {
            SimpleEdge(p, x0, y0, 0, mbLimit);
        }

        if (inner)
        {
            SimpleEdge(p, x0, y0 + 4, 0, subLimit);
            SimpleEdge(p, x0, y0 + 8, 0, subLimit);
            SimpleEdge(p, x0, y0 + 12, 0, subLimit);
        }
    }

    // An edge of 16 pixels: (dx, dy) = (1, 0) for a vertical edge at x, (0, 1) for a horizontal edge at y.
    private static void SimpleEdge(Plane plane, int x, int y, int dx, int limit)
    {
        int across = dx == 1 ? 1 : plane.Stride;
        int along = dx == 1 ? plane.Stride : 1;
        if (Vector128.IsHardwareAccelerated)
        {
            Vp8Filter.Edge(plane.Data, plane.Index(x, y), across, along, 16, Vp8Filter.Kind.Simple, limit, 0, 0);
            return;
        }

        ScalarSimpleEdge(plane.Data, plane.Index(x, y), across, along, limit);
    }

    /// <summary>The scalar simple filter on 16 points of an edge: the reference the SIMD version is tested against.</summary>
    internal static void ScalarSimpleEdge(byte[] data, int at, int across, int along, int limit)
    {
        for (int i = 0; i < 16; i++)
        {
            Edge edge = new(data, at + (i * along), across);
            if ((Math.Abs(edge[-1] - edge[0]) * 2) + (Math.Abs(edge[-2] - edge[1]) >> 1) <= limit)
            {
                Common(edge, outerTaps: true);
            }
        }
    }

    internal static void FilterNormal(Plane plane, int x0, int y0, int size, int mx, int my, int level, int interior, int hev, bool inner)
    {
        if (mx > 0)
        {
            NormalEdge(plane, x0, y0, 1, size, level + 2, interior, hev, macroblock: true);
        }

        if (inner)
        {
            for (int at = 4; at < size; at += 4)
            {
                NormalEdge(plane, x0 + at, y0, 1, size, level, interior, hev, macroblock: false);
            }
        }

        if (my > 0)
        {
            NormalEdge(plane, x0, y0, 0, size, level + 2, interior, hev, macroblock: true);
        }

        if (inner)
        {
            for (int at = 4; at < size; at += 4)
            {
                NormalEdge(plane, x0, y0 + at, 0, size, level, interior, hev, macroblock: false);
            }
        }
    }

    private static void NormalEdge(Plane plane, int x, int y, int dx, int size, int edgeLimit, int interior, int hevThreshold, bool macroblock)
    {
        int across = dx == 1 ? 1 : plane.Stride;
        int along = dx == 1 ? plane.Stride : 1;
        if (Vector128.IsHardwareAccelerated)
        {
            Vp8Filter.Edge(plane.Data, plane.Index(x, y), across, along, size, macroblock ? Vp8Filter.Kind.Macroblock : Vp8Filter.Kind.Subblock, edgeLimit, interior, hevThreshold);
            return;
        }

        ScalarNormalEdge(plane.Data, plane.Index(x, y), across, along, size, edgeLimit, interior, hevThreshold, macroblock);
    }

    /// <summary>The scalar normal filter on an edge: the reference the SIMD version is tested against.</summary>
    internal static void ScalarNormalEdge(byte[] data, int at, int across, int along, int size, int edgeLimit, int interior, int hevThreshold, bool macroblock)
    {
        for (int i = 0; i < size; i++)
        {
            Edge e = new(data, at + (i * along), across);
            int p3 = e[-4], p2 = e[-3], p1 = e[-2], p0 = e[-1], q0 = e[0], q1 = e[1], q2 = e[2], q3 = e[3];
            bool pass = (Math.Abs(p0 - q0) * 2) + (Math.Abs(p1 - q1) >> 1) <= (2 * edgeLimit) + interior
                && Math.Abs(p3 - p2) <= interior && Math.Abs(p2 - p1) <= interior && Math.Abs(p1 - p0) <= interior
                && Math.Abs(q3 - q2) <= interior && Math.Abs(q2 - q1) <= interior && Math.Abs(q1 - q0) <= interior;
            if (!pass)
            {
                continue;
            }

            bool highVariance = Math.Abs(p1 - p0) > hevThreshold || Math.Abs(q1 - q0) > hevThreshold;
            if (!macroblock)
            {
                Common(e, highVariance);
            }
            else if (highVariance)
            {
                Common(e, outerTaps: true);
            }
            else
            {
                int w = SaturateInt8(SaturateInt8(p1 - q1) + (3 * (q0 - p0)));
                int a = ((27 * w) + 63) >> 7;
                e[-1] = Clamp(p0 + a);
                e[0] = Clamp(q0 - a);
                a = ((18 * w) + 63) >> 7;
                e[-2] = Clamp(p1 + a);
                e[1] = Clamp(q1 - a);
                a = ((9 * w) + 63) >> 7;
                e[-3] = Clamp(p2 + a);
                e[2] = Clamp(q2 - a);
            }
        }
    }

    private static void Common(Edge e, bool outerTaps)
    {
        int p1 = e[-2], p0 = e[-1], q0 = e[0], q1 = e[1];
        int a = 3 * (q0 - p0);
        if (outerTaps)
        {
            a += SaturateInt8(p1 - q1);
        }

        a = SaturateInt8(a);
        int f1 = Math.Min(a + 4, 127) >> 3;
        int f2 = Math.Min(a + 3, 127) >> 3;
        e[-1] = Clamp(p0 + f2);
        e[0] = Clamp(q0 - f1);
        if (!outerTaps)
        {
            a = (f1 + 1) >> 1;
            e[-2] = Clamp(p1 + a);
            e[1] = Clamp(q1 - a);
        }
    }

    private static int SaturateInt8(int x) => x < -128 ? -128 : x > 127 ? 127 : x;

    /// <summary>
    /// The four row stages (parse, reconstruct, filter, convert) run as a pipeline: each stage handles its rows in
    /// order, one thread at a time, and any thread may take whichever stage has a row ready.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A row can be reconstructed once parsed; filtered once the row below is reconstructed (filtering changes the
    /// bottom of the row above it, which the row below predicts from unfiltered); converted once the row below is
    /// filtered (that changes this row's bottom three lines). Each stage's own order is the sequential order, so the
    /// output is the same whatever the number of threads.
    /// </para>
    /// <para>
    /// The calling thread works too, so the decode finishes even if no helper ever gets a thread from the pool; helpers
    /// only shorten it. A failure in any stage stops every worker and is rethrown on the calling thread.
    /// </para>
    /// </remarks>
    private sealed class RowPipeline(Vp8Decoder decoder, Action<Vp8Decoder, int> convertRow)
    {
        private const int Stages = 4;
        private readonly int[] _next = new int[Stages];
        private readonly int[] _busy = new int[Stages];
        private readonly int _rows = decoder._mbHigh;
        private int _activeHelpers;
        private volatile ExceptionDispatchInfo? _failure;

        public void Run()
        {
            long pixels = (long)decoder.Width * decoder.Height;
            int helpers = pixels < Parallelism.Threshold ? 0 : Math.Min(Parallelism.Degree - 1, Stages - 1);
            for (int i = 0; i < helpers; i++)
            {
                ThreadPool.UnsafeQueueUserWorkItem(static pipeline => pipeline.Help(), this, preferLocal: false);
            }

            Work();

            // Helpers that started finish their row (or see the failure) and leave; none starts a stage after this.
            var spin = default(SpinWait);
            while (Volatile.Read(ref _activeHelpers) > 0)
            {
                spin.SpinOnce(sleep1Threshold: -1);
            }

            _failure?.Throw();
        }

        private void Help()
        {
            Interlocked.Increment(ref _activeHelpers);
            try
            {
                Work();
            }
            finally
            {
                Interlocked.Decrement(ref _activeHelpers);
            }
        }

        private void Work()
        {
            var spin = default(SpinWait);
            while (_failure is null && Volatile.Read(ref _next[Stages - 1]) < _rows)
            {
                // Later stages first: they free ring slots and finish rows.
                bool worked = false;
                for (int stage = Stages - 1; stage >= 0 && !worked; stage--)
                {
                    if (Interlocked.CompareExchange(ref _busy[stage], 1, 0) != 0)
                    {
                        continue;
                    }

                    try
                    {
                        int row = _next[stage];
                        if (Ready(stage, row))
                        {
                            RunStage(stage, row);
                            Volatile.Write(ref _next[stage], row + 1);
                            worked = true;
                        }
                    }
#pragma warning disable CA1031 // Any failure is captured here and rethrown, unchanged, on the calling thread by Run.
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        _failure ??= ExceptionDispatchInfo.Capture(ex);
                    }
                    finally
                    {
                        Volatile.Write(ref _busy[stage], 0);
                    }
                }

                if (worked)
                {
                    spin.Reset();
                }
                else
                {
                    spin.SpinOnce(sleep1Threshold: -1);
                }
            }
        }

        private bool Ready(int stage, int row)
        {
            if (row >= _rows)
            {
                return false;
            }

            int done = stage == 0 ? 0 : Volatile.Read(ref _next[stage - 1]);
            return stage switch
            {
                0 => row < Volatile.Read(ref _next[1]) + Ring,
                1 => row < done,
                _ => row < done - 1 || done == _rows,
            };
        }

        private void RunStage(int stage, int row)
        {
            switch (stage)
            {
                case 0:
                    decoder.ParseRow(row);
                    break;
                case 1:
                    decoder.ReconstructRow(row);
                    break;
                case 2:
                    decoder.FilterRow(row);
                    break;
                default:
                    convertRow(decoder, row);
                    break;
            }
        }
    }

    /// <summary>A plane with a one-pixel border above and to the left (127 above, 129 left) and spare columns right.</summary>
    internal sealed class Plane
    {
        private readonly byte[] _data;
        private readonly int _stride;

        public Plane(int width, int height, int extra)
        {
            _stride = width + 1 + extra;
            _data = new byte[(long)_stride * (height + 1)];
            _data.AsSpan(0, _stride).Fill(127);
            for (int y = 1; y <= height; y++)
            {
                _data[y * _stride] = 129;
            }
        }

        /// <summary>Row y (from -1), starting at x = 0; index -1 is the left border.</summary>
        public Span<byte> Row(int y) => _data.AsSpan(((y + 1) * _stride) + 1, _stride - 1);

        public byte At(int x, int y) => _data[((y + 1) * _stride) + x + 1];

        public byte[] Data => _data;

        public int Stride => _stride;

        public int Index(int x, int y) => ((y + 1) * _stride) + x + 1;
    }

    /// <summary>
    /// The pixels across one point of an edge: index 0 is the first after it, -1 the last before it; the step is 1 across
    /// a vertical edge and the stride across a horizontal one.
    /// </summary>
    private readonly ref struct Edge(byte[] data, int at, int step)
    {
        public byte this[int i]
        {
            get => data[at + (step * i)];
            set => data[at + (step * i)] = value;
        }
    }
}

