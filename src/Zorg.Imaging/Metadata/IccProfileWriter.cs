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
            chromaticities = ColourMath.Chromaticities(primaries) ?? throw Unknown(colorSpace, format);
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
                ? ColourMath.Chromaticities(1)!
                : ColourMath.ToDoubles(colorSpace.PngChromaticities);
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
        double[] toXyz = ColourMath.RgbToXyz(chromaticities);
        double[] white = ColourMath.Xyz(chromaticities[0], chromaticities[1]);
        double[] adapt = ColourMath.Adaptation(white);
        double[] colorants = ColourMath.Multiply(adapt, toXyz);

        var tags = new List<(string Signature, byte[] Data)>
        {
            ("desc", Mluc(description)),
            ("cprt", Mluc("No copyright, use freely")),
            ("wtpt", XyzType(ColourMath.D50[0], ColourMath.D50[1], ColourMath.D50[2])),
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
        WriteFixed(header[68..], ColourMath.D50[0]);
        WriteFixed(header[72..], ColourMath.D50[1]);
        WriteFixed(header[76..], ColourMath.D50[2]);

        BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(128), count);
        table.CopyTo(profile, 132);
        data.CopyTo(profile, offset);
        return profile;
    }

    private static NotSupportedException Unknown(ColorSpace colorSpace, string format) => new(string.Create(
        CultureInfo.InvariantCulture,
        $"The colour space {colorSpace.Name} can't be described in a {format} file yet; save it as PNG to keep it."));

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
