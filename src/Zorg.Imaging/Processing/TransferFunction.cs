using System;

namespace Zorg.Imaging.Processing;

/// <summary>
/// Maps encoded values (0 to 1) to linear light (0 to 1) and back, for one colour space's transfer curve.
/// </summary>
/// <remarks>
/// A curve we can't read yet (an ICC profile, an unknown code point) uses the sRGB curve, which nearly every such
/// profile is close to. Values convert back to exactly what they were, so flat areas never change; only the blend at
/// edges is slightly less exact.
/// </remarks>
internal sealed class TransferFunction
{
    // SMPTE ST 2084 (PQ). Linear 1 is 10,000 cd/m².
    private const double PqM1 = 2610.0 / 16384;
    private const double PqM2 = 2523.0 / 4096 * 128;
    private const double PqC1 = 3424.0 / 4096;
    private const double PqC2 = 2413.0 / 4096 * 32;
    private const double PqC3 = 2392.0 / 4096 * 32;

    // ITU-R BT.2100 HLG, scene light.
    private const double HlgA = 0.17883277;
    private const double HlgB = 1 - (4 * HlgA);
    private const double HlgC = 0.55991073;

    private readonly Curve _curve;
    private readonly double _gamma;

    private TransferFunction(Curve curve, double gamma = 0)
    {
        _curve = curve;
        _gamma = gamma;
    }

    private enum Curve
    {
        Linear,
        Srgb,
        Bt709,
        Gamma,
        Pq,
        Hlg,
    }

    /// <summary>No conversion: encoded values are used as they are.</summary>
    public static TransferFunction Identity { get; } = new(Curve.Linear);

    private static TransferFunction Srgb { get; } = new(Curve.Srgb);

    /// <summary>The transfer curve of a colour space.</summary>
    public static TransferFunction For(ColorSpace colorSpace)
    {
        if (colorSpace.CicpTransfer is int transfer)
        {
            return transfer switch
            {
                8 => Identity,
                1 or 6 or 14 or 15 => new TransferFunction(Curve.Bt709),
                4 => new TransferFunction(Curve.Gamma, 2.2),
                5 => new TransferFunction(Curve.Gamma, 2.8),
                16 => new TransferFunction(Curve.Pq),
                18 => new TransferFunction(Curve.Hlg),
                _ => Srgb,
            };
        }

        if (colorSpace.PngGamma > 0)
        {
            // gAMA is the encoding exponent times 100,000: encoded = linear ^ (gAMA / 100,000).
            return new TransferFunction(Curve.Gamma, 100_000.0 / colorSpace.PngGamma);
        }

        return Srgb;
    }

    /// <summary>Encoded value to linear light.</summary>
    public double ToLinear(double v) => _curve switch
    {
        Curve.Srgb => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4),
        Curve.Bt709 => v < 0.081 ? v / 4.5 : Math.Pow((v + 0.099) / 1.099, 1 / 0.45),
        Curve.Gamma => Math.Pow(v, _gamma),
        Curve.Pq => PqToLinear(v),
        Curve.Hlg => v <= 0.5 ? v * v / 3 : (Math.Exp((v - HlgC) / HlgA) + HlgB) / 12,
        _ => v,
    };

    /// <summary>Linear light, 0 to 1, to the encoded value.</summary>
    public double FromLinear(double l) => _curve switch
    {
        Curve.Srgb => l <= 0.0031308 ? l * 12.92 : (1.055 * Math.Pow(l, 1 / 2.4)) - 0.055,
        Curve.Bt709 => l < 0.018 ? l * 4.5 : (1.099 * Math.Pow(l, 0.45)) - 0.099,
        Curve.Gamma => Math.Pow(l, 1 / _gamma),
        Curve.Pq => PqFromLinear(l),
        Curve.Hlg => l <= 1.0 / 12 ? Math.Sqrt(3 * l) : (HlgA * Math.Log((12 * l) - HlgB)) + HlgC,
        _ => l,
    };

    private static double PqToLinear(double v)
    {
        double p = Math.Pow(v, 1 / PqM2);
        return Math.Pow(Math.Max(p - PqC1, 0) / (PqC2 - (PqC3 * p)), 1 / PqM1);
    }

    private static double PqFromLinear(double l)
    {
        double y = Math.Pow(l, PqM1);
        return Math.Pow((PqC1 + (PqC2 * y)) / (1 + (PqC3 * y)), PqM2);
    }
}
