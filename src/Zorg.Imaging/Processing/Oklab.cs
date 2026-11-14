using System;
using System.Runtime.Intrinsics;

namespace Zorg.Imaging.Processing;

/// <summary>
/// Opaque sRGB colours as Oklab (Björn Ottosson, 2020, public domain), a space where distance follows perceived
/// difference, scaled to the quantiser's 0 to 255 lanes: (L, a + 128, b + 128, 0), each of L, a and b times 255.
/// </summary>
/// <remarks>
/// The cube root is a bit-level estimate refined by three Newton steps (relative error about 1e-7, well under the
/// quantiser's resolution), three lanes at once; every step is plain IEEE arithmetic, so results are the same on every
/// machine.
/// </remarks>
internal static class Oklab
{
    // sRGB levels as linear light, and the step from each to the next, for values between levels.
    private static readonly float[] Linear = BuildLinear();

    // Linear RGB to the cone responses (lanes 0 to 2: l, m, s; lane 3 is kept at 1 so its cube root is defined).
    private static readonly Vector128<float> FromRed = Vector128.Create(0.4122214708f, 0.2119034982f, 0.0883024619f, 0);
    private static readonly Vector128<float> FromGreen = Vector128.Create(0.5363325363f, 0.6806995451f, 0.2817188376f, 0);
    private static readonly Vector128<float> FromBlue = Vector128.Create(0.0514459929f, 0.1073969566f, 0.6299787005f, 0);

    // The cube-rooted responses to (L, a, b), times 255.
    private static readonly Vector128<float> FromL = Vector128.Create(0.2104542553f, 1.9779984951f, 0.0259040371f, 0) * 255;
    private static readonly Vector128<float> FromM = Vector128.Create(0.7936177850f, -2.4285922050f, 0.7827717662f, 0) * 255;
    private static readonly Vector128<float> FromS = Vector128.Create(-0.0040720468f, 0.4505937099f, -0.8086757660f, 0) * 255;
    private static readonly Vector128<float> Offset = Vector128.Create(0f, 128, 128, 0);

    /// <summary>An opaque colour's lanes.</summary>
    /// <param name="argb">The colour; its alpha is ignored.</param>
    /// <returns>(L, a, b, 0) as described.</returns>
    public static Vector128<float> FromArgb(uint argb) =>
        FromLinear(Linear[2 * (int)((argb >> 16) & 255)], Linear[2 * (int)((argb >> 8) & 255)], Linear[2 * (int)(argb & 255)]);

    /// <summary>A colour given as (any, R, G, B) floats from 0 to 255, as error diffusion and premultiplying make them.</summary>
    /// <param name="rgb">The colour, red to blue in lanes 1 to 3.</param>
    /// <returns>(L, a, b, 0) as described.</returns>
    public static Vector128<float> FromRgb(Vector128<float> rgb) =>
        FromLinear(ToLinear(rgb.GetElement(1)), ToLinear(rgb.GetElement(2)), ToLinear(rgb.GetElement(3)));

    /// <summary>
    /// Two colours at once, given as (any, R, G, B) floats from 0 to 255: (L, a, b) of the first in lanes 0 to 2 and of
    /// the second in lanes 3 to 5, the same values as two calls of <see cref="FromRgb"/>.
    /// </summary>
    /// <param name="first">The first colour, red to blue in lanes 1 to 3.</param>
    /// <param name="second">The second colour, likewise.</param>
    /// <returns>(L, a, b, L, a, b, 0, 0).</returns>
    public static Vector256<float> FromRgbPair(Vector128<float> first, Vector128<float> second)
    {
        Vector128<float> one = Responses(ToLinear(first.GetElement(1)), ToLinear(first.GetElement(2)), ToLinear(first.GetElement(3)));
        Vector128<float> two = Responses(ToLinear(second.GetElement(1)), ToLinear(second.GetElement(2)), ToLinear(second.GetElement(3)));
        Vector256<float> c = Cbrt(Vector256.Create(one, two) + Vector256.Create(0f, 0, 0, 1, 0, 0, 0, 1));
        Vector128<float> labOne = Lab(c.GetLower());
        Vector128<float> labTwo = Lab(c.GetUpper());
        return Vector256.Create(labOne.GetElement(0), labOne.GetElement(1), labOne.GetElement(2), labTwo.GetElement(0), labTwo.GetElement(1), labTwo.GetElement(2), 0, 0);
    }

    /// <summary>The opaque sRGB colour nearest a point in the lanes.</summary>
    /// <param name="l">L times 255.</param>
    /// <param name="a">a times 255, plus 128.</param>
    /// <param name="b">b times 255, plus 128.</param>
    /// <returns>The colour as opaque ARGB.</returns>
    public static uint ToArgb(float l, float a, float b)
    {
        double ll = l / 255.0, aa = (a - 128) / 255.0, bb = (b - 128) / 255.0;
        double cl = ll + (0.3963377774 * aa) + (0.2158037573 * bb);
        double cm = ll - (0.1055613458 * aa) - (0.0638541728 * bb);
        double cs = ll - (0.0894841775 * aa) - (1.2914855480 * bb);
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

    private static Vector128<float> FromLinear(float r, float g, float b) =>
        Lab(Cbrt(Responses(r, g, b) + Vector128.Create(0f, 0, 0, 1)));

    private static Vector128<float> Responses(float r, float g, float b) =>
        (Vector128.Create(r) * FromRed) + (Vector128.Create(g) * FromGreen) + (Vector128.Create(b) * FromBlue);

    private static Vector128<float> Lab(Vector128<float> c) =>
        (Vector128.Create(c.GetElement(0)) * FromL) + (Vector128.Create(c.GetElement(1)) * FromM) + (Vector128.Create(c.GetElement(2)) * FromS) + Offset;

    // Cube roots of non-negative lanes: an estimate from the exponent bits (a third of them), then Newton's steps. Each
    // lane is computed alone, so a lane's root is the same whatever the others hold, four lanes or eight.
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

    private static Vector256<float> Cbrt(Vector256<float> x)
    {
        x = Vector256.Max(x, Vector256.Create(1e-12f));
        Vector256<int> bits = Vector256.ConvertToInt32(Vector256.ConvertToSingle(x.AsInt32()) * (1f / 3)) + Vector256.Create(0x2A51_37A0);
        Vector256<float> y = bits.AsSingle();
        for (int step = 0; step < 3; step++)
        {
            y = (y * (2f / 3)) + (x / (Vector256.Create(3f) * y * y));
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
