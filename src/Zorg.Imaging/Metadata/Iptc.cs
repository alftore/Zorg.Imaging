using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Zorg.Imaging.Metadata;

/// <summary>
/// IPTC IIM records (IPTC-NAA Information Interchange Model 4.2), as JPEG files carry them inside a Photoshop APP13
/// segment (image resource 0x0404). Reading never throws: a record that doesn't fit ends the list.
/// </summary>
internal static class Iptc
{
    /// <summary>The APP13 segment's signature.</summary>
    public static ReadOnlySpan<byte> PhotoshopSignature => "Photoshop 3.0\0"u8;

    private const ushort IptcResource = 0x0404;

    // Record 2 datasets left out with the matching ExifGroup: places (and the creator's contact details, which often
    // hold an address), and dates and times.
    private static readonly HashSet<byte> LocationDataSets = [26, 27, 90, 92, 95, 100, 101, 118];

    private static readonly HashSet<byte> DateDataSets = [30, 35, 37, 38, 55, 60, 62, 63];

    /// <summary>One dataset: its record, its number and its value.</summary>
    public readonly record struct DataSet(byte Record, byte Number, byte[] Value);

    /// <summary>The IIM data in the resources of one or more Photoshop APP13 payloads (after the signature), or null.</summary>
    public static byte[]? FromPhotoshop(ReadOnlySpan<byte> resources)
    {
        int at = 0;
        while (at + 12 <= resources.Length && resources.Slice(at, 4).SequenceEqual("8BIM"u8))
        {
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(resources[(at + 4)..]);

            // A Pascal-string name, padded so the name and its length byte take an even number of bytes.
            int nameLength = resources[at + 6];
            int sizeAt = at + 6 + ((nameLength + 2) & ~1);
            if (sizeAt + 4 > resources.Length)
            {
                return null;
            }

            uint size = BinaryPrimitives.ReadUInt32BigEndian(resources[sizeAt..]);
            int dataAt = sizeAt + 4;
            if (size > (uint)(resources.Length - dataAt))
            {
                return null;
            }

            if (id == IptcResource)
            {
                return resources.Slice(dataAt, (int)size).ToArray();
            }

            at = dataAt + (int)((size + 1) & ~1u);
        }

        return null;
    }

    /// <summary>The datasets in IIM data, up to the first that doesn't fit.</summary>
    public static List<DataSet> Read(ReadOnlySpan<byte> iim)
    {
        var sets = new List<DataSet>();
        int at = 0;
        while (at + 5 <= iim.Length && iim[at] == 0x1C)
        {
            byte record = iim[at + 1];
            byte number = iim[at + 2];
            int length = BinaryPrimitives.ReadUInt16BigEndian(iim[(at + 3)..]);
            at += 5;
            if ((length & 0x8000) != 0)
            {
                // An extended dataset: the low bits say how many bytes hold the length (up to 4 here).
                int bytes = length & 0x7FFF;
                if (bytes is < 1 or > 4 || at + bytes > iim.Length)
                {
                    break;
                }

                long extended = 0;
                for (int i = 0; i < bytes; i++)
                {
                    extended = (extended << 8) | iim[at + i];
                }

                at += bytes;
                if (extended > iim.Length - at)
                {
                    break;
                }

                length = (int)extended;
            }

            if (length > iim.Length - at)
            {
                break;
            }

            sets.Add(new DataSet(record, number, iim.Slice(at, length).ToArray()));
            at += length;
        }

        return sets;
    }

    /// <summary>
    /// The text of record 2 datasets: UTF-8 when record 1 says so (dataset 90, ESC % G), otherwise Latin-1, which
    /// older writers' single-byte text reads as with the fewest surprises.
    /// </summary>
    public static Encoding TextEncoding(List<DataSet> sets)
    {
        foreach (DataSet set in sets)
        {
            if (set.Record == 1 && set.Number == 90)
            {
                return set.Value.AsSpan().SequenceEqual((ReadOnlySpan<byte>)[0x1B, 0x25, 0x47]) ? Encoding.UTF8 : Encoding.Latin1;
            }
        }

        return Encoding.Latin1;
    }

    /// <summary>The values of every record 2 dataset with this number, as text, trimmed; empty ones left out.</summary>
    public static List<string> Texts(List<DataSet> sets, byte number)
    {
        Encoding encoding = TextEncoding(sets);
        var texts = new List<string>();
        foreach (DataSet set in sets)
        {
            if (set.Record == 2 && set.Number == number)
            {
                string text = encoding.GetString(set.Value).Trim().TrimEnd('\0');
                if (text.Length > 0)
                {
                    texts.Add(text);
                }
            }
        }

        return texts;
    }

    /// <summary>
    /// The IIM data to write under <paramref name="options"/>: the character set and record version, and the record 2
    /// datasets the options keep; the envelope (record 1) and records 3 and up are left out. Null when nothing is left.
    /// </summary>
    public static byte[]? Filter(ReadOnlySpan<byte> iim, MetadataOptions options)
    {
        var output = new List<byte>();
        bool any = false;
        foreach (DataSet set in Read(iim))
        {
            bool keep = (set.Record, set.Number) switch
            {
                (1, 90) or (2, 0) => true,
                (2, 80) or (2, 116) => true,
                (2, _) when options.IsCopyrightOnly => false,
                (2, byte n) when LocationDataSets.Contains(n) => (options.Excluded & ExifGroup.Location) == 0,
                (2, byte n) when DateDataSets.Contains(n) => (options.Excluded & ExifGroup.Dates) == 0,
                (2, _) => true,
                _ => false,
            };
            if (!keep || set.Value.Length > 0x7FFF)
            {
                continue;
            }

            any |= set.Record == 2 && set.Number != 0;
            output.AddRange([0x1C, set.Record, set.Number, (byte)(set.Value.Length >> 8), (byte)set.Value.Length]);
            output.AddRange(set.Value);
        }

        return any ? [.. output] : null;
    }

    /// <summary>A Photoshop APP13 payload holding the IIM data as its one resource.</summary>
    public static byte[] ToPhotoshop(ReadOnlySpan<byte> iim)
    {
        var payload = new List<byte>(PhotoshopSignature.Length + 12 + iim.Length + 1);
        payload.AddRange(PhotoshopSignature);
        payload.AddRange("8BIM"u8);
        payload.AddRange([IptcResource >> 8, IptcResource & 0xFF, 0, 0]);
        payload.AddRange([(byte)(iim.Length >> 24), (byte)(iim.Length >> 16), (byte)(iim.Length >> 8), (byte)iim.Length]);
        payload.AddRange(iim);
        if ((iim.Length & 1) != 0)
        {
            payload.Add(0);
        }

        return [.. payload];
    }
}
