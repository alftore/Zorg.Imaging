using System;
using System.Buffers.Binary;

namespace Zorg.Imaging.Metadata;

/// <summary>
/// A device-to-PCS transform from an ICC A2B tag (ICC.1:2010, 10.10, 10.11 and 10.12): lut8Type, lut16Type or
/// lutAToBType. It maps device values (0 to 1, one to four channels) to D50 XYZ.
/// </summary>
/// <remarks>
/// <para>
/// The stages are applied in order: input ("A") curves, the multi-dimensional table (interpolated tetrahedrally, as
/// LittleCMS does), then for lutAToBType the "M" curves, the 3 x 4 matrix and the "B" curves, or for
/// the older types the output curves. The result is decoded from the PCS encoding the tag type uses and, for Lab,
/// turned into XYZ against the D50 white.
/// </para>
/// <para>
/// Reading checks every size against the tag before using it and gives null for anything it doesn't follow; the
/// table can't be larger than the tag that holds it, so a hostile profile is bounded by the profile size limit.
/// </para>
/// </remarks>
internal sealed class IccLut
{
    // The ICC D50 white point, which the PCS is relative to.
    private const double WhiteX = 0.9642;
    private const double WhiteY = 1.0;
    private const double WhiteZ = 0.8249;

    // The black of the version 4 perceptual reference medium (ICC.1:2010, 6.3.4.3; L* 3.1373), as XYZ.
    private const double BlackX = 0.00336;
    private const double BlackY = 0.0034731;
    private const double BlackZ = 0.00287;

    private readonly Func<double, double>[] _inputCurves;
    private readonly int[] _grid;
    private readonly float[] _table;
    private readonly Func<double, double>[]? _mCurves;
    private readonly double[]? _matrix;
    private readonly Func<double, double>[] _outputCurves;
    private readonly Pcs _pcs;

    private IccLut(IccLut other)
        : this(other.Inputs, other._inputCurves, other._grid, other._table, other._mCurves, other._matrix, other._outputCurves, other._pcs)
    {
    }

    private IccLut(int inputs, Func<double, double>[] inputCurves, int[] grid, float[] table, Func<double, double>[]? mCurves, double[]? matrix, Func<double, double>[] outputCurves, Pcs pcs)
    {
        Inputs = inputs;
        _inputCurves = inputCurves;
        _grid = grid;
        _table = table;
        _mCurves = mCurves;
        _matrix = matrix;
        _outputCurves = outputCurves;
        _pcs = pcs;
    }

    /// <summary>How the last stage's 0 to 1 outputs encode the PCS.</summary>
    private enum Pcs
    {
        /// <summary>XYZ, 1.0 at 0x8000 of 16 bits (lut16, lutAToB).</summary>
        Xyz16,

        /// <summary>Lab, 8-bit encoding: L 0 to 100, a and b -128 to 127 (lut8).</summary>
        Lab8,

        /// <summary>Lab, the legacy 16-bit encoding with 100 at 0xFF00 (lut16).</summary>
        LabLegacy16,

        /// <summary>Lab, the version 4 encoding: L 0 to 100 and a, b -128 to 127 over 0 to 1 (lutAToB).</summary>
        Lab4,
    }

    /// <summary>The device channels: 3 for RGB, 4 for CMYK.</summary>
    public int Inputs { get; }

    /// <summary>
    /// Whether the table is a version 4 perceptual (or saturation) one, whose PCS black is the reference medium's, not
    /// zero: its results are then scaled so that black maps to zero and white stays, the black point compensation
    /// LittleCMS always applies for these intents, so the darkest colour the device makes shows as black.
    /// </summary>
    public bool ScalesBlack { get; init; }

    /// <summary>The transform in an A2B tag, or null when it can't be read or isn't device to a three-channel PCS.</summary>
    /// <param name="tag">The tag's data.</param>
    /// <param name="labPcs">Whether the profile's PCS is Lab (otherwise XYZ).</param>
    /// <param name="scalesBlack">Whether the table is version 4 perceptual or saturation: see <see cref="ScalesBlack"/>.</param>
    public static IccLut? Read(ReadOnlySpan<byte> tag, bool labPcs, bool scalesBlack)
    {
        if (tag.Length < 32)
        {
            return null;
        }

        try
        {
            IccLut? lut = tag[..4] switch
            {
                [(byte)'m', (byte)'f', (byte)'t', (byte)'1'] => ReadLut(tag, sixteen: false, labPcs),
                [(byte)'m', (byte)'f', (byte)'t', (byte)'2'] => ReadLut(tag, sixteen: true, labPcs),
                [(byte)'m', (byte)'A', (byte)'B', (byte)' '] => ReadAToB(tag, labPcs),
                _ => null,
            };
            return lut is null ? null : new IccLut(lut) { ScalesBlack = scalesBlack };
        }
        catch (ArgumentOutOfRangeException)
        {
            // A size that passed the checks but still points outside the tag: treat the tag as unreadable.
            return null;
        }
    }

