using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Zorg.Imaging.Metadata;

/// <summary>
/// Writes a small ICC v4 display profile (ICC.1:2022) for a colour space known by its primaries, white point and
/// transfer curve, so formats that carry colour only as ICC (JPEG, WebP) can say what the pixels mean.
/// </summary>
/// <remarks>
/// <para>
/// A matrix/TRC profile: the colorants are the primaries in XYZ, adapted to the D50 connection space with the Bradford
/// transform, which is also stored as the <c>chad</c> tag; the curve is one parametric <c>para</c> tag shared by the three
/// channels. About 600 bytes.
/// </para>
/// <para>
/// PQ and HLG can't be written this way: an 8-bit file would need tone mapping first, which isn't supported yet, so
/// they throw rather than shift colours silently.
/// </para>
/// </remarks>
internal static class IccProfileWriter
{
    private static readonly double[] D50 = [0.9642, 1.0, 0.8249];

    // Bradford cone response (Lam, 1985), as ICC.1 Annex E uses.
    private static readonly double[] Bradford = [0.8951, 0.2664, -0.1614, -0.7502, 1.7135, 0.0367, 0.0389, -0.0685, 1.0296];

    /// <summary>The profile to write for a colour space: its own ICC bytes, none for sRGB, or a generated one.</summary>
    /// <param name="colorSpace">The image's colour space.</param>
    /// <param name="format">The format's name, for messages.</param>
    /// <returns>The profile, or empty for sRGB, which needs none.</returns>
    /// <exception cref="NotSupportedException">The colour space is HDR, or its primaries or curve aren't known.</exception>
    public static ReadOnlyMemory<byte> For(ColorSpace colorSpace, string format)
    {
        if (!colorSpace.IccProfile.IsEmpty)
        {
            return colorSpace.IccProfile;
        }

        if (colorSpace.IsSrgb)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        if (colorSpace.IsHdr)
        {
            throw new NotSupportedException(string.Create(
                CultureInfo.InvariantCulture,
                $"The image is HDR ({colorSpace.Name}). {format} can't hold it without tone mapping to SDR, which isn't supported yet; save it as PNG to keep it."));
        }

        double[] chromaticities;
        Curve curve;
        if (colorSpace.CicpPrimaries is int primaries && colorSpace.CicpTransfer is int transfer)
        {
            chromaticities = primaries switch
            {
                1 => [0.3127, 0.3290, 0.640, 0.330, 0.300, 0.600, 0.150, 0.060],
                9 => [0.3127, 0.3290, 0.708, 0.292, 0.170, 0.797, 0.131, 0.046],
                12 => [0.3127, 0.3290, 0.680, 0.320, 0.265, 0.690, 0.150, 0.060],
                _ => throw Unknown(colorSpace, format),
            };
            curve = transfer switch
            {
                13 => Curve.Srgb,
                1 or 6 or 14 or 15 => Curve.Bt709,
                8 => new Curve(0, [1]),
                4 => new Curve(0, [2.2]),
                5 => new Curve(0, [2.8]),
                _ => throw Unknown(colorSpace, format),
            };
        }
        else if (colorSpace.PngGamma > 0 || !colorSpace.PngChromaticities.IsEmpty)
        {
            chromaticities = colorSpace.PngChromaticities.IsEmpty
                ? [0.3127, 0.3290, 0.640, 0.330, 0.300, 0.600, 0.150, 0.060]
                : ToDoubles(colorSpace.PngChromaticities);
            curve = colorSpace.PngGamma > 0 ? new Curve(0, [100_000.0 / colorSpace.PngGamma]) : Curve.Srgb;
        }
        else
        {
            throw Unknown(colorSpace, format);
        }

        return Write(colorSpace.Name, chromaticities, curve);
    }

    /// <summary>Writes a display profile.</summary>
    /// <param name="description">The profile's description.</param>
    /// <param name="chromaticities">White x, y, then red, green and blue x, y.</param>
    /// <param name="curve">The transfer curve, encoded value to linear light.</param>
    /// <returns>The profile.</returns>
    public static byte[] Write(string description, ReadOnlySpan<double> chromaticities, Curve curve)
    {
        double[] toXyz = RgbToXyz(chromaticities);
        double[] white = Xyz(chromaticities[0], chromaticities[1]);
        double[] adapt = Adaptation(white);
        double[] colorants = Multiply(adapt, toXyz);

        var tags = new List<(string Signature, byte[] Data)>
        {
            ("desc", Mluc(description)),
            ("cprt", Mluc("No copyright, use freely")),
            ("wtpt", XyzType(D50[0], D50[1], D50[2])),
            ("chad", Sf32(adapt)),
            ("rXYZ", XyzType(colorants[0], colorants[3], colorants[6])),
            ("gXYZ", XyzType(colorants[1], colorants[4], colorants[7])),
            ("bXYZ", XyzType(colorants[2], colorants[5], colorants[8])),
            ("rTRC", Para(curve)),
        };

        // gTRC and bTRC share rTRC's data, as the specification allows.
        int count = tags.Count + 2;
        int offset = 128 + 4 + (12 * count);
        var table = new List<byte>();
        var data = new List<byte>();
        int trcOffset = 0;
        int trcSize = 0;
        foreach ((string signature, byte[] bytes) in tags)
        {
            int at = offset + data.Count;
            AddEntry(table, signature, at, bytes.Length);
            if (signature == "rTRC")
            {
                (trcOffset, trcSize) = (at, bytes.Length);
            }

            data.AddRange(bytes);
            while (data.Count % 4 != 0)
            {
                data.Add(0);
            }
        }

        AddEntry(table, "gTRC", trcOffset, trcSize);
        AddEntry(table, "bTRC", trcOffset, trcSize);

        byte[] profile = new byte[offset + data.Count];
        Span<byte> header = profile.AsSpan(0, 128);
        BinaryPrimitives.WriteInt32BigEndian(header, profile.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], 0x04300000);
        "mntr"u8.CopyTo(header[12..]);
        "RGB "u8.CopyTo(header[16..]);
        "XYZ "u8.CopyTo(header[20..]);

