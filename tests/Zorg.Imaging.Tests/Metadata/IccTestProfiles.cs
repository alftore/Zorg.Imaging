using System;
using System.Collections.Generic;
using System.Text;
using Zorg.Imaging.Metadata;

namespace Zorg.Imaging.Tests.Metadata;

/// <summary>
/// Lookup-table ICC profiles made for the tests, whose meaning is known: RGB ones that describe plain sRGB, in each
/// tag type and connection space, and a CMYK one for the naive conversion R = (1 - C)(1 - K).
/// </summary>
public static class IccTestProfiles
{
    public enum TagType
    {
        Lut8,
        Lut16,
        AToB,
    }

    private static readonly double[] SrgbToPcs = ColourMath.RgbToPcs(ColourMath.Chromaticities(1)!);

    /// <summary>sRGB as a lookup-table profile: the sRGB curve in the input curves, the matrix in the table.</summary>
    public static byte[] SrgbLut(TagType type, bool lab, int points = 9)
    {
        // For XYZ the curve goes in front, so the table holds a linear function of its inputs, which interpolates
        // exactly. For Lab the table is sampled on encoded values, as real Lab profiles are: Lab's cube root changes
        // too fast near black for a grid in linear light.
        // A version 4 table (lutAToB) puts device black at the perceptual reference medium's black, as real ones do.
        bool v4 = type == TagType.AToB;
        byte[] tag = lab
            ? Tag(type, 3, points, lab, v => v, device => Lab(Medium(ColourMath.Apply(SrgbToPcs, [Linearise(device[0]), Linearise(device[1]), Linearise(device[2])]), v4)))
            : Tag(type, 3, points, lab, Linearise, device => Medium(ColourMath.Apply(SrgbToPcs, device), v4));
        return Profile("RGB ", lab, type == TagType.AToB, tag);
    }

    /// <summary>A CMYK profile for the naive conversion: sRGB R = (1 - C)(1 - K), and so on, ink 0 to 1.</summary>
    public static byte[] NaiveCmyk(TagType type = TagType.Lut16, int points = 9)
    {
        byte[] tag = Tag(type, 4, points, lab: true, v => v, device =>
        {
            double k = device[3];
            double[] linear = [Linearise((1 - device[0]) * (1 - k)), Linearise((1 - device[1]) * (1 - k)), Linearise((1 - device[2]) * (1 - k))];
            return Lab(Medium(ColourMath.Apply(SrgbToPcs, linear), type == TagType.AToB));
        });
        return Profile("CMYK", lab: true, v4: type == TagType.AToB, tag);
    }

    // XYZ from black to white mapped onto the version 4 perceptual reference medium, black at L* 3.1373.
    private static double[] Medium(double[] xyz, bool v4)
    {
        if (!v4)
        {
            return xyz;
        }

        double[] black = [0.00336, 0.0034731, 0.00287];
        double[] white = [0.9642, 1.0, 0.8249];
        return [black[0] + (xyz[0] * (white[0] - black[0]) / white[0]), black[1] + (xyz[1] * (white[1] - black[1]) / white[1]), black[2] + (xyz[2] * (white[2] - black[2]) / white[2])];
    }

