using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Zorg.Imaging.Metadata;

/// <summary>
/// Rebuilds an EXIF block (CIPA DC-008, a TIFF structure) for writing: the main IFD with the Exif, GPS and
/// interoperability IFDs under it, filtered by <see cref="MetadataOptions"/>, with the orientation and pixel dimensions
/// made true of the image being saved. The thumbnail IFD and the maker note are dropped.
/// </summary>
/// <remarks>
/// Values are copied out of the source and laid out afresh, so no offset from the old file survives. Every count,
/// offset and size is checked against the block; anything that doesn't fit, or a type that isn't TIFF's, drops that
/// entry, and a block whose header or main IFD can't be read gives null. It never throws for bad input.
/// </remarks>
internal static class ExifWriter
{
    private const ushort ExifPointer = 0x8769;
    private const ushort GpsPointer = 0x8825;
    private const ushort InteropPointer = 0xA005;
    private const ushort Orientation = 0x0112;
    private const ushort PixelXDimension = 0xA002;
    private const ushort PixelYDimension = 0xA003;
    private const int MaxEntries = 512;

    // Main IFD tags that only describe image data in a TIFF file, point at blocks not copied, or carry other metadata.
    private static readonly HashSet<ushort> MainDropped =
    [
        0x0100, 0x0101, 0x0102, 0x0103, 0x0106, 0x0111, 0x0115, 0x0116, 0x0117, 0x011C, 0x0144, 0x0145, 0x014A, 0x0201,
        0x0202, 0x02BC, 0x8773, ExifPointer, GpsPointer,
    ];

    private static readonly HashSet<ushort> CameraTags = [0x010F, 0x0110, 0xC62F, 0xA430, 0xA431, 0xA432, 0xA433, 0xA434, 0xA435];

    private static readonly HashSet<ushort> DateTags = [0x0132, 0x9003, 0x9004, 0x9010, 0x9011, 0x9012, 0x9290, 0x9291, 0x9292];

    private static readonly HashSet<ushort> GpsDateTags = [0x0007, 0x001D];

    /// <summary>The rebuilt block, or null when there's nothing to write or the source can't be read.</summary>
    /// <param name="source">The source block, a TIFF structure.</param>
    /// <param name="width">The saved image's width.</param>
    /// <param name="height">The saved image's height.</param>
    /// <param name="resetOrientation">Whether to write orientation 1, because the pixels have been turned.</param>
    /// <param name="options">What to keep.</param>
    public static byte[]? Rewrite(ReadOnlySpan<byte> source, int width, int height, bool resetOrientation, MetadataOptions options)
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
        if (reader.U16(2) != 42)
        {
            return null;
        }

        List<Entry>? main = reader.Ifd(reader.U32(4));
        if (main is null)
        {
            return null;
        }

        List<Entry>? exif = null;
        List<Entry>? interop = null;
        List<Entry>? gps = null;
        if (!options.IsCopyrightOnly)
        {
            exif = Child(reader, main, ExifPointer);
            interop = exif is null ? null : Child(reader, exif, InteropPointer);
            gps = (options.Excluded & ExifGroup.Location) != 0 ? null : Child(reader, main, GpsPointer);
        }

        bool dropCamera = (options.Excluded & ExifGroup.Camera) != 0;
        bool dropDates = (options.Excluded & ExifGroup.Dates) != 0;
        var writer = new Writer(little);

        var mainOut = new List<Entry>();
        foreach (Entry entry in main)
        {
            if (entry.Tag == Orientation)
            {
                ushort value = resetOrientation || entry.Type != 3 || entry.Value.Length < 2 ? (ushort)1 : reader.U16(entry.Value, 0);
                if (!options.IsCopyrightOnly || value != 1)
                {
                    mainOut.Add(writer.Short(Orientation, value));
                }

                continue;
            }

            bool keep = options.IsCopyrightOnly
                ? entry.Tag is 0x013B or 0x8298
                : !MainDropped.Contains(entry.Tag) && !(dropCamera && CameraTags.Contains(entry.Tag)) && !(dropDates && DateTags.Contains(entry.Tag))
                    && !(entry.Tag == 0x83BB && options.Excluded != ExifGroup.None);
            if (keep)
            {
                mainOut.Add(entry);
            }
        }

        List<Entry>? exifOut = null;
        if (exif is not null)
        {
            exifOut = [];
            foreach (Entry entry in exif)
            {
                if (entry.Tag is 0x927C or InteropPointer or PixelXDimension or PixelYDimension
                    || (dropCamera && CameraTags.Contains(entry.Tag)) || (dropDates && DateTags.Contains(entry.Tag)))
                {
                    continue;
                }

                exifOut.Add(entry);
            }

            exifOut.Add(writer.Long(PixelXDimension, (uint)width));
            exifOut.Add(writer.Long(PixelYDimension, (uint)height));
        }

