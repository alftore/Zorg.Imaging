using System;
using Zorg.Imaging.Metadata;

namespace Zorg.Imaging.Processing;

/// <summary>
/// A lookup-table profile evaluated once on a grid, from device values to linear sRGB, so each pixel costs an
/// interpolation instead of the profile's whole chain: 33 points a side for RGB and 17 for CMYK, the sizes LittleCMS
/// uses for its own precalculated transforms.
/// </summary>
internal sealed class IccLookup
{
    private readonly float[] _grid;
    private readonly int _points;
    private readonly int _channels;

    private IccLookup(float[] grid, int points, int channels)
    {
        _grid = grid;
        _points = points;
        _channels = channels;
    }

    /// <summary>The device channels: 3 or 4.</summary>
    public int Channels => _channels;

    /// <summary>Evaluates the table on its grid; for CMYK, in parallel bands when the machine allows.</summary>
    /// <param name="lut">The profile's table.</param>
    /// <param name="fromPcs">D50 XYZ to linear sRGB, row by row.</param>
    public static IccLookup Build(IccLut lut, double[] fromPcs)
    {
        int channels = lut.Inputs;
        int points = channels == 4 ? 17 : 33;
        int nodes = 1;
        for (int i = 0; i < channels; i++)
        {
            nodes *= points;
        }

        float[] grid = new float[nodes * 3];
        int bands = Parallelism.Bands((long)nodes * 64, points);
        Parallelism.For(bands, band =>
        {
            Span<double> device = stackalloc double[4];
            int first = (int)((long)nodes * band / bands);
            int end = (int)((long)nodes * (band + 1) / bands);
            for (int node = first; node < end; node++)
            {
                // The last channel varies fastest, as the interpolation below expects.
                int rest = node;
                for (int d = channels - 1; d >= 0; d--)
                {
                    device[d] = (double)(rest % points) / (points - 1);
                    rest /= points;
                }

                (double x, double y, double z) = lut.Evaluate(device[..channels]);
                grid[node * 3] = (float)((fromPcs[0] * x) + (fromPcs[1] * y) + (fromPcs[2] * z));
                grid[(node * 3) + 1] = (float)((fromPcs[3] * x) + (fromPcs[4] * y) + (fromPcs[5] * z));
                grid[(node * 3) + 2] = (float)((fromPcs[6] * x) + (fromPcs[7] * y) + (fromPcs[8] * z));
            }
        });

        return new IccLookup(grid, points, channels);
    }

    /// <summary>Linear sRGB for device values, each 0 to 1, interpolated tetrahedrally between the grid points around them.</summary>
    public (float R, float G, float B) Lookup(ReadOnlySpan<float> device)
    {
        Span<double> input = stackalloc double[4];
        Span<int> points = stackalloc int[4];
        for (int d = 0; d < _channels; d++)
        {
            input[d] = device[d];
            points[d] = _points;
        }

        Span<double> rgb = stackalloc double[3];
        Tetrahedral.Interpolate(_grid, points[.._channels], input[.._channels], rgb);
        return ((float)rgb[0], (float)rgb[1], (float)rgb[2]);
    }
}
