using System;
using System.Buffers.Binary;

namespace Zorg.Imaging.Metadata;

/// <summary>
/// Reads what colour conversion needs from an ICC profile (ICC.1:2022, and v2 profiles): an RGB matrix/TRC display
/// profile's colorants and curves, or a grey profile's curve.
/// </summary>
/// <remarks>
/// Every offset, size and count is checked against the profile's length before it's used. An RGB profile without a
/// matrix and curves, a CMYK profile, or one with a Lab connection space is read through its A2B lookup table
/// (<see cref="IccLut"/>). Anything malformed or otherwise unsupported gives <see langword="null"/>: it can't be
/// converted, and is carried through as it is.
/// </remarks>
internal static class IccProfileReader
{
    private const int MaxTags = 1024;
    private const int MaxCurveEntries = 1 << 16;

    /// <summary>A profile's model: linear RGB to the D50 connection space, and each channel's curve; or grey.</summary>
    /// <param name="ToPcs">The colorant matrix, row by row (X, Y, Z from linear R, G, B); for grey, null.</param>
    /// <param name="Curves">Encoded value to linear light for R, G and B, or the one grey curve; empty for a lookup table.</param>
    /// <param name="Lut">The device-to-PCS lookup table, when the profile is read through one; then the others are unused.</param>
    public sealed record Model(double[]? ToPcs, Func<double, double>[] Curves, IccLut? Lut = null)
    {
        public bool IsGrey => ToPcs is null && Lut is null;

        /// <summary>The device channels a lookup-table model takes: 3 for RGB, 4 for CMYK; 0 for the others.</summary>
        public int LutChannels => Lut?.Inputs ?? 0;
    }

    public static Model? Read(ReadOnlySpan<byte> profile)
    {
        if (profile.Length < 132)
        {
            return null;
        }

        uint declared = BinaryPrimitives.ReadUInt32BigEndian(profile);
        if (declared < 132 || declared > profile.Length || !profile.Slice(36, 4).SequenceEqual("acsp"u8))
        {
            return null;
        }

        profile = profile[..(int)declared];
        ReadOnlySpan<byte> space = profile.Slice(16, 4);
        bool labPcs = profile.Slice(20, 4).SequenceEqual("Lab "u8);
        if (!labPcs && !profile.Slice(20, 4).SequenceEqual("XYZ "u8))
        {
            return null;
        }

        uint count = BinaryPrimitives.ReadUInt32BigEndian(profile[128..]);
        if (count > MaxTags || 132 + (count * 12) > profile.Length)
        {
            return null;
        }

        var tags = new Tags(profile, (int)count);
        if (space.SequenceEqual("GRAY"u8))
        {
            return !labPcs && Curve(tags.Find("kTRC"u8)) is { } grey ? new Model(null, [grey]) : null;
        }

        // CMYK has only lookup tables; RGB uses them when it has no matrix and curves, which it otherwise prefers, as
        // the shaper the profile's colorimetry is defined by.
        bool cmyk = space.SequenceEqual("CMYK"u8);
        if (!cmyk && !space.SequenceEqual("RGB "u8))
        {
            return null;
        }

        if (cmyk || labPcs || tags.Find("rXYZ"u8).IsEmpty)
        {
            return Lookup(tags, labPcs, cmyk ? 4 : 3, version4: profile[8] >= 4);
        }

        double[]? r = Xyz(tags.Find("rXYZ"u8));
        double[]? g = Xyz(tags.Find("gXYZ"u8));
        double[]? b = Xyz(tags.Find("bXYZ"u8));
        Func<double, double>? rc = Curve(tags.Find("rTRC"u8));
        Func<double, double>? gc = Curve(tags.Find("gTRC"u8));
        Func<double, double>? bc = Curve(tags.Find("bTRC"u8));
        if (r is null || g is null || b is null || rc is null || gc is null || bc is null)
        {
            return null;
        }

        double[] toPcs = [r[0], g[0], b[0], r[1], g[1], b[1], r[2], g[2], b[2]];
        double determinant = (toPcs[0] * ((toPcs[4] * toPcs[8]) - (toPcs[5] * toPcs[7])))
            - (toPcs[1] * ((toPcs[3] * toPcs[8]) - (toPcs[5] * toPcs[6])))
            + (toPcs[2] * ((toPcs[3] * toPcs[7]) - (toPcs[4] * toPcs[6])));
        return Math.Abs(determinant) < 1e-6 ? null : new Model(toPcs, [rc, gc, bc]);
    }

