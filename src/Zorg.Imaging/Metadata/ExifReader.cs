using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Zorg.Imaging.Metadata;

/// <summary>
/// Reads the IFDs of an EXIF block (a TIFF structure): entries whose values don't fit are left out, and a block whose
/// header or IFD can't be read gives null. It never throws for bad input.
/// </summary>
internal static class ExifReader
{
    public const ushort ExifPointer = 0x8769;
    public const ushort GpsPointer = 0x8825;
    private const int MaxEntries = 512;

    /// <summary>The main, Exif and GPS IFDs by tag, and the byte order; null when the block can't be read.</summary>
    public static Tags? Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < 8)
        {
            return null;
        }

        bool little;
        if (source[0] == 'I' && source[1] == 'I')
        {
            little = true;
        }
        else if (source[0] == 'M' && source[1] == 'M')
        {
            little = false;
        }
        else
        {
            return null;
        }

        var reader = new Reader(source, little);
        if (reader.U16(2) != 42 || reader.Ifd(reader.U32(4)) is not { } main)
        {
            return null;
        }

        return new Tags(little, ByTag(main), ByTag(Child(reader, main, ExifPointer)), ByTag(Child(reader, main, GpsPointer)));
    }

    private static Dictionary<ushort, Entry> ByTag(List<Entry>? entries)
    {
        var tags = new Dictionary<ushort, Entry>();
        foreach (Entry entry in entries ?? [])
        {
            tags.TryAdd(entry.Tag, entry);
        }

        return tags;
    }

    /// <summary>The tags of the three IFDs that hold what <see cref="Zorg.Imaging.ImageMetadata"/> reports.</summary>
    public sealed class Tags(bool little, Dictionary<ushort, Entry> main, Dictionary<ushort, Entry> exif, Dictionary<ushort, Entry> gps)
    {
        public Dictionary<ushort, Entry> Main { get; } = main;

        public Dictionary<ushort, Entry> Exif { get; } = exif;

        public Dictionary<ushort, Entry> Gps { get; } = gps;

        /// <summary>An ASCII tag as text: UTF-8 (which ASCII is part of), parts split by NUL joined with "; ", trimmed.</summary>
        public static string? Text(Dictionary<ushort, Entry> ifd, ushort tag)
        {
            if (!ifd.TryGetValue(tag, out Entry? entry) || entry.Type is not (2 or 7))
            {
                return null;
            }

            var parts = new List<string>();
            foreach (string part in System.Text.Encoding.UTF8.GetString(entry.Value).Split('\0'))
            {
                string trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    parts.Add(trimmed);
                }
            }

            return parts.Count == 0 ? null : string.Join("; ", parts);
        }

        /// <summary>A SHORT or LONG tag's first value.</summary>
        public uint? Number(Dictionary<ushort, Entry> ifd, ushort tag)
        {
            if (!ifd.TryGetValue(tag, out Entry? entry) || entry.Count < 1)
            {
                return null;
            }

            return entry.Type switch
            {
                1 => entry.Value[0],
                3 => little ? BinaryPrimitives.ReadUInt16LittleEndian(entry.Value) : BinaryPrimitives.ReadUInt16BigEndian(entry.Value),
                4 => little ? BinaryPrimitives.ReadUInt32LittleEndian(entry.Value) : BinaryPrimitives.ReadUInt32BigEndian(entry.Value),
                _ => null,
            };
        }

        /// <summary>A RATIONAL tag's values as doubles, or null when it isn't one or a denominator is 0.</summary>
        public double[]? Rationals(Dictionary<ushort, Entry> ifd, ushort tag)
        {
            if (!ifd.TryGetValue(tag, out Entry? entry) || entry.Type != 5 || entry.Count < 1)
            {
                return null;
            }

            double[] values = new double[entry.Count];
            for (int i = 0; i < values.Length; i++)
            {
                ReadOnlySpan<byte> pair = entry.Value.AsSpan(i * 8, 8);
                uint numerator = little ? BinaryPrimitives.ReadUInt32LittleEndian(pair) : BinaryPrimitives.ReadUInt32BigEndian(pair);
                uint denominator = little ? BinaryPrimitives.ReadUInt32LittleEndian(pair[4..]) : BinaryPrimitives.ReadUInt32BigEndian(pair[4..]);
                if (denominator == 0)
                {
                    return null;
                }

                values[i] = (double)numerator / denominator;
            }

            return values;
        }
    }

    public static List<Entry>? Child(Reader reader, List<Entry> parent, ushort pointer)
    {
        Entry? entry = parent.Find(e => e.Tag == pointer);
        if (entry is null || entry.Type is not (4 or 13) || entry.Value.Length < 4)
        {
            return null;
        }

        return reader.Ifd(reader.U32(entry.Value, 0));
    }

    public static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 => 8,
        _ => 0,
    };

    // A tag with its value bytes in the source's byte order.
    public sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Value);

    public readonly ref struct Reader(ReadOnlySpan<byte> data, bool little)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public ushort U16(int at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(_data[at..]) : BinaryPrimitives.ReadUInt16BigEndian(_data[at..]);

        public uint U32(int at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(_data[at..]) : BinaryPrimitives.ReadUInt32BigEndian(_data[at..]);

        public ushort U16(byte[] value, int at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(value.AsSpan(at));

        public uint U32(byte[] value, int at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(value.AsSpan(at));

        // The entries of the IFD at an offset, or null when its header doesn't fit. Entries whose values don't fit are
        // left out.
        public List<Entry>? Ifd(uint offset)
        {
            if (offset < 8 || offset > _data.Length - 2)
            {
                return null;
            }

            int count = U16((int)offset);
            if (count > MaxEntries || offset + 2 + (count * 12L) > _data.Length)
            {
                return null;
            }

            var entries = new List<Entry>(count);
            var seen = new HashSet<ushort>();
            for (int i = 0; i < count; i++)
            {
                int at = (int)offset + 2 + (i * 12);
                ushort tag = U16(at);
                ushort type = U16(at + 2);
                uint items = U32(at + 4);
                int size = TypeSize(type);
                long bytes = size * (long)items;
                if (size == 0 || bytes > _data.Length || !seen.Add(tag))
                {
                    continue;
                }

                long valueAt = bytes <= 4 ? at + 8 : U32(at + 8);
                if (valueAt + bytes > _data.Length)
                {
                    continue;
                }

                entries.Add(new Entry(tag, type, items, _data.Slice((int)valueAt, (int)bytes).ToArray()));
            }

            return entries;
        }
    }
}