    /// <summary>The D50 XYZ for device values, each 0 to 1.</summary>
    public (double X, double Y, double Z) Evaluate(ReadOnlySpan<double> device)
    {
        Span<double> input = stackalloc double[4];
        for (int i = 0; i < Inputs; i++)
        {
            input[i] = Math.Clamp(_inputCurves[i](Math.Clamp(device[i], 0, 1)), 0, 1);
        }

        Span<double> values = stackalloc double[3];
        if (_table.Length > 0)
        {
            Interpolate(input[..Inputs], values);
        }
        else
        {
            input[..3].CopyTo(values);
        }

        if (_mCurves is not null)
        {
            for (int c = 0; c < 3; c++)
            {
                values[c] = _mCurves[c](Math.Clamp(values[c], 0, 1));
            }
        }

        if (_matrix is not null)
        {
            double a = values[0], b = values[1], d = values[2];
            for (int c = 0; c < 3; c++)
            {
                values[c] = (_matrix[c * 3] * a) + (_matrix[(c * 3) + 1] * b) + (_matrix[(c * 3) + 2] * d) + _matrix[9 + c];
            }
        }

        for (int c = 0; c < 3; c++)
        {
            values[c] = _outputCurves[c](Math.Clamp(values[c], 0, 1));
        }

        (double x, double y, double z) = Decode(values[0], values[1], values[2]);
        return ScalesBlack
            ? ((x - BlackX) * WhiteX / (WhiteX - BlackX), (y - BlackY) * WhiteY / (WhiteY - BlackY), (z - BlackZ) * WhiteZ / (WhiteZ - BlackZ))
            : (x, y, z);
    }

    // Tetrahedral, as LittleCMS interpolates (see Tetrahedral).
    private void Interpolate(ReadOnlySpan<double> input, Span<double> output) => Tetrahedral.Interpolate(_table, _grid, input, output);

    private (double X, double Y, double Z) Decode(double a, double b, double c)
    {
        switch (_pcs)
        {
            case Pcs.Xyz16:
                double scale = 65535.0 / 32768;
                return (a * scale, b * scale, c * scale);
            case Pcs.Lab8:
                return FromLab(a * 100, (b * 255) - 128, (c * 255) - 128);
            case Pcs.LabLegacy16:
                return FromLab(a * 65535 / 65280 * 100, (b * 65535 / 256) - 128, (c * 65535 / 256) - 128);
            default:
                return FromLab(a * 100, (b * 255) - 128, (c * 255) - 128);
        }
    }

    // CIE Lab to XYZ against the D50 white.
    private static (double X, double Y, double Z) FromLab(double l, double a, double b)
    {
        double fy = (l + 16) / 116;
        double fx = fy + (a / 500);
        double fz = fy - (b / 200);
        return (WhiteX * Inverse(fx), WhiteY * Inverse(fy), WhiteZ * Inverse(fz));
    }

    private static double Inverse(double t) => t > 6.0 / 29 ? t * t * t : 3 * (6.0 / 29) * (6.0 / 29) * (t - (4.0 / 29));

    // lut8Type and lut16Type: a 48-byte header (with a matrix used only for XYZ input, so not here), input tables,
    // the table, output tables; the 16-bit type gives its table lengths after the header.
    private static IccLut? ReadLut(ReadOnlySpan<byte> tag, bool sixteen, bool labPcs)
    {
        int inputs = tag[8];
        int outputs = tag[9];
        int points = tag[10];
        if (inputs is < 1 or > 4 || outputs != 3 || points < 2 || tag.Length < (sixteen ? 52 : 48))
        {
            return null;
        }

        int inputEntries = sixteen ? BinaryPrimitives.ReadUInt16BigEndian(tag[48..]) : 256;
        int outputEntries = sixteen ? BinaryPrimitives.ReadUInt16BigEndian(tag[50..]) : 256;
        int size = sixteen ? 2 : 1;
        if (inputEntries is < 2 or > 4096 || outputEntries is < 2 or > 4096)
        {
            return null;
        }

        long nodes = 1;
        for (int i = 0; i < inputs; i++)
        {
            nodes *= points;
        }

        int at = sixteen ? 52 : 48;
        long needed = at + ((long)inputs * inputEntries * size) + (nodes * outputs * size) + ((long)outputs * outputEntries * size);
        if (needed > tag.Length)
        {
            return null;
        }

        var inputCurves = new Func<double, double>[inputs];
        for (int i = 0; i < inputs; i++)
        {
            inputCurves[i] = Table(tag.Slice(at, inputEntries * size), inputEntries, sixteen);
            at += inputEntries * size;
        }

        float[] table = new float[nodes * outputs];
        double max = sixteen ? 65535 : 255;
        for (int i = 0; i < table.Length; i++, at += size)
        {
            table[i] = (float)((sixteen ? BinaryPrimitives.ReadUInt16BigEndian(tag[at..]) : tag[at]) / max);
        }

        var outputCurves = new Func<double, double>[outputs];
        for (int i = 0; i < outputs; i++)
        {
            outputCurves[i] = Table(tag.Slice(at, outputEntries * size), outputEntries, sixteen);
            at += outputEntries * size;
        }

        int[] grid = new int[inputs];
        Array.Fill(grid, points);
        Pcs pcs = !labPcs ? Pcs.Xyz16 : sixteen ? Pcs.LabLegacy16 : Pcs.Lab8;
        return new IccLut(inputs, inputCurves, grid, table, null, null, outputCurves, pcs);
    }