    // The perceptual A2B table, else the colorimetric one, else saturation, as LittleCMS falls back for the default
    // intent. A version 4 perceptual or saturation table has the reference medium's black (see IccLut.ScalesBlack).
    private static Model? Lookup(Tags tags, bool labPcs, int channels, bool version4)
    {
        IccLut? lut = IccLut.Read(tags.Find("A2B0"u8), labPcs, version4)
            ?? IccLut.Read(tags.Find("A2B1"u8), labPcs, scalesBlack: false)
            ?? IccLut.Read(tags.Find("A2B2"u8), labPcs, version4);
        return lut is { } found && found.Inputs == channels ? new Model(null, [], found) : null;
    }

    private static double[]? Xyz(ReadOnlySpan<byte> tag)
    {
        if (tag.Length < 20 || !tag[..4].SequenceEqual("XYZ "u8))
        {
            return null;
        }

        return [Fixed(tag[8..]), Fixed(tag[12..]), Fixed(tag[16..])];
    }

    /// <summary>The bytes a curv or para element takes, before padding; 0 when it isn't one or its header is cut short.</summary>
    internal static int CurveSize(ReadOnlySpan<byte> element)
    {
        if (element.Length < 12)
        {
            return 0;
        }

        if (element[..4].SequenceEqual("curv"u8))
        {
            uint entries = BinaryPrimitives.ReadUInt32BigEndian(element[8..]);
            return entries > MaxCurveEntries ? 0 : 12 + (2 * (int)entries);
        }

        if (element[..4].SequenceEqual("para"u8))
        {
            int function = BinaryPrimitives.ReadUInt16BigEndian(element[8..]);
            int parameters = function switch { 0 => 1, 1 => 3, 2 => 4, 3 => 5, 4 => 7, _ => -1 };
            return parameters < 0 ? 0 : 12 + (4 * parameters);
        }

        return 0;
    }

    // A curv or para tag as a function from encoded value to linear light, both 0 to 1.
    internal static Func<double, double>? Curve(ReadOnlySpan<byte> tag)
    {
        if (tag.Length < 12)
        {
            return null;
        }

        if (tag[..4].SequenceEqual("curv"u8))
        {
            uint entries = BinaryPrimitives.ReadUInt32BigEndian(tag[8..]);
            if (entries > MaxCurveEntries || 12 + (2 * entries) > tag.Length)
            {
                return null;
            }

            if (entries == 0)
            {
                return v => v;
            }

            if (entries == 1)
            {
                double gamma = BinaryPrimitives.ReadUInt16BigEndian(tag[12..]) / 256.0;
                return v => Math.Pow(v, gamma);
            }

            double[] table = new double[entries];
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = BinaryPrimitives.ReadUInt16BigEndian(tag[(12 + (2 * i))..]) / 65535.0;
            }

            return v =>
            {
                double at = Math.Clamp(v, 0, 1) * (table.Length - 1);
                int i = Math.Min((int)at, table.Length - 2);
                double t = at - i;
                return (table[i] * (1 - t)) + (table[i + 1] * t);
            };
        }

        if (!tag[..4].SequenceEqual("para"u8))
        {
            return null;
        }

        int function = BinaryPrimitives.ReadUInt16BigEndian(tag[8..]);
        int parameters = function switch { 0 => 1, 1 => 3, 2 => 4, 3 => 5, 4 => 7, _ => -1 };
        if (parameters < 0 || 12 + (4 * parameters) > tag.Length)
        {
            return null;
        }

        double[] p = new double[7];
        for (int i = 0; i < parameters; i++)
        {
            p[i] = Fixed(tag[(12 + (4 * i))..]);
        }

        double g = p[0], a = p[1], b = p[2], c = p[3], d = p[4], e = p[5], f = p[6];
        return function switch
        {
            0 => v => Math.Pow(v, g),
            1 => v => v >= -b / a ? Math.Pow((a * v) + b, g) : 0,
            2 => v => v >= -b / a ? Math.Pow((a * v) + b, g) + c : c,
            3 => v => v >= d ? Math.Pow((a * v) + b, g) : c * v,
            _ => v => v >= d ? Math.Pow((a * v) + b, g) + e : (c * v) + f,
        };
    }

    private static double Fixed(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadInt32BigEndian(value) / 65536.0;

    // The tag table, looked up by signature, each tag's data checked to lie inside the profile.
    private readonly ref struct Tags(ReadOnlySpan<byte> profile, int count)
    {
        private readonly ReadOnlySpan<byte> _profile = profile;

        public ReadOnlySpan<byte> Find(ReadOnlySpan<byte> signature)
        {
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> entry = _profile.Slice(132 + (12 * i), 12);
                if (!entry[..4].SequenceEqual(signature))
                {
                    continue;
                }

                uint offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
                uint size = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
                return (ulong)offset + size <= (ulong)_profile.Length ? _profile.Slice((int)offset, (int)size) : default;
            }

            return default;
        }
    }
}
