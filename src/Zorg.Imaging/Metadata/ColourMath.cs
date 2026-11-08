using System;

namespace Zorg.Imaging.Metadata;

/// <summary>
/// Colour arithmetic shared by the ICC writer, the ICC reader and colour conversion: 3 x 3 matrices as nine doubles,
/// row by row, and the D50 connection space ICC profiles use.
/// </summary>
internal static class ColourMath
{
    public static readonly double[] D50 = [0.9642, 1.0, 0.8249];

    // Bradford cone response (Lam, 1985), as ICC.1 Annex E uses.
    public static readonly double[] Bradford = [0.8951, 0.2664, -0.1614, -0.7502, 1.7135, 0.0367, 0.0389, -0.0685, 1.0296];

    public static double[] Xyz(double x, double y) => [x / y, 1, (1 - x - y) / y];

    // The matrix from linear RGB to XYZ under the space's own white (SMPTE RP 177).
    public static double[] RgbToXyz(ReadOnlySpan<double> c)
    {
        double[] primaries = new double[9];
        for (int i = 0; i < 3; i++)
        {
            double[] xyz = Xyz(c[2 + (2 * i)], c[3 + (2 * i)]);
            for (int row = 0; row < 3; row++)
            {
                primaries[(row * 3) + i] = xyz[row];
            }
        }

        double[] scale = Apply(Invert(primaries), Xyz(c[0], c[1]));
        for (int row = 0; row < 3; row++)
        {
            for (int i = 0; i < 3; i++)
            {
                primaries[(row * 3) + i] *= scale[i];
            }
        }

        return primaries;
    }

    // Bradford adaptation from a white point to D50.
    public static double[] Adaptation(double[] white)
    {
        double[] source = Apply(Bradford, white);
        double[] target = Apply(Bradford, D50);
        double[] scale = new double[9];
        for (int i = 0; i < 3; i++)
        {
            scale[(i * 3) + i] = target[i] / source[i];
        }

        return Multiply(Invert(Bradford), Multiply(scale, Bradford));
    }

    public static double[] Apply(double[] m, ReadOnlySpan<double> v)
    {
        double[] result = new double[3];
        for (int row = 0; row < 3; row++)
        {
            result[row] = (m[(row * 3) + 0] * v[0]) + (m[(row * 3) + 1] * v[1]) + (m[(row * 3) + 2] * v[2]);
        }

        return result;
    }

    public static double[] Multiply(double[] a, double[] b)
    {
        double[] result = new double[9];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                result[(row * 3) + column] = (a[(row * 3) + 0] * b[column]) + (a[(row * 3) + 1] * b[3 + column]) + (a[(row * 3) + 2] * b[6 + column]);
            }
        }

        return result;
    }

    public static double[] Invert(double[] m)
    {
        double det = (m[0] * ((m[4] * m[8]) - (m[5] * m[7])))
            - (m[1] * ((m[3] * m[8]) - (m[5] * m[6])))
            + (m[2] * ((m[3] * m[7]) - (m[4] * m[6])));
        double[] result = new double[9];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                // The cofactor of (column, row), divided by the determinant.
                int r1 = (column + 1) % 3;
                int r2 = (column + 2) % 3;
                int c1 = (row + 1) % 3;
                int c2 = (row + 2) % 3;
                result[(row * 3) + column] = ((m[(r1 * 3) + c1] * m[(r2 * 3) + c2]) - (m[(r1 * 3) + c2] * m[(r2 * 3) + c1])) / det;
            }
        }

        return result;
    }

    /// <summary>The matrix from a space's linear RGB to XYZ under D50, the ICC connection space.</summary>
    /// <param name="chromaticities">White x, y, then red, green and blue x, y.</param>
    public static double[] RgbToPcs(ReadOnlySpan<double> chromaticities) =>
        Multiply(Adaptation(Xyz(chromaticities[0], chromaticities[1])), RgbToXyz(chromaticities));

    /// <summary>White x, y, then red, green and blue x, y for an ITU-T H.273 colour primaries code, or null.</summary>
    public static double[]? Chromaticities(int primaries) => primaries switch
    {
        1 => [0.3127, 0.3290, 0.640, 0.330, 0.300, 0.600, 0.150, 0.060],
        9 => [0.3127, 0.3290, 0.708, 0.292, 0.170, 0.797, 0.131, 0.046],
        12 => [0.3127, 0.3290, 0.680, 0.320, 0.265, 0.690, 0.150, 0.060],
        _ => null,
    };

    /// <summary>PNG's chromaticities, in units of 1/100,000, as doubles.</summary>
    public static double[] ToDoubles(ReadOnlySpan<int> values)
    {
        double[] result = new double[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            result[i] = values[i] / 100_000.0;
        }

        return result;
    }
}
