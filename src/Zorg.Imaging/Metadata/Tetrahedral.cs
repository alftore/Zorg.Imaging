using System;

namespace Zorg.Imaging.Metadata;

/// <summary>
/// Interpolation in a colour lookup table with three outputs, as colour management usually does it: tetrahedral for
/// three inputs (the cube around the point split into six tetrahedra, four corners weighted), and for four inputs,
/// tetrahedral in the last three at the two grid points around the first, then linear between them. Fewer corners than
/// multilinear interpolation, and no blending across a cube's diagonal, which keeps neutrals neutral.
/// </summary>
internal static class Tetrahedral
{
    /// <summary>Interpolates the table at <paramref name="input"/> (each 0 to 1) into <paramref name="output"/>.</summary>
    /// <param name="table">Three values per grid point, the last input varying fastest.</param>
    /// <param name="points">Grid points along each input.</param>
    /// <param name="input">Three or four inputs.</param>
    /// <param name="output">The three outputs.</param>
    public static void Interpolate(ReadOnlySpan<float> table, ReadOnlySpan<int> points, ReadOnlySpan<double> input, Span<double> output)
    {
        if (input.Length == 3)
        {
            Three(table, points, input, 0, output);
            return;
        }

        if (input.Length == 4)
        {
            int stride = 3 * points[1] * points[2] * points[3];
            (int low, double fraction) = Cell(input[0], points[0]);
            Span<double> a = stackalloc double[3];
            Span<double> b = stackalloc double[3];
            Three(table, points[1..], input[1..], low * stride, a);
            Three(table, points[1..], input[1..], (low + 1) * stride, b);
            for (int c = 0; c < 3; c++)
            {
                output[c] = a[c] + (fraction * (b[c] - a[c]));
            }

            return;
        }

        // One or two inputs: linear in each.
        Multilinear(table, points, input, output);
    }

    private static (int Low, double Fraction) Cell(double value, int points)
    {
        double at = Math.Clamp(value, 0, 1) * (points - 1);
        int low = Math.Min((int)at, points - 2);
        return (low, at - low);
    }

    private static void Three(ReadOnlySpan<float> table, ReadOnlySpan<int> points, ReadOnlySpan<double> input, int origin, Span<double> output)
    {
        (int x0, double rx) = Cell(input[0], points[0]);
        (int y0, double ry) = Cell(input[1], points[1]);
        (int z0, double rz) = Cell(input[2], points[2]);
        int sz = 3;
        int sy = sz * points[2];
        int sx = sy * points[1];
        int c000 = origin + (x0 * sx) + (y0 * sy) + (z0 * sz);
        int c111 = c000 + sx + sy + sz;

        // The tetrahedron the point lies in, by the order of its fractions: the path from the low corner to the high
        // one along the three edges in that order.
        int first, second;
        double w1, w2, w3;
        if (rx >= ry)
        {
            if (ry >= rz)
            {
                (first, second, w1, w2, w3) = (c000 + sx, c000 + sx + sy, rx, ry, rz);
            }
            else if (rx >= rz)
            {
                (first, second, w1, w2, w3) = (c000 + sx, c000 + sx + sz, rx, rz, ry);
            }
            else
            {
                (first, second, w1, w2, w3) = (c000 + sz, c000 + sx + sz, rz, rx, ry);
            }
        }
        else if (rx >= rz)
        {
            (first, second, w1, w2, w3) = (c000 + sy, c000 + sx + sy, ry, rx, rz);
        }
        else if (ry >= rz)
        {
            (first, second, w1, w2, w3) = (c000 + sy, c000 + sy + sz, ry, rz, rx);
        }
        else
        {
            (first, second, w1, w2, w3) = (c000 + sz, c000 + sy + sz, rz, ry, rx);
        }

        for (int c = 0; c < 3; c++)
        {
            double v0 = table[c000 + c];
            double v1 = table[first + c];
            double v2 = table[second + c];
            double v3 = table[c111 + c];
            output[c] = v0 + (w1 * (v1 - v0)) + (w2 * (v2 - v1)) + (w3 * (v3 - v2));
        }
    }

    private static void Multilinear(ReadOnlySpan<float> table, ReadOnlySpan<int> points, ReadOnlySpan<double> input, Span<double> output)
    {
        int n = input.Length;
        Span<int> low = stackalloc int[2];
        Span<double> fraction = stackalloc double[2];
        Span<int> stride = stackalloc int[2];
        int step = 3;
        for (int d = n - 1; d >= 0; d--)
        {
            stride[d] = step;
            step *= points[d];
            (low[d], fraction[d]) = Cell(input[d], points[d]);
        }

        output.Clear();
        for (int corner = 0; corner < 1 << n; corner++)
        {
            double weight = 1;
            int index = 0;
            for (int d = 0; d < n; d++)
            {
                bool high = (corner & (1 << d)) != 0;
                weight *= high ? fraction[d] : 1 - fraction[d];
                index += (low[d] + (high ? 1 : 0)) * stride[d];
            }

            for (int c = 0; c < 3; c++)
            {
                output[c] += weight * table[index + c];
            }
        }
    }
}
