using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Codecs.Webp;

/// <summary>
/// Converts to YUV 4:2:0 with the chroma chosen for what a decoder shows rather than averaged: a decoder spreads each
/// chroma sample over a 4 x 4 area (libwebp's "fancy" upsampler, which ours follows too), then clips the colour to 0 to
/// 255, so a plain average of each 2 x 2 block blurs colour edges twice and pales thin coloured lines and text against
/// colour. Each round, every pixel's luma is set so that its brightness (BT.601 weights) is the source's given the
/// chroma it will be shown with, the colour error left is turned into the chroma change that would remove it (least
/// squares, leaving out a channel clipped at a limit the source is at as well: yellow text on brown shows best with the
/// yellow's chroma for both and the brown's blue clipped to 0), and each chroma sample moves by twice the mean of those
/// changes, weighted as the upsampler weights it. The upsampler's transpose is the 1-3-3-1 filter at a stride of two,
/// whose weights sum to 64 everywhere, edges included. Our own derivation; cwebp's <c>-sharp_yuv</c> does a similar job.
/// </summary>
internal static class SharpYuv
{
    // The decoder's YUV to RGB (BT.601, studio range), with luma measured as 1.164 x (Y - 16).
    private const float Kr = 1.596f;
    private const float Kgu = -0.391f;
    private const float Kgv = -0.813f;
    private const float Kb = 2.018f;
    private const float Ky = 255f / 219f;
    private const float Wr = 0.299f;
    private const float Wg = 0.587f;
    private const float Wb = 0.114f;

    // Keeps the 2 x 2 solve sound when two of the three channels are clipped.
    private const float Ridge = 0.01f;

    // Twice the mean change a round (the weights sum to 64): two rounds then do about what four plain ones do.
    private const float Step = 2f / 64;

