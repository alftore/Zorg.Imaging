using System;
using System.Buffers.Binary;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.ExceptionServices;
using System.Threading;
using static Zorg.Imaging.Codecs.Webp.Vp8Decoder;

namespace Zorg.Imaging.Codecs.Webp;

/// <summary>Writes a VP8 key frame, as a lossy WebP holds one (RFC 6386), from Y, U and V planes.</summary>
/// <remarks>
/// <para>
/// Macroblocks are coded in raster order against the reconstruction the decoder will make, so predictions match: each
/// is tried with the four whole-block luma modes and with the ten subblock modes on every 4 x 4 block, and its chroma
/// with the four chroma modes, keeping whichever costs least as distortion plus lambda times bits. Bits are counted
/// with the token probabilities the frame will use, from the contexts of the blocks already chosen.
/// </para>
/// <para>
/// The forward transforms are the inverses of the decoder's (section 14.3 and 14.4): with M the matrix of the inverse
/// DCT's one-dimensional pass, the inverse is X = M C M^T / 8, so C = M^T X M / 2; the WHT likewise. The token
/// probabilities are then fitted to the frame's own counts where an update pays for itself (section 13.4).
/// </para>
/// </remarks>
internal sealed class Vp8Encoder
{
    // Bits to code a bool of probability p (that it's 0) as 0 or as 1, in 1/256 of a bit.
    private static readonly int[] Cost0 = BuildCosts(zero: true);
    private static readonly int[] Cost1 = BuildCosts(zero: false);

    private readonly int _width;
    private readonly int _height;
    private readonly int _mbWide;
    private readonly int _mbHigh;
    private readonly byte[] _sourceY;
    private readonly byte[] _sourceU;
    private readonly byte[] _sourceV;
    private readonly int _strideY;
    private readonly int _strideUv;
    private readonly Plane _y;
    private readonly Plane _u;
    private readonly Plane _v;
    private readonly byte[] _probabilities = Vp8Tables.DefaultCoefficientProbabilities.ToArray();
    private readonly Macroblock[] _macroblocks;

    // Every macroblock's 25 blocks of levels and 16 subblock modes, in one array each rather than two small ones a
    // macroblock (about 40 MB of garbage for a 12-megapixel picture).
    private readonly short[] _levels;
    private readonly byte[] _subModes;
    private readonly byte[] _aboveNonZero;
    private readonly byte[] _aboveModes;
    private readonly int _quantiser;
    private readonly int _y1Dc;
    private readonly int _y1Ac;
    private readonly int _y2Dc;
    private readonly int _y2Ac;
    private readonly int _uvDc;
    private readonly int _uvAc;
    private readonly long _lambda;
    private readonly long _lambdaSubblock;
    private readonly int _wholeCandidates;
    private readonly int _subblockCandidates;
    private bool _subblocks = true;
    private int[] _progress = [];
    private volatile bool _failed;

    private Vp8Encoder(byte[] y, byte[] u, byte[] v, int width, int height, int quantiser, int effort)
    {
        // How many modes, ranked by prediction error alone, get the full test of transform, tokens and reconstruction.
        _wholeCandidates = effort >= 5 ? 4 : effort >= 2 ? 2 : 1;
        _subblockCandidates = effort switch { 0 => 1, 1 => 1, 2 => 2, 3 => 3, 4 => 3, 5 => 5, _ => 10 };

        _width = width;
        _height = height;
        _mbWide = (width + 15) / 16;
        _mbHigh = (height + 15) / 16;
        _strideY = _mbWide * 16;
        _strideUv = _mbWide * 8;
        _sourceY = y;
        _sourceU = u;
        _sourceV = v;
        _y = new Plane(_mbWide * 16, _mbHigh * 16, extra: 4);
        _u = new Plane(_mbWide * 8, _mbHigh * 8, extra: 0);
        _v = new Plane(_mbWide * 8, _mbHigh * 8, extra: 0);
        _macroblocks = new Macroblock[_mbWide * _mbHigh];
        _levels = new short[(long)_mbWide * _mbHigh * 25 * 16];
        _subModes = new byte[(long)_mbWide * _mbHigh * 16];
        _aboveNonZero = new byte[_mbWide * 9];
        _aboveModes = new byte[_mbWide * 4];

        // The decoder's factors for the quantiser (section 14.1), with no per-component deltas.
        _quantiser = quantiser;
        _y1Dc = Dc(quantiser);
        _y1Ac = Ac(quantiser);
        _y2Dc = Dc(quantiser) * 2;
        _y2Ac = Math.Max(Ac(quantiser) * 155 / 100, 8);
        _uvDc = Math.Min(Dc(quantiser), 132);
        _uvAc = Ac(quantiser);

        // Distortion is in squared pixel steps and bits in 1/256 of a bit, so lambda carries 1/256 too.
        _lambda = Math.Max((long)_y1Ac * _y1Ac * 3 / 16, 1);
        _lambdaSubblock = Math.Max((long)_y1Ac * _y1Ac * 3 / 16, 1);
    }