    private static double Linearise(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    private static double[] Lab(double[] xyz)
    {
        static double F(double t) => t > Math.Pow(6.0 / 29, 3) ? Math.Cbrt(t) : (t / (3 * Math.Pow(6.0 / 29, 2))) + (4.0 / 29);
        double fx = F(xyz[0] / 0.9642), fy = F(xyz[1] / 1.0), fz = F(xyz[2] / 0.8249);
        return [(116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz)];
    }

    // The PCS value as the 0 to 1 the tag type stores.
    private static double Encode(TagType type, bool lab, int channel, double value) => (type, lab) switch
    {
        (_, false) => value * 32768 / 65535,
        (TagType.Lut8, true) => channel == 0 ? value / 100 : (value + 128) / 255,
        (TagType.Lut16, true) => channel == 0 ? value / 100 * 65280 / 65535 : (value + 128) * 256 / 65535,
        _ => channel == 0 ? value / 100 : (value + 128) / 255,
    };

    private static byte[] Tag(TagType type, int inputs, int points, bool lab, Func<double, double> inputCurve, Func<double[], double[]> table)
    {
        var data = new List<byte>();
        int nodes = (int)Math.Pow(points, inputs);
        double[][] values = new double[nodes][];
        for (int node = 0; node < nodes; node++)
        {
            double[] device = new double[inputs];
            int rest = node;
            for (int d = inputs - 1; d >= 0; d--)
            {
                device[d] = (double)(rest % points) / (points - 1);
                rest /= points;
            }

            values[node] = table(device);
        }

        if (type == TagType.AToB)
        {
            // B curves (identity), no matrix or M curves, the table, then A curves sampled in curv elements.
            byte[] identity = Curv([]);
            byte[] a = Curv(Sample(inputCurve, 4096));
            int bOffset = 32;
            int tableOffset = bOffset + (3 * identity.Length);
            var clut = new List<byte>();
            for (int i = 0; i < 16; i++)
            {
                clut.Add(i < inputs ? (byte)points : (byte)0);
            }

            clut.AddRange([2, 0, 0, 0]);
            foreach (double[] node in values)
            {
                for (int c = 0; c < 3; c++)
                {
                    clut.AddRange(U16(Encode(type, lab, c, node[c])));
                }
            }

            while (clut.Count % 4 != 0)
            {
                clut.Add(0);
            }

            int aOffset = tableOffset + clut.Count;
            data.AddRange("mAB "u8.ToArray());
            data.AddRange(new byte[4]);
            data.AddRange([(byte)inputs, 3, 0, 0]);
            data.AddRange(Be32(bOffset));
            data.AddRange(Be32(0));
            data.AddRange(Be32(0));
            data.AddRange(Be32(tableOffset));
            data.AddRange(Be32(aOffset));
            for (int i = 0; i < 3; i++)
            {
                data.AddRange(identity);
            }

            data.AddRange(clut);
            for (int i = 0; i < inputs; i++)
            {
                data.AddRange(a);
            }

            return [.. data];
        }

        bool sixteen = type == TagType.Lut16;
        data.AddRange(sixteen ? "mft2"u8.ToArray() : "mft1"u8.ToArray());
        data.AddRange(new byte[4]);
        data.AddRange([(byte)inputs, 3, (byte)points, 0]);
        foreach (double m in (double[])[1, 0, 0, 0, 1, 0, 0, 0, 1])
        {
            data.AddRange(Be32((int)(m * 65536)));
        }

        int entries = sixteen ? 4096 : 256;
        if (sixteen)
        {
            data.AddRange([(byte)(entries >> 8), (byte)entries, (byte)(entries >> 8), (byte)entries]);
        }

        for (int i = 0; i < inputs; i++)
        {
            foreach (double v in Sample(inputCurve, entries))
            {
                data.AddRange(sixteen ? U16(v) : [(byte)Math.Round(v * 255)]);
            }
        }

        foreach (double[] node in values)
        {
            for (int c = 0; c < 3; c++)
            {
                double v = Math.Clamp(Encode(type, lab, c, node[c]), 0, 1);
                data.AddRange(sixteen ? U16(v) : [(byte)Math.Round(v * 255)]);
            }
        }

        for (int c = 0; c < 3; c++)
        {
            foreach (double v in Sample(x => x, entries))
            {
                data.AddRange(sixteen ? U16(v) : [(byte)Math.Round(v * 255)]);
            }
        }

        return [.. data];
    }

    private static double[] Sample(Func<double, double> curve, int entries)
    {
        double[] values = new double[entries];
        for (int i = 0; i < entries; i++)
        {
            values[i] = curve((double)i / (entries - 1));
        }

        return values;
    }

    private static byte[] Curv(double[] table)
    {
        var data = new List<byte>("curv"u8.ToArray());
        data.AddRange(new byte[4]);
        data.AddRange(Be32(table.Length));
        foreach (double v in table)
        {
            data.AddRange(U16(v));
        }

        while (data.Count % 4 != 0)
        {
            data.Add(0);
        }

        return [.. data];
    }

    // A profile with desc, wtpt, cprt and the A2B0 tag.
    private static byte[] Profile(string space, bool lab, bool v4, byte[] a2b0)
    {
        byte[] desc = [.. "desc"u8, 0, 0, 0, 0, .. Be32(5), .. "Test\0"u8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. new byte[67]];
        byte[] wtpt = [.. "XYZ "u8, 0, 0, 0, 0, .. Be32(63189), .. Be32(65536), .. Be32(54061)];
        byte[] cprt = [.. "text"u8, 0, 0, 0, 0, .. "No copyright\0"u8];
        (string Signature, byte[] Data)[] tags = [("desc", desc), ("wtpt", wtpt), ("cprt", cprt), ("A2B0", a2b0)];
        int offset = 128 + 4 + (12 * tags.Length);
        var table = new List<byte>(Be32(tags.Length));
        var body = new List<byte>();
        foreach ((string signature, byte[] data) in tags)
        {
            table.AddRange(Encoding.ASCII.GetBytes(signature));
            table.AddRange(Be32(offset + body.Count));
            table.AddRange(Be32(data.Length));
            body.AddRange(data);
            while (body.Count % 4 != 0)
            {
                body.Add(0);
            }
        }

        byte[] header = new byte[128];
        int size = 128 + table.Count + body.Count;
        Be32(size).CopyTo(header, 0);
        (v4 ? new byte[] { 4, 0x30, 0, 0 } : [2, 0x10, 0, 0]).CopyTo(header, 8);
        Encoding.ASCII.GetBytes(space == "CMYK" ? "prtr" : "mntr").CopyTo(header, 12);
        Encoding.ASCII.GetBytes(space).CopyTo(header, 16);
        Encoding.ASCII.GetBytes(lab ? "Lab " : "XYZ ").CopyTo(header, 20);
        "acsp"u8.CopyTo(header.AsSpan(36));
        Be32(63189).CopyTo(header, 68);
        Be32(65536).CopyTo(header, 72);
        Be32(54061).CopyTo(header, 76);
        return [.. header, .. table, .. body];
    }

    private static byte[] U16(double v)
    {
        int value = (int)Math.Round(Math.Clamp(v, 0, 1) * 65535);
        return [(byte)(value >> 8), (byte)value];
    }

    private static byte[] Be32(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
}