        // A fixed date, so the same colour space always gives the same bytes.
        ReadOnlySpan<ushort> date = [2026, 1, 1, 0, 0, 0];
        for (int i = 0; i < date.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(header[(24 + (2 * i))..], date[i]);
        }

        "acsp"u8.CopyTo(header[36..]);
        WriteFixed(header[68..], D50[0]);
        WriteFixed(header[72..], D50[1]);
        WriteFixed(header[76..], D50[2]);

        BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(128), count);
        table.CopyTo(profile, 132);
        data.CopyTo(profile, offset);
        return profile;
    }

    private static NotSupportedException Unknown(ColorSpace colorSpace, string format) => new(string.Create(
        CultureInfo.InvariantCulture,
        $"The colour space {colorSpace.Name} can't be described in a {format} file yet; save it as PNG to keep it."));

    private static double[] ToDoubles(ReadOnlySpan<int> values)
    {
        double[] result = new double[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            result[i] = values[i] / 100_000.0;
        }

        return result;
    }

    private static double[] Xyz(double x, double y) => [x / y, 1, (1 - x - y) / y];

    // The matrix from linear RGB to XYZ under the space's own white (SMPTE RP 177).
    private static double[] RgbToXyz(ReadOnlySpan<double> c)
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
    private static double[] Adaptation(double[] white)
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

    private static double[] Apply(double[] m, ReadOnlySpan<double> v)
    {
        double[] result = new double[3];
        for (int row = 0; row < 3; row++)
        {
            result[row] = (m[(row * 3) + 0] * v[0]) + (m[(row * 3) + 1] * v[1]) + (m[(row * 3) + 2] * v[2]);
        }

        return result;
    }

    private static double[] Multiply(double[] a, double[] b)
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

    private static double[] Invert(double[] m)
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

    private static void AddEntry(List<byte> table, string signature, int offset, int size)
    {
        table.AddRange(Encoding.ASCII.GetBytes(signature));
        Span<byte> numbers = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(numbers, offset);
        BinaryPrimitives.WriteInt32BigEndian(numbers[4..], size);
        table.AddRange(numbers.ToArray());
    }

    private static byte[] Mluc(string text)
    {
        byte[] utf16 = Encoding.BigEndianUnicode.GetBytes(text);
        byte[] tag = new byte[28 + utf16.Length];
        "mluc"u8.CopyTo(tag);
        BinaryPrimitives.WriteInt32BigEndian(tag.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32BigEndian(tag.AsSpan(12), 12);
        "enUS"u8.CopyTo(tag.AsSpan(16));
        BinaryPrimitives.WriteInt32BigEndian(tag.AsSpan(20), utf16.Length);
        BinaryPrimitives.WriteInt32BigEndian(tag.AsSpan(24), 28);
        utf16.CopyTo(tag, 28);
        return tag;
    }

    private static byte[] XyzType(double x, double y, double z)
    {
        byte[] tag = new byte[20];
        "XYZ "u8.CopyTo(tag);
        WriteFixed(tag.AsSpan(8), x);
        WriteFixed(tag.AsSpan(12), y);
        WriteFixed(tag.AsSpan(16), z);
        return tag;
    }

    private static byte[] Sf32(double[] m)
    {
        byte[] tag = new byte[8 + 36];
        "sf32"u8.CopyTo(tag);
        for (int i = 0; i < 9; i++)
        {
            WriteFixed(tag.AsSpan(8 + (4 * i)), m[i]);
        }

        return tag;
    }

    private static byte[] Para(Curve curve)
    {
        byte[] tag = new byte[12 + (4 * curve.Parameters.Length)];
        "para"u8.CopyTo(tag);
        BinaryPrimitives.WriteUInt16BigEndian(tag.AsSpan(8), (ushort)curve.Function);
        for (int i = 0; i < curve.Parameters.Length; i++)
        {
            WriteFixed(tag.AsSpan(12 + (4 * i)), curve.Parameters[i]);
        }

        return tag;
    }

    private static void WriteFixed(Span<byte> destination, double value) =>
        BinaryPrimitives.WriteInt32BigEndian(destination, (int)Math.Round(value * 65536));

    /// <summary>A parametric curve (ICC.1, 10.18): function type and its parameters g, a, b, c, d.</summary>
    internal sealed record Curve(int Function, double[] Parameters)
    {
        /// <summary>IEC 61966-2-1: ((v + 0.055) / 1.055)^2.4 above 0.04045, v / 12.92 below.</summary>
        public static Curve Srgb { get; } = new(3, [2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045]);

        /// <summary>The inverse of BT.709's OETF, with BT.2020's precise constants.</summary>
        public static Curve Bt709 { get; } = new(3, [1 / 0.45, 1 / 1.09929682680944, 0.09929682680944 / 1.09929682680944, 1 / 4.5, 4.5 * 0.018053968510807]);
    }
}