    /// <summary>Codes Y, U and V planes (luma <paramref name="width"/> x <paramref name="height"/>, chroma half that,
    /// both padded to whole macroblocks) as a VP8 frame: the payload of a <c>VP8 </c> chunk.</summary>
    /// <exception cref="NotSupportedException">The macroblock modes don't fit the 512 KB the first partition can hold,
    /// even without subblock modes: only for pictures of tens of megapixels.</exception>
    public static byte[] Encode(byte[] y, byte[] u, byte[] v, int width, int height, int quantiser, int effort)
    {
        // Subblock modes take most of the first partition; a very large picture is tried again without them.
        foreach (bool subblocks in new[] { effort > 0, false })
        {
            var encoder = new Vp8Encoder(y, u, v, width, height, quantiser, effort) { _subblocks = subblocks };
            encoder.EncodeRows();
            byte[]? frame = encoder.Write();
            if (frame is not null)
            {
                return frame;
            }
        }

        throw new NotSupportedException(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"A {width} x {height} picture has too many macroblocks for lossy WebP's first partition (512 KB); save it smaller or as lossless WebP."));
    }

    // Quality to quantiser index, between these points: fitted so a quality gives about cwebp's file size for the same
    // quality on photos (docs/performance.md).
    private static ReadOnlySpan<byte> QualityPoints => [1, 10, 20, 30, 40, 50, 60, 70, 75, 80, 85, 90, 95, 100];

    private static ReadOnlySpan<byte> QuantiserPoints => [90, 66, 55, 44, 38, 33, 30, 26, 24, 18, 13, 9, 4, 0];

    /// <summary>The quantiser index (0 to 127) for a quality of 1 to 100.</summary>
    public static int QuantiserFor(int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        ReadOnlySpan<byte> qualities = QualityPoints;
        ReadOnlySpan<byte> quantisers = QuantiserPoints;
        int i = 1;
        while (qualities[i] < quality)
        {
            i++;
        }

        int span = qualities[i] - qualities[i - 1];
        int along = quality - qualities[i - 1];
        return ((quantisers[i - 1] * (span - along)) + (quantisers[i] * along) + (span / 2)) / span;
    }

    private static int Dc(int index) => Vp8Tables.DcQuantiser[Math.Clamp(index, 0, 127)];

    private static int Ac(int index) => Vp8Tables.AcQuantiser[Math.Clamp(index, 0, 127)];

    private static int[] BuildCosts(bool zero)
    {
        int[] costs = new int[256];
        for (int p = 0; p < 256; p++)
        {
            double probability = Math.Clamp(p, 1, 255) / 256.0;
            costs[p] = (int)Math.Round(-Math.Log2(zero ? probability : 1 - probability) * 256);
        }

        return costs;
    }

    private static int BoolCost(int probability, bool value) => value ? Cost1[probability] : Cost0[probability];

    // ---- Macroblock decisions ----

    // Macroblock rows as a wavefront: a row may code a macroblock once the row above has coded the one above-right of it,
    // which is all its predictions and contexts read. Each row is claimed in order by a thread that then codes it to
    // the end, so a row waits only on rows already being coded, and the caller codes rows too: if no helper starts,
    // it codes them all. The result is the same on any number of threads.
    private void EncodeRows()
    {
        _progress = new int[_mbHigh];
        int next = -1;
        int helpers = Processing.Parallelism.Bands((long)_mbWide * _mbHigh * 256, _mbHigh);
        ExceptionDispatchInfo? failure = null;
        Processing.Parallelism.For(helpers, _ =>
        {
            try
            {
                int row;
                while (!_failed && (row = Interlocked.Increment(ref next)) < _mbHigh)
                {
                    EncodeRow(row);
                }
            }
#pragma warning disable CA1031 // Any failure is passed to the caller as it was thrown, after the other threads stop.
            catch (Exception e)
#pragma warning restore CA1031
            {
                _failed = true;
                Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(e), null);
            }
        });

        failure?.Throw();
    }

    private void EncodeRow(int my)
    {
        Span<byte> leftNonZero = stackalloc byte[9];
        Span<byte> leftModes = stackalloc byte[4];
        leftNonZero.Clear();
        leftModes.Clear();
        int[] edge = new int[13];
        for (int mx = 0; mx < _mbWide; mx++)
        {
            if (my > 0)
            {
                var spin = default(SpinWait);
                int needed = Math.Min(mx + 2, _mbWide);
                while (Volatile.Read(ref _progress[my - 1]) < needed)
                {
                    if (_failed)
                    {
                        return;
                    }

                    spin.SpinOnce(sleep1Threshold: -1);
                }
            }

            ref Macroblock mb = ref _macroblocks[(my * _mbWide) + mx];
            mb.Index = (my * _mbWide) + mx;
            Span<byte> aboveNonZero = _aboveNonZero.AsSpan(mx * 9, 9);
            Span<byte> aboveModes = _aboveModes.AsSpan(mx * 4, 4);
            ChooseLuma(ref mb, mx, my, edge, leftNonZero, aboveNonZero, leftModes, aboveModes);
            ChooseChroma(ref mb, mx, my, leftNonZero, aboveNonZero);
            mb.Skip = mb.NonZero == 0;

            // The above-right pixels of the next row's last macroblock repeat this row's last pixel, as the decoder
            // does: set before the next row can read them.
            if (mx == _mbWide - 1)
            {
                Span<byte> bottom = _y.Row((my * 16) + 15);
                bottom.Slice(_mbWide * 16, 4).Fill(bottom[(_mbWide * 16) - 1]);
            }

            Volatile.Write(ref _progress[my], mx + 1);
        }
    }

    // Whole-block luma modes against subblock modes. The plane holds the chosen reconstruction afterwards.
    private void ChooseLuma(ref Macroblock mb, int mx, int my, int[] edge, Span<byte> leftNonZero, Span<byte> aboveNonZero, Span<byte> leftModes, Span<byte> aboveModes)
    {
        int x0 = mx * 16;
        int y0 = my * 16;
        Span<short> levels = stackalloc short[17 * 16];
        Span<byte> contextLeft = stackalloc byte[9];
        Span<byte> contextAbove = stackalloc byte[9];

        // The whole-block modes ranked by prediction error and mode bits, then the best few tested in full.
        Span<long> rank = stackalloc long[4];
        for (int mode = DcPred; mode <= TmPred; mode++)
        {
            PredictBlock(_y, x0, y0, 16, mode, mx == 0, my == 0);
            rank[mode] = Rank(PredictionError(_sourceY, (y0 * _strideY) + x0, _strideY, _y, x0, y0, 16) + (_lambda * ModeCost(mode)), mode);
        }

        rank.Sort();
        long best = long.MaxValue;
        int bestMode = DcPred;
        for (int k = 0; k < _wholeCandidates; k++)
        {
            int mode = (int)(rank[k] & 0xF);
            PredictBlock(_y, x0, y0, 16, mode, mx == 0, my == 0);
            leftNonZero.CopyTo(contextLeft);
            aboveNonZero.CopyTo(contextAbove);
            long score = WholeLuma(x0, y0, levels, contextLeft, contextAbove, out _) + (_lambda * ModeCost(mode));
            if (score < best)
            {
                best = score;
                bestMode = mode;
            }
        }

        // The subblock modes, each 4 x 4 block chosen in order on the reconstruction of those before it.
        Span<byte> subModes = stackalloc byte[16];
        Span<byte> subLeft = stackalloc byte[4];
        Span<byte> subAbove = stackalloc byte[4];
        leftNonZero[..4].CopyTo(subLeft);
        aboveNonZero[..4].CopyTo(subAbove);
        Span<short> subLevels = stackalloc short[16 * 16];
        Span<byte> reconstruction = stackalloc byte[16];
        Span<short> blockLevels = stackalloc short[16];
        Span<long> subRank = stackalloc long[10];
        Span<byte> original = stackalloc byte[16];
        Span<byte> predictions = stackalloc byte[10 * 16];
        Span<byte> trialPredictions = stackalloc byte[4 * 16];
        Span<short> trialLevels = stackalloc short[4 * 16];
        Span<byte> trialReconstructions = stackalloc byte[4 * 16];
        Span<long> trialDistortions = stackalloc long[4];
        long subTotal = _subblocks ? _lambda * ModeCost(BPred) : long.MaxValue;
        for (int i = 0; i < 16 && subTotal < best; i++)
        {
            int bx = x0 + ((i & 3) * 4);
            int by = y0 + ((i >> 2) * 4);
            int above = i < 4 ? aboveModes[i] : subModes[i - 4];
            int left = (i & 3) == 0 ? leftModes[i >> 2] : subModes[i - 1];
            ReadOnlySpan<int> modeCosts = SubblockModeCosts.AsSpan(((above * 10) + left) * 10, 10);
            int context = subLeft[i >> 2] + subAbove[i & 3];
            long bestSub = long.MaxValue;
            int bestSubMode = BDcPred;
            bool bestNonZero = false;
            // The edge is the same for every mode, so it is read once; each mode predicts into its own 16 bytes, which the
            // full trials below copy into the plane instead of predicting again.
            FillSubblockEdge(_y, edge, bx, by, (i & 3) == 3 && i > 3, x0 + 16, y0 - 1);
            Gather4x4(_sourceY, (by * _strideY) + bx, _strideY, original);
            for (int mode = BDcPred; mode <= BHuPred; mode++)
            {
                Span<byte> prediction = predictions.Slice(mode * 16, 16);
                PredictSubblock(edge, mode, prediction);
                long error = (long)SquaredError(Vector128.Create<byte>(original), Vector128.Create<byte>(prediction)) << 8;
                subRank[mode] = Rank(error + (_lambdaSubblock * modeCosts[mode]), mode);
            }

            subRank.Sort();

            // The best few candidates in rank order, four at a time through the vector trial (TrialFour).
            for (int group = 0; group < _subblockCandidates; group += 4)
            {
                int count = Math.Min(4, _subblockCandidates - group);
                for (int k = 0; k < 4; k++)
                {
                    int mode = (int)(subRank[group + Math.Min(k, count - 1)] & 0xF);
                    predictions.Slice(mode * 16, 16).CopyTo(trialPredictions.Slice(k * 16, 16));
                }

                TrialFour(original, trialPredictions, _y1Dc, _y1Ac, trialLevels, trialReconstructions, trialDistortions);
                for (int k = 0; k < count; k++)
                {
                    int mode = (int)(subRank[group + k] & 0xF);
                    ReadOnlySpan<short> candidate = trialLevels.Slice(k * 16, 16);
                    int bits = BlockCost(candidate, 3, 0, context) + modeCosts[mode];
                    long score = trialDistortions[k] + (_lambdaSubblock * bits);
                    if (score < bestSub)
                    {
                        bestSub = score;
                        bestSubMode = mode;
                        bestNonZero = HasLevels(candidate, 0);
                        candidate.CopyTo(subLevels.Slice(i * 16, 16));
                        trialReconstructions.Slice(k * 16, 16).CopyTo(reconstruction);
                    }
                }
            }

            Restore(_y, bx, by, 4, reconstruction);
            subModes[i] = (byte)bestSubMode;
            subLeft[i >> 2] = subAbove[i & 3] = (byte)(bestNonZero ? 1 : 0);
            subTotal += bestSub;
        }

        if (subTotal < best)
        {
            mb.YMode = BPred;
            subModes.CopyTo(SubModes(mb));
            subLevels.CopyTo(Levels(mb).Slice(0, 16 * 16));
            for (int i = 0; i < 16; i++)
            {
                if (HasLevels(Levels(mb).Slice(i * 16, 16), 0))
                {
                    mb.NonZero |= 1u << i;
                }
            }

            subLeft.CopyTo(leftNonZero[..4]);
            subAbove.CopyTo(aboveNonZero[..4]);
            subModes.Slice(12, 4).CopyTo(aboveModes);
            for (int r = 0; r < 4; r++)
            {
                leftModes[r] = subModes[(r * 4) + 3];
            }

            return;
        }

        // Back to the best whole-block mode: predict and reconstruct it again.
        mb.YMode = bestMode;
        PredictBlock(_y, x0, y0, 16, bestMode, mx == 0, my == 0);
        WholeLuma(x0, y0, levels, leftNonZero, aboveNonZero, out uint nonZeroBits);
        levels[..(16 * 16)].CopyTo(Levels(mb).Slice(0, 16 * 16));
        levels.Slice(16 * 16, 16).CopyTo(Levels(mb).Slice(24 * 16, 16));
        mb.NonZero = nonZeroBits;
        byte implied = bestMode switch { VPred => BVePred, HPred => BHePred, TmPred => BTmPred, _ => BDcPred };
        aboveModes.Fill(implied);
        leftModes.Fill(implied);
    }

    // One whole-block luma candidate, predicted in the plane: transform, quantise, reconstruct in place, and score.
    // levels gets the 16 Y blocks then the Y2 block; the contexts are updated as the tokens would leave them.
    private long WholeLuma(int x0, int y0, Span<short> levels, Span<byte> left, Span<byte> above, out uint nonZeroBits)
    {
        // The sixteen blocks' predictions are all in the plane already, so the blocks are transformed four at a time:
        // each row of four blocks in the lanes. Only the token costs depend on order, and they are counted after.
        Span<Vector128<int>> coefficients = stackalloc Vector128<int>[4 * 16];
        Span<short> dcs = stackalloc short[16];
        Span<int> xs = stackalloc int[4];
        Span<int> ys = stackalloc int[4];
        nonZeroBits = 0;
        for (int g = 0; g < 4; g++)
        {
            for (int l = 0; l < 4; l++)
            {
                xs[l] = x0 + (l * 4);
                ys[l] = y0 + (g * 4);
            }

            Span<Vector128<int>> e = coefficients.Slice(g * 16, 16);
            ResidualLanes(_sourceY, _strideY, _y, xs, ys, e);
            ForwardDctLanes(e);
            for (int l = 0; l < 4; l++)
            {
                dcs[(g * 4) + l] = (short)e[0].GetElement(l);
            }
        }

        Span<short> y2 = stackalloc short[16];
        ForwardWht(dcs, y2);
        Span<short> y2Levels = levels.Slice(16 * 16, 16);
        Span<short> y2Dequantised = stackalloc short[16];
        Quantise(y2, 0, _y2Dc, _y2Ac, y2Levels, y2Dequantised);
        int bits = BlockCost(y2Levels, 1, 0, left[8] + above[8]);
        bool y2NonZero = HasLevels(y2Levels, 0);
        left[8] = above[8] = (byte)(y2NonZero ? 1 : 0);
        if (y2NonZero)
        {
            nonZeroBits |= 1u << 24;
        }

        Span<short> reconstructedDcs = stackalloc short[16];
        InverseWht(y2Dequantised, reconstructedDcs);
        Span<Vector128<int>> blockLevels = stackalloc Vector128<int>[16];
        Span<Vector128<int>> dequantised = stackalloc Vector128<int>[16];
        Span<Vector128<int>> residual = stackalloc Vector128<int>[16];
        long distortion = 0;
        for (int g = 0; g < 4; g++)
        {
            for (int l = 0; l < 4; l++)
            {
                xs[l] = x0 + (l * 4);
                ys[l] = y0 + (g * 4);
            }

            QuantiseLanes(coefficients.Slice(g * 16, 16), 1, _y1Dc, _y1Ac, blockLevels, dequantised);
            LevelsToBlocks(blockLevels, levels.Slice(g * 64, 64));
            dequantised[0] = Vector128.Create(reconstructedDcs[g * 4], reconstructedDcs[(g * 4) + 1], reconstructedDcs[(g * 4) + 2], reconstructedDcs[(g * 4) + 3]);
            InverseDctLanes(dequantised, residual);
            distortion += ReconstructLanes(_sourceY, _strideY, _y, xs, ys, residual);
        }

        for (int i = 0; i < 16; i++)
        {
            Span<short> block = levels.Slice(i * 16, 16);
            int context = left[i >> 2] + above[i & 3];
            bits += BlockCost(block, 0, 1, context);
            bool nonZero = HasLevels(block, 1);
            left[i >> 2] = above[i & 3] = (byte)(nonZero ? 1 : 0);
            if (nonZero)
            {
                nonZeroBits |= 1u << i;
            }
        }

        return distortion + (_lambda * bits);
    }

    // One 4 x 4 block predicted in the plane: transform, quantise, reconstruct in place; its distortion and token bits.
    private (long Distortion, int Bits, bool NonZero) Subblock(int bx, int by, int type, int first, int context, int dc, int ac, Span<short> levels)
    {
        Span<short> coefficients = stackalloc short[16];
        Span<short> dequantised = stackalloc short[16];
        ForwardDct(_sourceY, (by * _strideY) + bx, _strideY, _y, bx, by, coefficients);
        Quantise(coefficients, first, dc, ac, levels, dequantised);
        int bits = BlockCost(levels, type, first, context);
        AddResidual(_y, bx, by, dequantised, any: true);
        return (Distortion(_sourceY, (by * _strideY) + bx, _strideY, _y, bx, by), bits, HasLevels(levels, first));
    }

    // Four candidate predictions of one 4 x 4 luma block (Y type, DC included) at once, one in each lane: the residual,
    // forward DCT, quantisation and dequantisation, inverse DCT, reconstruction and squared error, in the same integer
    // steps as ForwardDct, Quantise, AddResidual and Distortion, so each lane's results equal theirs. Levels come out in
    // scan order, sixteen per candidate; reconstructions as sixteen bytes per candidate, row by row.
    private static void TrialFour(ReadOnlySpan<byte> original, ReadOnlySpan<byte> predictions, int dc, int ac, Span<short> levelsOut, Span<byte> reconstructions, Span<long> distortions)
    {
        Span<Vector128<int>> e = stackalloc Vector128<int>[16];
        for (int i = 0; i < 16; i++)
        {
            int o = original[i];
            e[i] = Vector128.Create(o - predictions[i], o - predictions[16 + i], o - predictions[32 + i], o - predictions[48 + i]);
        }

        ForwardDctLanes(e);
        Span<Vector128<int>> levels = stackalloc Vector128<int>[16];
        Span<Vector128<int>> dequantised = stackalloc Vector128<int>[16];
        QuantiseLanes(e, 0, dc, ac, levels, dequantised);
        LevelsToBlocks(levels, levelsOut);
        Span<Vector128<int>> residual = e;
        InverseDctLanes(dequantised, residual);

        // Squared errors fit 32 bits: sixteen of at most 255^2.
        Vector128<int> sum = Vector128<int>.Zero;
        for (int at = 0; at < 16; at++)
        {
            Vector128<int> prediction = Vector128.Create(predictions[at], predictions[16 + at], predictions[32 + at], predictions[48 + at]);
            Vector128<int> pixel = Vector128.Min(Vector128.Max(prediction + residual[at], Vector128<int>.Zero), Vector128.Create(255));
            Vector128<int> error = Vector128.Create((int)original[at]) - pixel;
            sum += error * error;
            reconstructions[at] = (byte)pixel.GetElement(0);
            reconstructions[16 + at] = (byte)pixel.GetElement(1);
            reconstructions[32 + at] = (byte)pixel.GetElement(2);
            reconstructions[48 + at] = (byte)pixel.GetElement(3);
        }

        for (int k = 0; k < 4; k++)
        {
            distortions[k] = (long)sum.GetElement(k) << 8;
        }
    }

    // ---- Four blocks in the lanes of 128-bit vectors ----
    // Element i (row-major in the block) of four blocks, one block in each lane. Each helper follows its scalar
    // counterpart's integer steps exactly, so every lane's result equals the scalar result for that block.

    // ForwardDct: residuals in, coefficients out (as the scalar code's shorts), in place.
    private static void ForwardDctLanes(Span<Vector128<int>> e)
    {
        Vector128<int> c5352 = Vector128.Create(5352);
        Vector128<int> c2217 = Vector128.Create(2217);
        Span<Vector128<int>> temp = stackalloc Vector128<int>[16];
        for (int r = 0; r < 4; r++)
        {
            Vector128<int> d0 = e[r * 4], d1 = e[(r * 4) + 1], d2 = e[(r * 4) + 2], d3 = e[(r * 4) + 3];
            Vector128<int> a0 = d0 + d3, a1 = d1 + d2, a2 = d1 - d2, a3 = d0 - d3;
            temp[r * 4] = (a0 + a1) * 8;
            temp[(r * 4) + 2] = (a0 - a1) * 8;
            temp[(r * 4) + 1] = Vector128.ShiftRightArithmetic((a3 * c5352) + (a2 * c2217) + Vector128.Create(256), 9);
            temp[(r * 4) + 3] = Vector128.ShiftRightArithmetic((a3 * c2217) - (a2 * c5352) + Vector128.Create(256), 9);
        }

        for (int c = 0; c < 4; c++)
        {
            Vector128<int> a0 = temp[c] + temp[12 + c], a1 = temp[4 + c] + temp[8 + c], a2 = temp[4 + c] - temp[8 + c], a3 = temp[c] - temp[12 + c];
            e[c] = Short(Vector128.ShiftRightArithmetic(a0 + a1 + Vector128.Create(8), 4));
            e[8 + c] = Short(Vector128.ShiftRightArithmetic(a0 - a1 + Vector128.Create(8), 4));
            e[4 + c] = Short(Vector128.ShiftRightArithmetic((a3 * c5352) + (a2 * c2217) + Vector128.Create(32768), 16));
            e[12 + c] = Short(Vector128.ShiftRightArithmetic((a3 * c2217) - (a2 * c5352) + Vector128.Create(32768), 16));
        }
    }

    // Quantise: levels and dequantised values in natural order, with the 16-bit wrap of the scalar casts; from a first
    // of 1 the DC is left at 0 in both.
    private static void QuantiseLanes(ReadOnlySpan<Vector128<int>> coefficients, int first, int dc, int ac, Span<Vector128<int>> levels, Span<Vector128<int>> dequantised)
    {
        Vector128<int> top = Vector128.Create(2048);
        for (int n = 0; n < 16; n++)
        {
            if (n < first)
            {
                levels[n] = Vector128<int>.Zero;
                dequantised[n] = Vector128<int>.Zero;
                continue;
            }

            int q = n == 0 ? dc : ac;
            (uint multiplier, int shift) = Magic[q];
            Vector128<int> value = coefficients[n];
            Vector128<int> magnitude = Vector128.Abs(value) + Vector128.Create(n == 0 ? q / 2 : (q * 3) / 8);
            Vector128<int> level = Vector128.Min(Vector128.ShiftRightLogical(magnitude.AsUInt32() * Vector128.Create(multiplier), shift).AsInt32(), top);
            level = Vector128.ConditionalSelect(Vector128.LessThan(value, Vector128<int>.Zero), -level, level);
            levels[n] = level;
            dequantised[n] = Short(level * q);
        }
    }

    // The four blocks' levels in scan order, sixteen each, one block after another.
    private static void LevelsToBlocks(ReadOnlySpan<Vector128<int>> levels, Span<short> output)
    {
        ReadOnlySpan<byte> zigzag = ZigZag;
        for (int c = 0; c < 16; c++)
        {
            Vector128<int> level = levels[zigzag[c]];
            output[c] = (short)level.GetElement(0);
            output[16 + c] = (short)level.GetElement(1);
            output[32 + c] = (short)level.GetElement(2);
            output[48 + c] = (short)level.GetElement(3);
        }
    }

    // AddResidual's transform (section 14.4): dequantised coefficients in, the sixteen values added to the prediction out.
    private static void InverseDctLanes(ReadOnlySpan<Vector128<int>> dequantised, Span<Vector128<int>> residual)
    {
        Vector128<int> cos = Vector128.Create(20091);
        Vector128<int> sin = Vector128.Create(35468);
        Span<Vector128<int>> pass = stackalloc Vector128<int>[16];
        for (int i = 0; i < 4; i++)
        {
            Vector128<int> i0 = dequantised[i], i4 = dequantised[4 + i], i8 = dequantised[8 + i], i12 = dequantised[12 + i];
            Vector128<int> a1 = i0 + i8;
            Vector128<int> b1 = i0 - i8;
            Vector128<int> c1 = Vector128.ShiftRightArithmetic(i4 * sin, 16) - (i12 + Vector128.ShiftRightArithmetic(i12 * cos, 16));
            Vector128<int> d1 = i4 + Vector128.ShiftRightArithmetic(i4 * cos, 16) + Vector128.ShiftRightArithmetic(i12 * sin, 16);
            pass[i] = a1 + d1;
            pass[12 + i] = a1 - d1;
            pass[4 + i] = b1 + c1;
            pass[8 + i] = b1 - c1;
        }

        Vector128<int> four = Vector128.Create(4);
        for (int i = 0; i < 4; i++)
        {
            Vector128<int> t0 = pass[i * 4], t1 = pass[(i * 4) + 1], t2 = pass[(i * 4) + 2], t3 = pass[(i * 4) + 3];
            Vector128<int> a1 = t0 + t2;
            Vector128<int> b1 = t0 - t2;
            Vector128<int> c1 = Vector128.ShiftRightArithmetic(t1 * sin, 16) - (t3 + Vector128.ShiftRightArithmetic(t3 * cos, 16));
            Vector128<int> d1 = t1 + Vector128.ShiftRightArithmetic(t1 * cos, 16) + Vector128.ShiftRightArithmetic(t3 * sin, 16);
            residual[i * 4] = Vector128.ShiftRightArithmetic(a1 + d1 + four, 3);
            residual[(i * 4) + 1] = Vector128.ShiftRightArithmetic(b1 + c1 + four, 3);
            residual[(i * 4) + 2] = Vector128.ShiftRightArithmetic(b1 - c1 + four, 3);
            residual[(i * 4) + 3] = Vector128.ShiftRightArithmetic(a1 - d1 + four, 3);
        }
    }

    // Four blocks of a plane, each at its own place: the residual against the source, in lanes.
    private static void ResidualLanes(byte[] source, int strideSource, Plane plane, ReadOnlySpan<int> xs, ReadOnlySpan<int> ys, Span<Vector128<int>> e)
    {
        for (int r = 0; r < 4; r++)
        {
            ReadOnlySpan<byte> p0 = plane.Row(ys[0] + r).Slice(xs[0], 4);
            ReadOnlySpan<byte> p1 = plane.Row(ys[1] + r).Slice(xs[1], 4);
            ReadOnlySpan<byte> p2 = plane.Row(ys[2] + r).Slice(xs[2], 4);
            ReadOnlySpan<byte> p3 = plane.Row(ys[3] + r).Slice(xs[3], 4);
            int s0 = ((ys[0] + r) * strideSource) + xs[0], s1 = ((ys[1] + r) * strideSource) + xs[1];
            int s2 = ((ys[2] + r) * strideSource) + xs[2], s3 = ((ys[3] + r) * strideSource) + xs[3];
            for (int c = 0; c < 4; c++)
            {
                e[(r * 4) + c] = Vector128.Create(source[s0 + c] - p0[c], source[s1 + c] - p1[c], source[s2 + c] - p2[c], source[s3 + c] - p3[c]);
            }
        }
    }

    // Adds the residuals to the four predictions in the plane, clamped, as AddResidual does; returns the squared error
    // against the source over the four blocks.
    private static long ReconstructLanes(byte[] source, int strideSource, Plane plane, ReadOnlySpan<int> xs, ReadOnlySpan<int> ys, ReadOnlySpan<Vector128<int>> residual)
    {
        Vector128<int> sum = Vector128<int>.Zero;
        Vector128<int> top = Vector128.Create(255);
        for (int r = 0; r < 4; r++)
        {
            Span<byte> p0 = plane.Row(ys[0] + r).Slice(xs[0], 4);
            Span<byte> p1 = plane.Row(ys[1] + r).Slice(xs[1], 4);
            Span<byte> p2 = plane.Row(ys[2] + r).Slice(xs[2], 4);
            Span<byte> p3 = plane.Row(ys[3] + r).Slice(xs[3], 4);
            int s0 = ((ys[0] + r) * strideSource) + xs[0], s1 = ((ys[1] + r) * strideSource) + xs[1];
            int s2 = ((ys[2] + r) * strideSource) + xs[2], s3 = ((ys[3] + r) * strideSource) + xs[3];
            for (int c = 0; c < 4; c++)
            {
                Vector128<int> prediction = Vector128.Create(p0[c], p1[c], p2[c], p3[c]);
                Vector128<int> pixel = Vector128.Min(Vector128.Max(prediction + residual[(r * 4) + c], Vector128<int>.Zero), top);
                Vector128<int> error = Vector128.Create(source[s0 + c], source[s1 + c], source[s2 + c], source[s3 + c]) - pixel;
                sum += error * error;
                p0[c] = (byte)pixel.GetElement(0);
                p1[c] = (byte)pixel.GetElement(1);
                p2[c] = (byte)pixel.GetElement(2);
                p3[c] = (byte)pixel.GetElement(3);
            }
        }

        return (long)Vector128.Sum(sum) << 8;
    }

    // A value as the scalar code's (short) cast leaves it: the low 16 bits, sign-extended.
    private static Vector128<int> Short(Vector128<int> value) => Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(value, 16), 16);

    private void ChooseChroma(ref Macroblock mb, int mx, int my, Span<byte> leftNonZero, Span<byte> aboveNonZero)
    {
        int cx = mx * 8;
        int cy = my * 8;
        Span<short> levels = stackalloc short[8 * 16];
        Span<byte> contextLeft = stackalloc byte[9];
        Span<byte> contextAbove = stackalloc byte[9];
        Span<long> rank = stackalloc long[4];
        for (int mode = DcPred; mode <= TmPred; mode++)
        {
            PredictBlock(_u, cx, cy, 8, mode, mx == 0, my == 0);
            PredictBlock(_v, cx, cy, 8, mode, mx == 0, my == 0);
            long error = PredictionError(_sourceU, (cy * _strideUv) + cx, _strideUv, _u, cx, cy, 8)
                + PredictionError(_sourceV, (cy * _strideUv) + cx, _strideUv, _v, cx, cy, 8);
            rank[mode] = Rank(error + (_lambda * ChromaModeCosts[mode]), mode);
        }

        rank.Sort();
        long best = long.MaxValue;
        int bestMode = DcPred;
        for (int k = 0; k < _wholeCandidates; k++)
        {
            int mode = (int)(rank[k] & 0xF);
            PredictBlock(_u, cx, cy, 8, mode, mx == 0, my == 0);
            PredictBlock(_v, cx, cy, 8, mode, mx == 0, my == 0);
            leftNonZero.CopyTo(contextLeft);
            aboveNonZero.CopyTo(contextAbove);
            long score = WholeChroma(cx, cy, levels, contextLeft, contextAbove, out _) + (_lambda * ChromaModeCosts[mode]);
            if (score < best)
            {
                best = score;
                bestMode = mode;
            }
        }

        mb.UvMode = bestMode;
        PredictBlock(_u, cx, cy, 8, bestMode, mx == 0, my == 0);
        PredictBlock(_v, cx, cy, 8, bestMode, mx == 0, my == 0);
        WholeChroma(cx, cy, levels, leftNonZero, aboveNonZero, out uint nonZeroBits);
        levels.CopyTo(Levels(mb).Slice(16 * 16, 8 * 16));
        mb.NonZero |= nonZeroBits << 16;
    }

    private long WholeChroma(int cx, int cy, Span<short> levels, Span<byte> left, Span<byte> above, out uint nonZeroBits)
    {
        // The four U blocks in the lanes, then the four V blocks; token costs after, in block order.
        Span<Vector128<int>> e = stackalloc Vector128<int>[16];
        Span<Vector128<int>> blockLevels = stackalloc Vector128<int>[16];
        Span<Vector128<int>> dequantised = stackalloc Vector128<int>[16];
        Span<int> xs = stackalloc int[4];
        Span<int> ys = stackalloc int[4];
        for (int l = 0; l < 4; l++)
        {
            xs[l] = cx + ((l & 1) * 4);
            ys[l] = cy + ((l >> 1) * 4);
        }

        nonZeroBits = 0;
        long distortion = 0;
        for (int g = 0; g < 2; g++)
        {
            Plane plane = g == 0 ? _u : _v;
            byte[] source = g == 0 ? _sourceU : _sourceV;
            ResidualLanes(source, _strideUv, plane, xs, ys, e);
            ForwardDctLanes(e);
            QuantiseLanes(e, 0, _uvDc, _uvAc, blockLevels, dequantised);
            LevelsToBlocks(blockLevels, levels.Slice(g * 64, 64));
            InverseDctLanes(dequantised, e);
            distortion += ReconstructLanes(source, _strideUv, plane, xs, ys, e);
        }

        int bits = 0;
        for (int i = 0; i < 8; i++)
        {
            int slot = i < 4 ? 4 : 6;
            int x = i & 1;
            int y = (i >> 1) & 1;
            Span<short> block = levels.Slice(i * 16, 16);
            bits += BlockCost(block, 2, 0, left[slot + y] + above[slot + x]);
            bool nonZero = HasLevels(block, 0);
            left[slot + y] = above[slot + x] = (byte)(nonZero ? 1 : 0);
            if (nonZero)
            {
                nonZeroBits |= 1u << i;
            }
        }

        return distortion + (_lambda * bits);
    }

    // The whole-block luma and chroma mode costs, fixed for key frames like the subblock ones.
    private static readonly int[] LumaModeCosts = [.. Enumerable.Range(0, 5).Select(mode => TreeCost(YModeTree, YModeProbabilities, mode))];

    private static readonly int[] ChromaModeCosts = [.. Enumerable.Range(0, 4).Select(mode => TreeCost(UvModeTree, UvModeProbabilities, mode))];

    private static int ModeCost(int yMode) => LumaModeCosts[yMode];

    private static void Save(Plane plane, int x0, int y0, int size, Span<byte> into)
    {
        for (int r = 0; r < size; r++)
        {
            plane.Row(y0 + r).Slice(x0, size).CopyTo(into.Slice(r * size, size));
        }
    }

    private static void Restore(Plane plane, int x0, int y0, int size, ReadOnlySpan<byte> from)
    {
        for (int r = 0; r < size; r++)
        {
            from.Slice(r * size, size).CopyTo(plane.Row(y0 + r).Slice(x0, size));
        }
    }

    // A score and a mode in one sortable value: the score above, the mode in the low 4 bits, so ties go to the lower mode.
    private static long Rank(long score, int mode) => (score << 4) | (long)mode;

    // The squared error of the prediction now in the plane, over a square, in the same units as Distortion.
    private static long PredictionError(byte[] source, int at, int stride, Plane plane, int x0, int y0, int size)
    {
        long sum = 0;
        if (size == 16)
        {
            for (int r = 0; r < 16; r++)
            {
                sum += SquaredError(Vector128.Create<byte>(source.AsSpan(at + (r * stride), 16)), Vector128.Create<byte>(plane.Row(y0 + r).Slice(x0, 16)));
            }
        }
        else if (size == 8)
        {
            // Two rows of 8 in each vector.
            for (int r = 0; r < 8; r += 2)
            {
                Vector128<byte> original = Vector128.Create(
                    MemoryMarshal.Read<ulong>(source.AsSpan(at + (r * stride), 8)),
                    MemoryMarshal.Read<ulong>(source.AsSpan(at + ((r + 1) * stride), 8))).AsByte();
                Vector128<byte> predicted = Vector128.Create(
                    MemoryMarshal.Read<ulong>(plane.Row(y0 + r).Slice(x0, 8)),
                    MemoryMarshal.Read<ulong>(plane.Row(y0 + r + 1).Slice(x0, 8))).AsByte();
                sum += SquaredError(original, predicted);
            }
        }
        else
        {
            Span<byte> original = stackalloc byte[16];
            Span<byte> predicted = stackalloc byte[16];
            Gather4x4(source, at, stride, original);
            Gather4x4(plane, x0, y0, predicted);
            sum = SquaredError(Vector128.Create<byte>(original), Vector128.Create<byte>(predicted));
        }

        return sum << 8;
    }

    private static long Distortion(byte[] source, int at, int stride, Plane plane, int x0, int y0)
    {
        Span<byte> original = stackalloc byte[16];
        Span<byte> reconstructed = stackalloc byte[16];
        Gather4x4(source, at, stride, original);
        Gather4x4(plane, x0, y0, reconstructed);
        return (long)SquaredError(Vector128.Create<byte>(original), Vector128.Create<byte>(reconstructed)) << 8;
    }

    // A 4 x 4 block into 16 bytes, row by row.
    private static void Gather4x4(byte[] source, int at, int stride, Span<byte> into)
    {
        for (int r = 0; r < 4; r++)
        {
            source.AsSpan(at + (r * stride), 4).CopyTo(into.Slice(r * 4, 4));
        }
    }

    private static void Gather4x4(Plane plane, int x0, int y0, Span<byte> into)
    {
        for (int r = 0; r < 4; r++)
        {
            plane.Row(y0 + r).Slice(x0, 4).CopyTo(into.Slice(r * 4, 4));
        }
    }

    // The sum of squared differences of 16 bytes: widened to 16 bits, where the difference of two bytes fits, then
    // squared and summed at 32 bits.
    private static int SquaredError(Vector128<byte> a, Vector128<byte> b)
    {
        Vector128<short> low = (Vector128.WidenLower(a) - Vector128.WidenLower(b)).AsInt16();
        Vector128<short> high = (Vector128.WidenUpper(a) - Vector128.WidenUpper(b)).AsInt16();
        Vector128<int> sum = (Vector128.WidenLower(low) * Vector128.WidenLower(low)) + (Vector128.WidenUpper(low) * Vector128.WidenUpper(low))
            + (Vector128.WidenLower(high) * Vector128.WidenLower(high)) + (Vector128.WidenUpper(high) * Vector128.WidenUpper(high));
        return Vector128.Sum(sum);
    }

    // ---- Transforms and quantisation ----

    // The forward DCT of source less the prediction now in the plane (see the class remarks for the derivation): a
    // one-dimensional pass on rows, scaled by 8, then on columns; cos and sin of pi/8 times the square root of 2 are
    // 5352/4096 and 2217/4096.
    internal static void ForwardDct(byte[] source, int at, int stride, Plane plane, int x0, int y0, Span<short> output)
    {
        Span<int> temp = stackalloc int[16];
        for (int r = 0; r < 4; r++)
        {
            ReadOnlySpan<byte> prediction = plane.Row(y0 + r).Slice(x0, 4);
            int d0 = source[at + (r * stride)] - prediction[0];
            int d1 = source[at + (r * stride) + 1] - prediction[1];
            int d2 = source[at + (r * stride) + 2] - prediction[2];
            int d3 = source[at + (r * stride) + 3] - prediction[3];
            int a0 = d0 + d3;
            int a1 = d1 + d2;
            int a2 = d1 - d2;
            int a3 = d0 - d3;
            temp[(r * 4) + 0] = (a0 + a1) * 8;
            temp[(r * 4) + 2] = (a0 - a1) * 8;
            temp[(r * 4) + 1] = ((a3 * 5352) + (a2 * 2217) + 256) >> 9;
            temp[(r * 4) + 3] = ((a3 * 2217) - (a2 * 5352) + 256) >> 9;
        }

        for (int c = 0; c < 4; c++)
        {
            int a0 = temp[c] + temp[12 + c];
            int a1 = temp[4 + c] + temp[8 + c];
            int a2 = temp[4 + c] - temp[8 + c];
            int a3 = temp[c] - temp[12 + c];
            output[c] = (short)((a0 + a1 + 8) >> 4);
            output[8 + c] = (short)((a0 - a1 + 8) >> 4);
            output[4 + c] = (short)(((a3 * 5352) + (a2 * 2217) + 32768) >> 16);
            output[12 + c] = (short)(((a3 * 2217) - (a2 * 5352) + 32768) >> 16);
        }
    }

    // The forward WHT of the 16 luma DCs: the decoder's inverse is X = W C W / 8 with W its symmetric Hadamard matrix,
    // so C = W X W / 2.
    internal static void ForwardWht(ReadOnlySpan<short> input, Span<short> output)
    {
        Span<int> temp = stackalloc int[16];
        for (int r = 0; r < 4; r++)
        {
            int x0 = input[r * 4];
            int x1 = input[(r * 4) + 1];
            int x2 = input[(r * 4) + 2];
            int x3 = input[(r * 4) + 3];
            temp[(r * 4) + 0] = x0 + x1 + x2 + x3;
            temp[(r * 4) + 1] = x0 + x1 - x2 - x3;
            temp[(r * 4) + 2] = x0 - x1 - x2 + x3;
            temp[(r * 4) + 3] = x0 - x1 + x2 - x3;
        }

        for (int c = 0; c < 4; c++)
        {
            int x0 = temp[c];
            int x1 = temp[4 + c];
            int x2 = temp[8 + c];
            int x3 = temp[12 + c];
            output[c] = (short)((x0 + x1 + x2 + x3 + 1) >> 1);
            output[4 + c] = (short)((x0 + x1 - x2 - x3 + 1) >> 1);
            output[8 + c] = (short)((x0 - x1 - x2 + x3 + 1) >> 1);
            output[12 + c] = (short)((x0 - x1 + x2 - x3 + 1) >> 1);
        }
    }

    // Levels in scan order from first, and the dequantised coefficients in natural order (as the decoder rebuilds them).
    // Magnitudes round down after adding a bias of 3/8 of a step, or half a step for the DC, which suits the cost of
    // larger levels; at most 2048.
    // floor(x / q) as (x * (2^32 / q + 1)) >> 32, exact for x and q below 2^16, which the values here are: a multiply
    // where a division cost tens of cycles for each coefficient. Used for the DC.
    private static readonly ulong[] Reciprocals = BuildReciprocals();

    // For the AC coefficients, which share one quantiser and go through vectors: floor(x / q) as (x * m) >> s with
    // s = 15 + ceil(log2 q) and m = ceil(2^s / q), exact for x below 2^15 (Granlund and Montgomery), and x * m stays
    // below 2^31. The largest magnitude here, a Y2 coefficient plus its bias, is under 16,700.
    private static readonly (uint Multiplier, int Shift)[] Magic = BuildMagic();

    private static ulong[] BuildReciprocals()
    {
        ulong[] reciprocals = new ulong[4096];
        for (int q = 1; q < reciprocals.Length; q++)
        {
            reciprocals[q] = (1UL << 32) / (ulong)q + 1;
        }

        return reciprocals;
    }

    private static (uint, int)[] BuildMagic()
    {
        var magic = new (uint, int)[4096];
        for (int q = 1; q < magic.Length; q++)
        {
            int l = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(q - 1));
            int shift = 15 + l;
            magic[q] = ((uint)(((1UL << shift) + (ulong)q - 1) / (ulong)q), shift);
        }

        return magic;
    }

    // Levels in scan order from first, and the dequantised coefficients in natural order (as the decoder rebuilds them).
    // Magnitudes round down after adding a bias of 3/8 of a step, or half a step for the DC, which suits the cost of
    // larger levels; at most 2048. The AC coefficients are quantised together as vectors in natural order, without a
    // branch for each; the DC on its own.
    private static void Quantise(ReadOnlySpan<short> coefficients, int first, int dc, int ac, Span<short> levels, Span<short> dequantised)
    {
        (uint multiplier, int shift) = Magic[ac];
        Vector128<int> bias = Vector128.Create((ac * 3) / 8);
        Vector128<uint> m = Vector128.Create(multiplier);
        Vector128<int> top = Vector128.Create(2048);
        Vector128<int> step = Vector128.Create(ac);
        Span<short> natural = stackalloc short[16];
        ReadOnlySpan<Vector128<short>> input = MemoryMarshal.Cast<short, Vector128<short>>(coefficients[..16]);
        Span<Vector128<short>> levelVectors = MemoryMarshal.Cast<short, Vector128<short>>(natural);
        Span<Vector128<short>> outVectors = MemoryMarshal.Cast<short, Vector128<short>>(dequantised[..16]);
        for (int half = 0; half < 2; half++)
        {
            Vector128<short> v = input[half];
            Vector128<int> low = Quantise(Vector128.WidenLower(v), bias, m, shift, top);
            Vector128<int> high = Quantise(Vector128.WidenUpper(v), bias, m, shift, top);
            levelVectors[half] = Vector128.Narrow(low, high);
            outVectors[half] = Vector128.Narrow(low * step, high * step);
        }

        // The DC: its own quantiser and bias, or none from a first of 1 (the Y blocks whose DC goes to Y2).
        if (first == 0)
        {
            int value = coefficients[0];
            int level = (int)Math.Min(((ulong)(Math.Abs(value) + (dc / 2)) * Reciprocals[dc]) >> 32, 2048);
            natural[0] = (short)(value < 0 ? -level : level);
            dequantised[0] = (short)(natural[0] * dc);
        }
        else
        {
            natural[0] = 0;
            dequantised[0] = 0;
        }

        ReadOnlySpan<byte> zigzag = ZigZag;
        for (int c = 0; c < 16; c++)
        {
            levels[c] = natural[zigzag[c]];
        }
    }

    private static Vector128<int> Quantise(Vector128<int> value, Vector128<int> bias, Vector128<uint> multiplier, int shift, Vector128<int> top)
    {
        Vector128<int> magnitude = Vector128.Abs(value) + bias;
        Vector128<int> level = Vector128.Min(Vector128.ShiftRightLogical(magnitude.AsUInt32() * multiplier, shift).AsInt32(), top);
        return Vector128.ConditionalSelect(Vector128.LessThan(value, Vector128<int>.Zero), -level, level);
    }

    private static bool HasLevels(ReadOnlySpan<short> levels, int first)
    {
        for (int c = first; c < 16; c++)
        {
            if (levels[c] != 0)
            {
                return true;
            }
        }

        return false;
    }

    // ---- Tokens ----

    private interface ISink
    {
        // A bool coded with a probability; index is the coefficient probability's place in the table, or -1.
        public void Put(int probability, bool value, int index);
    }

    private struct CostSink : ISink
    {
        public int Bits;

        public void Put(int probability, bool value, int index) => Bits += BoolCost(probability, value);
    }

    private readonly struct CountSink(int[] zeros, int[] ones) : ISink
    {
        public void Put(int probability, bool value, int index)
        {
            if (index >= 0)
            {
                (value ? ones : zeros)[index]++;
            }
        }
    }

    private readonly struct WriteSink(Vp8BoolEncoder bits) : ISink
    {
        public void Put(int probability, bool value, int index) => bits.WriteBool(probability, value);
    }

    // The bits a block's tokens take, with the default token probabilities, which are the ones in force while modes are
    // chosen (the fitted ones come after, for writing). The same sum as walking the tokens (Tokens with a CostSink), with
    // the walk for each level done once ahead: a level's cost in each of the 96 contexts (type, band, previous), up to
    // 66, and for larger ones the category 6 prefix in each context plus its eleven extra bits.
    private static int BlockCost(ReadOnlySpan<short> levels, int type, int first, int context)
    {
        int last = -1;
        for (int c = 15; c >= first; c--)
        {
            if (levels[c] != 0)
            {
                last = c;
                break;
            }
        }

        ReadOnlySpan<byte> bands = Bands;
        ReadOnlySpan<byte> probabilities = Vp8Tables.DefaultCoefficientProbabilities;
        int table = type * 8 * 3;
        int slot = table + (bands[first] * 3) + context;
        int bits = BoolCost(probabilities[slot * 11], last >= 0);
        if (last < 0)
        {
            return bits;
        }

        int[] zeroCost = ZeroCost;
        int[] levelCost = LevelCost;
        for (int c = first; ;)
        {
            int value = Math.Abs((int)levels[c]);
            if (value == 0)
            {
                bits += zeroCost[slot];
                c++;
                slot = table + (bands[c] * 3);
                continue;
            }

            bits += value < Category6 ? levelCost[(slot * Category6) + value] : Category6Prefix[slot] + Category6Extra[value - Category6];
            bits += BoolCost(128, levels[c] < 0);
            if (++c == 16)
            {
                return bits;
            }

            slot = table + (bands[c] * 3) + (value == 1 ? 1 : 2);
            bits += BoolCost(probabilities[slot * 11], c <= last);
            if (c > last)
            {
                return bits;
            }
        }
    }

    private const int Category6 = 67;

    private static readonly int[] ZeroCost = BuildZeroCost();
    private static readonly int[] LevelCost = BuildLevelCost(out Category6Prefix);
    private static readonly int[] Category6Prefix;
    private static readonly int[] Category6Extra = BuildCategory6Extra();

    private static int[] BuildZeroCost()
    {
        ReadOnlySpan<byte> p = Vp8Tables.DefaultCoefficientProbabilities;
        int[] costs = new int[96];
        for (int slot = 0; slot < 96; slot++)
        {
            costs[slot] = BoolCost(p[(slot * 11) + 1], false);
        }

        return costs;
    }

    // For each context and each level 1 to 66: the "not zero" bit, the value tree and its extra bits (not the sign), as
    // Tokens puts them; and for category 6, the prefix alone.
    private static int[] BuildLevelCost(out int[] category6Prefix)
    {
        ReadOnlySpan<byte> p = Vp8Tables.DefaultCoefficientProbabilities;
        int[] costs = new int[96 * Category6];
        category6Prefix = new int[96];
        for (int slot = 0; slot < 96; slot++)
        {
            int at = slot * 11;
            for (int value = 1; value <= Category6; value++)
            {
                var sink = new CostSink();
                sink.Put(p[at + 1], true, -1);
                if (value == 1)
                {
                    sink.Put(p[at + 2], false, -1);
                }
                else
                {
                    sink.Put(p[at + 2], true, -1);
                    if (value <= 4)
                    {
                        sink.Put(p[at + 3], false, -1);
                        sink.Put(p[at + 4], value != 2, -1);
                        if (value != 2)
                        {
                            sink.Put(p[at + 5], value == 4, -1);
                        }
                    }
                    else if (value <= 10)
                    {
                        sink.Put(p[at + 3], true, -1);
                        sink.Put(p[at + 6], false, -1);
                        sink.Put(p[at + 7], value > 6, -1);
                        if (value <= 6)
                        {
                            sink.Put(159, value == 6, -1);
                        }
                        else
                        {
                            sink.Put(165, value - 7 >= 2, -1);
                            sink.Put(145, ((value - 7) & 1) != 0, -1);
                        }
                    }
                    else
                    {
                        sink.Put(p[at + 3], true, -1);
                        sink.Put(p[at + 6], true, -1);
                        bool high = value > 34;
                        bool odd = high ? value > 66 : value > 18;
                        sink.Put(p[at + 8], high, -1);
                        sink.Put(p[at + (high ? 10 : 9)], odd, -1);
                        if (value < Category6)
                        {
                            ReadOnlySpan<byte> extra = high ? Cat5 : (odd ? Cat4 : Cat3);
                            int extraValue = value - (high ? 35 : (odd ? 19 : 11));
                            for (int b = extra.Length - 1; b >= 0; b--)
                            {
                                sink.Put(extra[extra.Length - 1 - b], ((extraValue >> b) & 1) != 0, -1);
                            }
                        }
                    }
                }

                if (value < Category6)
                {
                    costs[(slot * Category6) + value] = sink.Bits;
                }
                else
                {
                    category6Prefix[slot] = sink.Bits;
                }
            }
        }

        return costs;
    }

    private static int[] BuildCategory6Extra()
    {
        ReadOnlySpan<byte> extra = Cat6;
        int[] costs = new int[2048 - Category6 + 1];
        for (int extraValue = 0; extraValue < costs.Length; extraValue++)
        {
            var sink = new CostSink();
            for (int b = extra.Length - 1; b >= 0; b--)
            {
                sink.Put(extra[extra.Length - 1 - b], ((extraValue >> b) & 1) != 0, -1);
            }

            costs[extraValue] = sink.Bits;
        }

        return costs;
    }

    // One block's tokens (section 13.2), as the decoder reads them.
    private void Tokens<TSink>(ref TSink sink, ReadOnlySpan<short> levels, int type, int first, int context)
        where TSink : struct, ISink
    {
        int last = -1;
        for (int c = 15; c >= first; c--)
        {
            if (levels[c] != 0)
            {
                last = c;
                break;
            }
        }

        ReadOnlySpan<byte> bands = Bands;
        int table = type * 8 * 3 * 11;
        int c0 = first;
        int at = table + (((bands[c0] * 3) + context) * 11);
        sink.Put(_probabilities[at], last >= 0, at);
        if (last < 0)
        {
            return;
        }

        for (int c = first; ;)
        {
            int value = Math.Abs((int)levels[c]);
            if (value == 0)
            {
                sink.Put(_probabilities[at + 1], false, at + 1);
                c++;
                at = table + (bands[c] * 3 * 11);
                continue;
            }

            sink.Put(_probabilities[at + 1], true, at + 1);
            int next;
            if (value == 1)
            {
                sink.Put(_probabilities[at + 2], false, at + 2);
                next = 1;
            }
            else
            {
                sink.Put(_probabilities[at + 2], true, at + 2);
                next = 2;
                if (value <= 4)
                {
                    sink.Put(_probabilities[at + 3], false, at + 3);
                    sink.Put(_probabilities[at + 4], value != 2, at + 4);
                    if (value != 2)
                    {
                        sink.Put(_probabilities[at + 5], value == 4, at + 5);
                    }
                }
                else if (value <= 10)
                {
                    sink.Put(_probabilities[at + 3], true, at + 3);
                    sink.Put(_probabilities[at + 6], false, at + 6);
                    sink.Put(_probabilities[at + 7], value > 6, at + 7);
                    if (value <= 6)
                    {
                        sink.Put(159, value == 6, -1);
                    }
                    else
                    {
                        sink.Put(165, value - 7 >= 2, -1);
                        sink.Put(145, ((value - 7) & 1) != 0, -1);
                    }
                }
                else
                {
                    sink.Put(_probabilities[at + 3], true, at + 3);
                    sink.Put(_probabilities[at + 6], true, at + 6);
                    bool high = value > 34;
                    bool odd = high ? value > 66 : value > 18;
                    sink.Put(_probabilities[at + 8], high, at + 8);
                    sink.Put(_probabilities[at + (high ? 10 : 9)], odd, at + (high ? 10 : 9));
                    ReadOnlySpan<byte> extra = high ? (odd ? Cat6 : Cat5) : (odd ? Cat4 : Cat3);
                    int extraValue = value - (high ? (odd ? 67 : 35) : (odd ? 19 : 11));
                    for (int b = extra.Length - 1; b >= 0; b--)
                    {
                        sink.Put(extra[extra.Length - 1 - b], ((extraValue >> b) & 1) != 0, -1);
                    }
                }
            }

            sink.Put(128, levels[c] < 0, -1);
            if (++c == 16)
            {
                return;
            }

            at = table + (((bands[c] * 3) + next) * 11);
            sink.Put(_probabilities[at], c <= last, at);
            if (c > last)
            {
                return;
            }
        }
    }

    // The bits to code a value with a tree (section 8.1); the path is found by walking the tree.
    // The bits of each subblock mode in each context of the modes above and to the left: the probabilities are fixed for
    // key frames, so the tree walks are done once.
    private static readonly int[] SubblockModeCosts = BuildSubblockModeCosts();

    private static int[] BuildSubblockModeCosts()
    {
        int[] costs = new int[10 * 10 * 10];
        for (int context = 0; context < 100; context++)
        {
            ReadOnlySpan<byte> probabilities = Vp8Tables.KeyFrameSubblockModeProbabilities.Slice(context * 9, 9);
            for (int mode = 0; mode < 10; mode++)
            {
                costs[(context * 10) + mode] = TreeCost(SubblockModeTree, probabilities, mode);
            }
        }

        return costs;
    }

    private static int TreeCost(ReadOnlySpan<sbyte> tree, ReadOnlySpan<byte> probabilities, int value)
    {
        var sink = new CostSink();
        WriteTree(ref sink, tree, probabilities, value, 0);
        return sink.Bits;
    }

    private static bool WriteTree<TSink>(ref TSink sink, ReadOnlySpan<sbyte> tree, ReadOnlySpan<byte> probabilities, int value, int node)
        where TSink : struct, ISink
    {
        for (int branch = 0; branch < 2; branch++)
        {
            int child = tree[node + branch];
            bool found = child <= 0 ? -child == value : Contains(tree, child, value);
            if (found)
            {
                sink.Put(probabilities[node >> 1], branch == 1, -1);
                return child <= 0 || WriteTree(ref sink, tree, probabilities, value, child);
            }
        }

        return false;
    }

    private static bool Contains(ReadOnlySpan<sbyte> tree, int node, int value)
    {
        for (int branch = 0; branch < 2; branch++)
        {
            int child = tree[node + branch];
            if (child <= 0 ? -child == value : Contains(tree, child, value))
            {
                return true;
            }
        }

        return false;
    }

    // ---- The frame ----

    // The frame, or null when the first partition is larger than its 19-bit size field can say.
    private byte[]? Write()
    {
        // The token probabilities: count what the frame codes with the defaults, then update each one whose saving pays
        // for the update (section 13.4).
        int[] zeros = new int[_probabilities.Length];
        int[] ones = new int[_probabilities.Length];
        var counter = new CountSink(zeros, ones);
        WalkTokens(ref counter);
        bool[] update = new bool[_probabilities.Length];
        ReadOnlySpan<byte> updateProbabilities = Vp8Tables.CoefficientUpdateProbabilities;
        for (int i = 0; i < _probabilities.Length; i++)
        {
            int total = zeros[i] + ones[i];
            if (total == 0)
            {
                continue;
            }

            int fitted = Math.Clamp((int)(((zeros[i] * 256L) + (total / 2)) / total), 1, 255);
            long before = ((long)zeros[i] * Cost0[_probabilities[i]]) + ((long)ones[i] * Cost1[_probabilities[i]]) + Cost0[updateProbabilities[i]];
            long after = ((long)zeros[i] * Cost0[fitted]) + ((long)ones[i] * Cost1[fitted]) + Cost1[updateProbabilities[i]] + (8 * 256);
            if (after < before)
            {
                update[i] = true;
                _probabilities[i] = (byte)fitted;
            }
        }

        int skipped = 0;
        foreach (Macroblock mb in _macroblocks)
        {
            skipped += mb.Skip ? 1 : 0;
        }

        int skipProbability = Math.Clamp(255 - (int)((skipped * 255L) / _macroblocks.Length), 1, 254);

        var header = new Vp8BoolEncoder(1024 + (_macroblocks.Length * 4));
        header.WriteFlag(false);    // colour space
        header.WriteFlag(false);    // clamping type
        header.WriteFlag(false);    // segmentation
        header.WriteFlag(false);    // normal loop filter
        header.WriteLiteral(FilterLevel(), 6);
        header.WriteLiteral(0, 3);  // sharpness
        header.WriteFlag(false);    // no filter deltas
        header.WriteLiteral(0, 2);  // one token partition
        header.WriteLiteral(_quantiser, 7);
        for (int i = 0; i < 5; i++)
        {
            header.WriteOptionalSigned(0, 4);
        }

        header.WriteFlag(false);    // refresh_entropy_probs
        for (int i = 0; i < _probabilities.Length; i++)
        {
            header.WriteBool(updateProbabilities[i], update[i]);
            if (update[i])
            {
                header.WriteLiteral(_probabilities[i], 8);
            }
        }

        header.WriteFlag(true);     // macroblocks may be skipped
        header.WriteLiteral(skipProbability, 8);

        var modes = new WriteSink(header);
        var aboveModes = new byte[_mbWide * 4];
        Span<byte> leftModes = stackalloc byte[4];
        for (int my = 0; my < _mbHigh; my++)
        {
            leftModes.Clear();
            for (int mx = 0; mx < _mbWide; mx++)
            {
                Macroblock mb = _macroblocks[(my * _mbWide) + mx];
                header.WriteBool(skipProbability, mb.Skip);
                WriteTree(ref modes, YModeTree, YModeProbabilities, mb.YMode, 0);
                Span<byte> above = aboveModes.AsSpan(mx * 4, 4);
                if (mb.YMode == BPred)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        int a = i < 4 ? above[i] : SubModes(mb)[i - 4];
                        int l = (i & 3) == 0 ? leftModes[i >> 2] : SubModes(mb)[i - 1];
                        WriteTree(ref modes, SubblockModeTree, Vp8Tables.KeyFrameSubblockModeProbabilities.Slice(((a * 10) + l) * 9, 9), SubModes(mb)[i], 0);
                    }

                    SubModes(mb).Slice(12, 4).CopyTo(above);
                    for (int r = 0; r < 4; r++)
                    {
                        leftModes[r] = SubModes(mb)[(r * 4) + 3];
                    }
                }
                else
                {
                    byte implied = mb.YMode switch { VPred => BVePred, HPred => BHePred, TmPred => BTmPred, _ => BDcPred };
                    above.Fill(implied);
                    leftModes.Fill(implied);
                }

                WriteTree(ref modes, UvModeTree, UvModeProbabilities, mb.UvMode, 0);
            }
        }

        var tokens = new Vp8BoolEncoder(_width * _height / 2);
        var tokenSink = new WriteSink(tokens);
        WalkTokens(ref tokenSink);

        ReadOnlySpan<byte> first = header.Finish();
        if (first.Length >= 1 << 19)
        {
            return null;
        }

        ReadOnlySpan<byte> second = tokens.Finish();
        byte[] frame = new byte[10 + first.Length + second.Length];
        int tag = (first.Length << 5) | (1 << 4);   // key frame, version 0, shown
        frame[0] = (byte)tag;
        frame[1] = (byte)(tag >> 8);
        frame[2] = (byte)(tag >> 16);
        frame[3] = 0x9D;
        frame[4] = 0x01;
        frame[5] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), (ushort)_width);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), (ushort)_height);
        first.CopyTo(frame.AsSpan(10));
        second.CopyTo(frame.AsSpan(10 + first.Length));
        return frame;
    }

    // Every macroblock's tokens in order, with the contexts the decoder keeps (section 13.3).
    private void WalkTokens<TSink>(ref TSink sink)
        where TSink : struct, ISink
    {
        byte[] above = new byte[_mbWide * 9];
        Span<byte> left = stackalloc byte[9];
        for (int my = 0; my < _mbHigh; my++)
        {
            left.Clear();
            for (int mx = 0; mx < _mbWide; mx++)
            {
                Macroblock mb = _macroblocks[(my * _mbWide) + mx];
                Span<byte> top = above.AsSpan(mx * 9, 9);
                if (mb.Skip)
                {
                    left[..8].Clear();
                    top[..8].Clear();
                    if (mb.YMode != BPred)
                    {
                        left[8] = 0;
                        top[8] = 0;
                    }

                    continue;
                }

                int firstY = 0;
                if (mb.YMode != BPred)
                {
                    ReadOnlySpan<short> y2 = Levels(mb).Slice(24 * 16, 16);
                    Tokens(ref sink, y2, 1, 0, left[8] + top[8]);
                    left[8] = top[8] = (byte)(HasLevels(y2, 0) ? 1 : 0);
                    firstY = 1;
                }

                int yType = firstY == 1 ? 0 : 3;
                for (int i = 0; i < 16; i++)
                {
                    ReadOnlySpan<short> block = Levels(mb).Slice(i * 16, 16);
                    Tokens(ref sink, block, yType, firstY, left[i >> 2] + top[i & 3]);
                    left[i >> 2] = top[i & 3] = (byte)(HasLevels(block, firstY) ? 1 : 0);
                }

                for (int i = 16; i < 24; i++)
                {
                    int slot = i < 20 ? 4 : 6;
                    int x = (i - 16) & 1;
                    int y = ((i - 16) >> 1) & 1;
                    ReadOnlySpan<short> block = Levels(mb).Slice(i * 16, 16);
                    Tokens(ref sink, block, 2, 0, left[slot + y] + top[slot + x]);
                    left[slot + y] = top[slot + x] = (byte)(HasLevels(block, 0) ? 1 : 0);
                }
            }
        }
    }

    // A loop filter level for the quantiser: stronger as the steps grow, as blocking grows with them.
    private int FilterLevel() => Math.Clamp((_y1Ac * 3 / 8) + 2, 0, 63);

    private Span<short> Levels(in Macroblock mb) => _levels.AsSpan(mb.Index * 25 * 16, 25 * 16);

    private Span<byte> SubModes(in Macroblock mb) => _subModes.AsSpan(mb.Index * 16, 16);

    private struct Macroblock
    {
        public int YMode;
        public int UvMode;
        public int Index;
        public uint NonZero;
        public bool Skip;
    }
}