        List<Entry>? gpsOut = gps?.FindAll(entry => !(dropDates && GpsDateTags.Contains(entry.Tag)));
        if (mainOut.Count == 0 && exifOut is null && (gpsOut is null || gpsOut.Count == 0))
        {
            return null;
        }

        return writer.Write(mainOut, exifOut, interop, gpsOut);
    }

    private static List<Entry>? Child(Reader reader, List<Entry> parent, ushort pointer)
    {
        Entry? entry = parent.Find(e => e.Tag == pointer);
        if (entry is null || entry.Type is not (4 or 13) || entry.Value.Length < 4)
        {
            return null;
        }

        return reader.Ifd(reader.U32(entry.Value, 0));
    }

    private static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 => 8,
        _ => 0,
    };

    // A tag with its value bytes in the source's byte order.
    private sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Value);

    private readonly ref struct Reader(ReadOnlySpan<byte> data, bool little)
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

    private sealed class Writer(bool little)
    {
        private readonly List<byte> _out = [];

        public Entry Short(ushort tag, ushort value)
        {
            byte[] bytes = new byte[2];
            if (little)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
            }

            return new Entry(tag, 3, 1, bytes);
        }

        public Entry Long(ushort tag, uint value) => new(tag, 4, 1, U32Bytes(value));

        public byte[] Write(List<Entry> main, List<Entry>? exif, List<Entry>? interop, List<Entry>? gps)
        {
            _out.AddRange(little ? "II"u8 : "MM"u8);
            _out.AddRange(U16Bytes(42));
            _out.AddRange(U32Bytes(8));

            // Pointers are written as placeholders and filled in once the IFD they point at has a place.
            if (exif is not null)
            {
                main.Add(Long(ExifPointer, 0));
            }

            if (gps is { Count: > 0 })
            {
                main.Add(Long(GpsPointer, 0));
            }

            if (interop is { Count: > 0 } && exif is not null)
            {
                exif.Add(Long(InteropPointer, 0));
            }

            Dictionary<ushort, int> mainPointers = WriteIfd(main);
            if (exif is not null)
            {
                Patch(mainPointers[ExifPointer], _out.Count);
                Dictionary<ushort, int> exifPointers = WriteIfd(exif);
                if (interop is { Count: > 0 })
                {
                    Patch(exifPointers[InteropPointer], _out.Count);
                    WriteIfd(interop);
                }
            }

            if (gps is { Count: > 0 })
            {
                Patch(mainPointers[GpsPointer], _out.Count);
                WriteIfd(gps);
            }

            return [.. _out];
        }

        // Writes an IFD at the current (even) position, its out-of-line values after it, and returns where each
        // pointer entry's value went.
        private Dictionary<ushort, int> WriteIfd(List<Entry> entries)
        {
            entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));
            int start = _out.Count;
            int valuesAt = start + 2 + (entries.Count * 12) + 4;
            var pointers = new Dictionary<ushort, int>();
            var values = new List<byte>();
            _out.AddRange(U16Bytes((ushort)entries.Count));
            foreach (Entry entry in entries)
            {
                _out.AddRange(U16Bytes(entry.Tag));
                _out.AddRange(U16Bytes(entry.Type));
                _out.AddRange(U32Bytes(entry.Count));
                if (entry.Tag is ExifPointer or GpsPointer or InteropPointer)
                {
                    pointers[entry.Tag] = _out.Count;
                }

                if (entry.Value.Length <= 4)
                {
                    _out.AddRange(entry.Value);
                    for (int i = entry.Value.Length; i < 4; i++)
                    {
                        _out.Add(0);
                    }
                }
                else
                {
                    _out.AddRange(U32Bytes((uint)(valuesAt + values.Count)));
                    values.AddRange(entry.Value);
                    if ((values.Count & 1) != 0)
                    {
                        values.Add(0);
                    }
                }
            }

            _out.AddRange(U32Bytes(0));
            _out.AddRange(values);
            return pointers;
        }

        private void Patch(int at, int value)
        {
            byte[] bytes = U32Bytes((uint)value);
            for (int i = 0; i < 4; i++)
            {
                _out[at + i] = bytes[i];
            }
        }

        private byte[] U16Bytes(ushort value)
        {
            byte[] bytes = new byte[2];
            if (little)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
            }

            return bytes;
        }

        private byte[] U32Bytes(uint value)
        {
            byte[] bytes = new byte[4];
            if (little)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            }

            return bytes;
        }
    }
}
