using System;
using System.Runtime.Intrinsics;

namespace Zorg.Imaging.Processing;

/// <summary>
/// Opaque sRGB colours as Oklab (Björn Ottosson, 2020, public domain), a space where distance follows perceived
/// difference, scaled to the quantiser's 0 to 255 lanes: (255, L, a + 128, b + 128), each of L, a and b times 255.
/// </summary>
/// <remarks>
/// The cube root is a bit-level estimate refined by three Newton steps (relative error about 1e-7, well under the
/// quantiser's resolution), four lanes at once; every step is plain IEEE arithmetic, so results are the same on every
/// machine.
/// </remarks>
internal static class Oklab
{
    // sRGB levels as linear light, and the step from each to the next, for values between levels.
    private static readonly float[] Linear = BuildLinear();

    // Linear RGB to the cone responses (lanes 1 to 3: l, m, s; lane 0 is kept at 1 so its cube root is defined).
    private static readonly Vector128<float> FromRed = Vector128.Create(0f, 0.4122214708f, 0.2119034982f, 0.0883024619f);
    private static readonly Vector128<float> FromGreen = Vector128.Create(0f, 0.5363325363f, 0.6806995451f, 0.2817188376f);
    private static readonly Vector128<float> FromBlue = Vector128.Create(0f, 0.0514459929f, 0.1073969566f, 0.6299787005f);

    // The cube-rooted responses to (L, a, b), times 255.
    private static readonly Vector128<float> FromL = Vector128.Create(0f, 0.2104542553f, 1.9779984951f, 0.0259040371f) * 255;
    private static readonly Vector128<float> FromM = Vector128.Create(0f, 0.7936177850f, -2.4285922050f, 0.7827717662f) * 255;
    private static readonly Vector128<float> FromS = Vector128.Create(0f, -0.0040720468f, 0.4505937099f, -0.8086757660f) * 255;
    private static readonly Vector128<float> Offset = Vector128.Create(255f, 0, 128, 128);

    /// <summary>An opaque colour's lanes.</summary>
    /// <param name="argb">The colour; its alpha is ignored.</param>
    /// <returns>(255, L, a, b) as described.</returns>
    public static Vector128<float> FromArgb(uint argb) =>
        FromLinear(Linear[2 * (int)((argb >> 16) & 255)], Linear[2 * (int)((argb >> 8) & 255)], Linear[2 * (int)(argb & 255)]);

    /// <summary>A colour given as (any, R, G, B) floats from 0 to 255, as error diffusion carries them.</summary>
    /// <param name="rgb">The colour, red to blue in lanes 1 to 3.</param>
    /// <returns>(255, L, a, b) as described.</returns>
    public static Vector128<float> FromRgb(Vector128<float> rgb) =>
        FromLinear(ToLinear(rgb.GetElement(1)), ToLinear(rgb.GetElement(2)), ToLinear(rgb.GetElement(3)));

    /// <summary>The opaque sRGB colour nearest a point in the lanes, for a palette entry.</summary>
    /// <param name="lanes">(any, L, a, b) as described.</param>
    /// <returns>The colour as opaque ARGB.</returns>
    public static uint ToArgb(Vector128<float> lanes)
    {
        double l = lanes.GetElement(1) / 255.0, a = (lanes.GetElement(2) - 128) / 255.0, b = (lanes.GetElement(3) - 128) / 255.0;
        double cl = l + (0.3963377774 * a) + (0.2158037573 * b);
        double cm = l - (0.1055613458 * a) - (0.0638541728 * b);
        double cs = l - (0.0894841775 * a) - (1.2914855480 * b);
        (cl, cm, cs) = (cl * cl * cl, cm * cm * cm, cs * cs * cs);
        uint red = Encode((4.0767416621 * cl) - (3.3077115913 * cm) + (0.2309699292 * cs));
        uint green = Encode((-1.2684380046 * cl) + (2.6097574011 * cm) - (0.3413193965 * cs));
        uint blue = Encode((-0.0041960863 * cl) - (0.7034186147 * cm) + (1.7076147010 * cs));
        return 0xFF000000u | (red << 16) | (green << 8) | blue;

        static uint Encode(double linear)
        {
            linear = Math.Clamp(linear, 0, 1);
            double s = linear <= 0.0031308 ? linear * 12.92 : (1.055 * Math.Pow(linear, 1 / 2.4)) - 0.055;
            return (uint)Math.Clamp(Math.Round(s * 255), 0, 255);
        }
    }

    /// <summary>
    /// Recent conversions, 4,096 places addressed by a hash of the colour: pictures repeat colours often, but far apart
    /// in the pixel order. One per thread.
    /// </summary>
    public sealed class Cache
    {
        private const int Bits = 12;
        private readonly uint[] _keys = new uint[1 << Bits];
        private readonly Vector128<float>[] _values = new Vector128<float>[1 << Bits];

        /// <summary>The colour's lanes, as <see cref="Oklab.FromArgb"/>.</summary>
        /// <param name="argb">The colour; alpha should be 255.</param>
        /// <returns>(255, L, a, b).</returns>
        public Vector128<float> FromArgb(uint argb)
        {
            // A key of 0 is an empty place; an opaque colour is never 0.
            int at = (int)((argb * 0x9E3779B1u) >> (32 - Bits));
            if (_keys[at] == argb && argb != 0)
            {
                return _values[at];
            }

            Vector128<float> lanes = Oklab.FromArgb(argb);
            (_keys[at], _values[at]) = (argb, lanes);
            return lanes;
        }
    }

    private static Vector128<float> FromLinear(float r, float g, float b)
    {
        Vector128<float> lms = (Vector128.Create(r) * FromRed) + (Vector128.Create(g) * FromGreen) + (Vector128.Create(b) * FromBlue);
        Vector128<float> c = Cbrt(lms + Vector128.Create(1f, 0, 0, 0));
        Vector128<float> lab = (Vector128.Create(c.GetElement(1)) * FromL) + (Vector128.Create(c.GetElement(2)) * FromM) + (Vector128.Create(c.GetElement(3)) * FromS);
        return lab + Offset;
    }

    // Cube roots of non-negative lanes: an estimate from the exponent bits (a third of them), then Newton's steps.
    private static Vector128<float> Cbrt(Vector128<float> x)
    {
        x = Vector128.Max(x, Vector128.Create(1e-12f));
        Vector128<int> bits = Vector128.ConvertToInt32(Vector128.ConvertToSingle(x.AsInt32()) * (1f / 3)) + Vector128.Create(0x2A51_37A0);
        Vector128<float> y = bits.AsSingle();
        for (int step = 0; step < 3; step++)
        {
            y = (y * (2f / 3)) + (x / (Vector128.Create(3f) * y * y));
        }

        return y;
    }

    private static float ToLinear(float level)
    {
        level = Math.Clamp(level, 0, 255);
        int i = Math.Min((int)level, 254);
        return Linear[2 * i] + ((level - i) * Linear[(2 * i) + 1]);
    }

    private static float[] BuildLinear()
    {
        float[] table = new float[512];
        double previous = 0;
        for (int i = 255; i >= 0; i--)
        {
            double s = i / 255.0;
            double linear = s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            table[2 * i] = (float)linear;
            table[(2 * i) + 1] = (float)(previous - linear);
            previous = linear;
        }

        return table;
    }
}