    /// <summary>The planes, padded to whole macroblocks as <see cref="WebpEncoder.PlainYuv"/> pads them.</summary>
    public static (byte[] Y, byte[] U, byte[] V) Convert(uint[] argb, int width, int height, int rounds)
    {
        (byte[] y, byte[] u0, byte[] v0) = WebpEncoder.PlainYuv(argb, width, height);
        int w = (width + 15) / 16 * 16;
        int cw = w / 2;
        int ch = (height + 15) / 16 * 8;

        // Chroma rows a whole number of vectors long; the columns past the picture are worked out and never used.
        int cs = (cw + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count;
        float[] u = new float[cs * ch];
        float[] v = new float[cs * ch];
        float[] nextU = new float[u.Length];
        float[] nextV = new float[v.Length];
        var picture = new Picture(argb, width, height, cw, cs, ch);
        int bands = Parallelism.Bands((long)w * ch * 2, ch / 8);
        Parallelism.For(bands, band =>
        {
            for (int row = ch * band / bands; row < ch * (band + 1) / bands; row++)
            {
                for (int c = 0; c < cw; c++)
                {
                    u[(row * cs) + c] = u0[(row * cw) + c] - 128;
                    v[(row * cs) + c] = v0[(row * cw) + c] - 128;
                }
            }
        });

        for (int round = 0; round < rounds; round++)
        {
            float[] cu = u;
            float[] cv = v;
            float[] nu = nextU;
            float[] nv = nextV;
            Parallelism.For(bands, band => picture.Round(cu, cv, nu, nv, ch * band / bands, ch * (band + 1) / bands));
            (u, nextU) = (nextU, u);
            (v, nextV) = (nextV, v);
        }

        Parallelism.For(bands, band =>
        {
            var rows = new Rows(cs);
            int[] even = new int[cs];
            int[] odd = new int[cs];
            for (int row = 2 * (ch * band / bands); row < 2 * (ch * (band + 1) / bands); row++)
            {
                picture.Row(u, v, row, rows, errors: false);
                ToByte(rows.LumaEven, even);
                ToByte(rows.LumaOdd, odd);
                Span<byte> line = y.AsSpan(row * w, w);
                for (int c = 0; c < cw; c++)
                {
                    line[2 * c] = (byte)even[c];
                    line[(2 * c) + 1] = (byte)odd[c];
                }
            }

            for (int row = ch * band / bands; row < ch * (band + 1) / bands; row++)
            {
                for (int c = 0; c < cw; c++)
                {
                    u0[(row * cw) + c] = (byte)Math.Clamp((int)(u[(row * cs) + c] + 128.5f), 0, 255);
                    v0[(row * cw) + c] = (byte)Math.Clamp((int)(v[(row * cs) + c] + 128.5f), 0, 255);
                }
            }
        });

        return (y, u0, v0);

        // Luma back to Y, rounded half up.
        static void ToByte(float[] luma, int[] bytes)
        {
            var most = new Vector<float>(255.5f);
            for (int c = 0; c < bytes.Length; c += Vector<float>.Count)
            {
                var value = Vector.MinNative(Vector.MaxNative((new Vector<float>(luma, c) * (1 / Ky)) + new Vector<float>(16.5f), Vector<float>.Zero), most);
                Vector.ConvertToInt32(value).CopyTo(bytes, c);
            }
        }
    }

    private sealed class Picture(uint[] argb, int width, int height, int cw, int cs, int ch)
    {
        // One round for chroma rows first to last: the pixel rows they spread to (one more each side) are worked out
        // again here, so bands share nothing and the result is the same however many there are.
        public void Round(float[] u, float[] v, float[] nextU, float[] nextV, int first, int last)
        {
            var rows = new Rows(cs);
            float[] sumU = new float[(last - first) * cs];
            float[] sumV = new float[(last - first) * cs];
            int top = Math.Max((2 * first) - 1, 0);
            int bottom = Math.Min(2 * last, (2 * ch) - 1);
            for (int row = top; row <= bottom; row++)
            {
                Row(u, v, row, rows, errors: true);
                int near = row >> 1;
                int far = Math.Clamp(near + ((row & 1) == 0 ? -1 : 1), 0, ch - 1);
                Add(near, 3);
                Add(far, 1);

                void Add(int c, float weight)
                {
                    if (c < first || c >= last)
                    {
                        return;
                    }

                    int at = (c - first) * cs;
                    for (int x = 0; x < cs; x += Vector<float>.Count)
                    {
                        (new Vector<float>(sumU, at + x) + (weight * new Vector<float>(rows.ErrorU, x))).CopyTo(sumU, at + x);
                        (new Vector<float>(sumV, at + x) + (weight * new Vector<float>(rows.ErrorV, x))).CopyTo(sumV, at + x);
                    }
                }
            }

            var low = new Vector<float>(-128);
            var high = new Vector<float>(127);
            for (int c = first; c < last; c++)
            {
                for (int x = 0; x < cs; x += Vector<float>.Count)
                {
                    int i = (c * cs) + x;
                    int s = ((c - first) * cs) + x;
                    Vector.MinNative(Vector.MaxNative(new Vector<float>(u, i) + (Step * new Vector<float>(sumU, s)), low), high).CopyTo(nextU, i);
                    Vector.MinNative(Vector.MaxNative(new Vector<float>(v, i) + (Step * new Vector<float>(sumV, s)), low), high).CopyTo(nextV, i);
                }
            }
        }

        // One pixel row, its even and odd columns apart: the chroma each pixel will be shown with and the luma that best
        // suits it; with errors, also each pixel's chroma change, spread across to the samples as the upsampler weights
        // them.
        public void Row(float[] u, float[] v, int row, Rows rows, bool errors)
        {
            // The two chroma rows blended down (3 to 1), with each end repeated: entry c + 1 is sample c.
            int near = (row >> 1) * cs;
            int far = Math.Clamp((row >> 1) + ((row & 1) == 0 ? -1 : 1), 0, ch - 1) * cs;
            float[] blendU = rows.BlendU;
            float[] blendV = rows.BlendV;
            for (int c = 0; c < cs; c += Vector<float>.Count)
            {
                ((3 * new Vector<float>(u, near + c)) + new Vector<float>(u, far + c)).CopyTo(blendU, c + 1);
                ((3 * new Vector<float>(v, near + c)) + new Vector<float>(v, far + c)).CopyTo(blendV, c + 1);
            }

            blendU[0] = blendU[1];
            blendV[0] = blendV[1];
            blendU[cw + 1] = blendU[cw];
            blendV[cw + 1] = blendV[cw];

            int source = Math.Min(row, height - 1) * width;
            uint[] even = rows.Even;
            uint[] odd = rows.Odd;
            ReadOnlySpan<uint> line = argb.AsSpan(source, width);
            int pairs = width / 2;
            for (int c = 0; c < pairs; c++)
            {
                even[c] = line[2 * c];
                odd[c] = line[(2 * c) + 1];
            }

            uint last = line[width - 1];
            for (int c = pairs; c < cs; c++)
            {
                even[c] = (2 * c) < width ? line[2 * c] : last;
                odd[c] = last;
            }

            // An even pixel is 3 parts its own sample to 1 of the one on its left; an odd one, of the one on its right.
            Pixels(even, 0, rows.LumaEven, rows.DuEven, rows.DvEven, 0, rows, errors);
            Pixels(odd, 2, rows.LumaOdd, rows.DuOdd, rows.DvOdd, 1, rows, errors);
            if (!errors)
            {
                return;
            }

            // Sample c takes 3 parts from pixels 2c and 2c + 1 and 1 from 2c - 1 and 2c + 2, those clamped to the row:
            // the even changes sit at c, the odd at c + 1.
            float[] duEven = rows.DuEven;
            float[] duOdd = rows.DuOdd;
            float[] dvEven = rows.DvEven;
            float[] dvOdd = rows.DvOdd;
            duOdd[0] = duEven[0];
            dvOdd[0] = dvEven[0];
            duEven[cw] = duOdd[cw];
            dvEven[cw] = dvOdd[cw];
            for (int c = 0; c < cs; c += Vector<float>.Count)
            {
                var eu = (3 * (new Vector<float>(duEven, c) + new Vector<float>(duOdd, c + 1))) + new Vector<float>(duOdd, c) + new Vector<float>(duEven, c + 1);
                var ev = (3 * (new Vector<float>(dvEven, c) + new Vector<float>(dvOdd, c + 1))) + new Vector<float>(dvOdd, c) + new Vector<float>(dvEven, c + 1);
                eu.CopyTo(rows.ErrorU, c);
                ev.CopyTo(rows.ErrorV, c);
            }
        }

        // One set of columns: pixels, the blend entry of their other sample (0 left, 2 right), and where to put the
        // luma and the chroma changes (at an offset of 0 for even columns, 1 for odd).
        private static void Pixels(uint[] pixels, int side, float[] luma, float[] du, float[] dv, int offset, Rows rows, bool errors)
        {
            float[] blendU = rows.BlendU;
            float[] blendV = rows.BlendV;
            var mask = new Vector<uint>(0xFF);
            for (int c = 0; c < pixels.Length; c += Vector<float>.Count)
            {
                var p = new Vector<uint>(pixels, c);
                var r = Vector.ConvertToSingle(Vector.AsVectorInt32(Vector.ShiftRightLogical(p, 16) & mask));
                var g = Vector.ConvertToSingle(Vector.AsVectorInt32(Vector.ShiftRightLogical(p, 8) & mask));
                var b = Vector.ConvertToSingle(Vector.AsVectorInt32(p & mask));
                var pu = ((3 * new Vector<float>(blendU, c + 1)) + new Vector<float>(blendU, c + side)) * (1f / 16);
                var pv = ((3 * new Vector<float>(blendV, c + 1)) + new Vector<float>(blendV, c + side)) * (1f / 16);
                var cr = Kr * pv;
                var cg = (Kgu * pu) + (Kgv * pv);
                var cb = Kb * pu;
                var target = (Wr * r) + (Wg * g) + (Wb * b);

                // The luma that shows the source's brightness without clipping, then one step for any clipping.
                var l = target - ((Wr * cr) + (Wg * cg) + (Wb * cb));
                l += target - ((Wr * Clip(l + cr)) + (Wg * Clip(l + cg)) + (Wb * Clip(l + cb)));

                l = Vector.MinNative(Vector.MaxNative(l, new Vector<float>(-Ky * 16)), new Vector<float>(Ky * 239));
                l.CopyTo(luma, c);
                if (!errors)
                {
                    continue;
                }

                var dr = l + cr;
                var dg = l + cg;
                var db = l + cb;
                var er = r - Clip(dr);
                var eg = g - Clip(dg);
                var eb = b - Clip(db);
                var mr = Active(dr, er);
                var mg = Active(dg, eg);
                var mb = Active(db, eb);
                var a11 = (mg * (Kgu * Kgu)) + (mb * (Kb * Kb)) + new Vector<float>(Ridge);
                var a12 = mg * (Kgu * Kgv);
                var a22 = (mr * (Kr * Kr)) + (mg * (Kgv * Kgv)) + new Vector<float>(Ridge);
                var b1 = (mg * Kgu * eg) + (mb * Kb * eb);
                var b2 = (mr * Kr * er) + (mg * Kgv * eg);
                var inverse = Vector<float>.One / ((a11 * a22) - (a12 * a12));
                (((a22 * b1) - (a12 * b2)) * inverse).CopyTo(du, c + offset);
                (((a11 * b2) - (a12 * b1)) * inverse).CopyTo(dv, c + offset);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector<float> Clip(Vector<float> value) => Vector.MinNative(Vector.MaxNative(value, Vector<float>.Zero), new Vector<float>(255));

        // A channel clipped at a limit the source is at too has no error and no say in the chroma.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector<float> Active(Vector<float> shown, Vector<float> error)
        {
            var clipped = Vector.LessThan(shown, Vector<float>.Zero) | Vector.GreaterThan(shown, new Vector<float>(255));
            return Vector.ConditionalSelect(clipped & Vector.Equals(error, Vector<float>.Zero), Vector<float>.Zero, Vector<float>.One);
        }
    }

    // One band's working rows, each a chroma row long: the source pixels, luma and chroma changes of the even and the
    // odd columns (the changes with a spare entry), the blended chroma rows (with each end repeated), and the changes
    // spread to the chroma samples.
    private sealed class Rows(int cs)
    {
        public uint[] Even { get; } = new uint[cs];

        public uint[] Odd { get; } = new uint[cs];

        public float[] LumaEven { get; } = new float[cs];

        public float[] LumaOdd { get; } = new float[cs];

        public float[] DuEven { get; } = new float[cs + 1];

        public float[] DuOdd { get; } = new float[cs + 1];

        public float[] DvEven { get; } = new float[cs + 1];

        public float[] DvOdd { get; } = new float[cs + 1];

        public float[] BlendU { get; } = new float[cs + 2];

        public float[] BlendV { get; } = new float[cs + 2];

        public float[] ErrorU { get; } = new float[cs];

        public float[] ErrorV { get; } = new float[cs];
    }
}