    // lutAToBType: optional A curves, table, M curves and matrix, then B curves, each at an offset from the tag's start.
    private static IccLut? ReadAToB(ReadOnlySpan<byte> tag, bool labPcs)
    {
        int inputs = tag[8];
        int outputs = tag[9];
        if (inputs is < 1 or > 4 || outputs != 3)
        {
            return null;
        }

        uint bOffset = BinaryPrimitives.ReadUInt32BigEndian(tag[12..]);
        uint matrixOffset = BinaryPrimitives.ReadUInt32BigEndian(tag[16..]);
        uint mOffset = BinaryPrimitives.ReadUInt32BigEndian(tag[20..]);
        uint tableOffset = BinaryPrimitives.ReadUInt32BigEndian(tag[24..]);
        uint aOffset = BinaryPrimitives.ReadUInt32BigEndian(tag[28..]);
        if (bOffset == 0 || (tableOffset == 0) != (aOffset == 0) || (tableOffset == 0 && inputs != 3) || (matrixOffset == 0) != (mOffset == 0))
        {
            return null;
        }

        Func<double, double>[]? outputCurves = Curves(tag, bOffset, 3);
        Func<double, double>[]? mCurves = mOffset == 0 ? null : Curves(tag, mOffset, 3);
        Func<double, double>[]? inputCurves = aOffset == 0 ? Identity(3) : Curves(tag, aOffset, inputs);
        if (outputCurves is null || inputCurves is null || (mOffset != 0 && mCurves is null))
        {
            return null;
        }

        double[]? matrix = null;
        if (matrixOffset != 0)
        {
            if (matrixOffset + 48L > tag.Length)
            {
                return null;
            }

            matrix = new double[12];
            for (int i = 0; i < 12; i++)
            {
                matrix[i] = BinaryPrimitives.ReadInt32BigEndian(tag[(int)(matrixOffset + (4 * i))..]) / 65536.0;
            }
        }

        int[] grid = [];
        float[] table = [];
        if (tableOffset != 0)
        {
            if (tableOffset + 20L > tag.Length)
            {
                return null;
            }

            ReadOnlySpan<byte> clut = tag[(int)tableOffset..];
            grid = new int[inputs];
            long nodes = 1;
            for (int i = 0; i < inputs; i++)
            {
                grid[i] = clut[i];
                if (grid[i] < 2)
                {
                    return null;
                }

                nodes *= grid[i];
            }

            int precision = clut[16];
            if (precision is not (1 or 2) || 20 + (nodes * outputs * precision) > clut.Length)
            {
                return null;
            }

            table = new float[nodes * outputs];
            double max = precision == 2 ? 65535 : 255;
            for (int i = 0; i < table.Length; i++)
            {
                int at = 20 + (i * precision);
                table[i] = (float)((precision == 2 ? BinaryPrimitives.ReadUInt16BigEndian(clut[at..]) : clut[at]) / max);
            }
        }

        return new IccLut(inputs, inputCurves, grid, table, mCurves, matrix, outputCurves, labPcs ? Pcs.Lab4 : Pcs.Xyz16);
    }

    // `count` curv or para elements one after another, each padded to four bytes.
    private static Func<double, double>[]? Curves(ReadOnlySpan<byte> tag, uint offset, int count)
    {
        var curves = new Func<double, double>[count];
        long at = offset;
        for (int i = 0; i < count; i++)
        {
            if (at + 12 > tag.Length)
            {
                return null;
            }

            ReadOnlySpan<byte> element = tag[(int)at..];
            int size = IccProfileReader.CurveSize(element);
            if (size <= 0 || at + size > tag.Length || IccProfileReader.Curve(element[..size]) is not { } curve)
            {
                return null;
            }

            curves[i] = curve;
            at += (size + 3) & ~3;
        }

        return curves;
    }

    private static Func<double, double>[] Identity(int count)
    {
        var curves = new Func<double, double>[count];
        Array.Fill(curves, v => v);
        return curves;
    }

    // A lut8 or lut16 one-dimensional table as a piecewise-linear function on 0 to 1.
    private static Func<double, double> Table(ReadOnlySpan<byte> data, int entries, bool sixteen)
    {
        double[] table = new double[entries];
        for (int i = 0; i < entries; i++)
        {
            table[i] = sixteen ? BinaryPrimitives.ReadUInt16BigEndian(data[(2 * i)..]) / 65535.0 : data[i] / 255.0;
        }

        return v =>
        {
            double at = v * (table.Length - 1);
            int i = Math.Min((int)at, table.Length - 2);
            double t = at - i;
            return (table[i] * (1 - t)) + (table[i + 1] * t);
        };
    }
}
