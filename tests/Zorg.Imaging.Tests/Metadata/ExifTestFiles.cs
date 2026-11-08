using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Zorg.Imaging.Tests.Metadata;

/// <summary>Builds and reads EXIF blocks (TIFF structures) for the metadata tests.</summary>
internal static class ExifTestFiles
{
    /// <summary>A tag: number, TIFF type and value bytes in machine order of the block (built per byte order).</summary>
    public sealed record Tag(ushort Number, ushort Type, uint Count, byte[] Value);

    /// <summary>A camera photo's EXIF: make, model, orientation, owner, dates, GPS, a maker note and a thumbnail.</summary>
    public static byte[] Camera(bool little, int orientation = 6)
    {
        var b = new Builder(little);
        List<Tag> main =
        [
            b.Ascii(0x010F, "Zorgcam"),
            b.Ascii(0x0110, "Z-1 Mark II"),
            b.Short(0x0112, (ushort)orientation),
            b.Ascii(0x0131, "Firmware 1.2"),
            b.Ascii(0x0132, "2026:11:08 10:00:00"),
            b.Ascii(0x013B, "Alex Example"),
            b.Ascii(0x8298, "(c) 2026 Alex Example"),
        ];
        List<Tag> exif =
        [
            b.Rational(0x829A, 1, 250),
            b.Ascii(0x9003, "2026:11:08 10:00:00"),
            b.Ascii(0x9011, "+01:00"),
            new(0x927C, 7, 16, Enumerable.Repeat((byte)0xAB, 16).ToArray()),
            b.Long(0xA002, 4000),
            b.Long(0xA003, 3000),
            b.Ascii(0xA431, "SN123456"),
            b.Ascii(0xA434, "Zorg 24-70mm"),
        ];
        List<Tag> gps =
        [
            new(0x0000, 1, 4, [2, 3, 0, 0]),
            b.Ascii(0x0001, "N"),
            b.Rationals(0x0002, (59, 1), (54, 1), (3000, 100)),
            b.Ascii(0x001D, "2026:11:08"),
        ];
        return b.Build(main, exif, gps, thumbnail: true);
    }

    /// <summary>The tags of the main, Exif and GPS IFDs, by number, and whether an IFD1 follows; null for a bad block.</summary>
    public static (Dictionary<ushort, Tag> Main, Dictionary<ushort, Tag> Exif, Dictionary<ushort, Tag> Gps, bool HasIfd1) Read(byte[] tiff)
    {
        bool little = tiff[0] == 'I';
        ushort U16(int at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(tiff.AsSpan(at));
        uint U32(int at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(tiff.AsSpan(at));
        (Dictionary<ushort, Tag> Tags, uint Next) Ifd(uint offset)
        {
            var tags = new Dictionary<ushort, Tag>();
            int count = U16((int)offset);
            for (int i = 0; i < count; i++)
            {
                int at = (int)offset + 2 + (i * 12);
                ushort type = U16(at + 2);
                uint items = U32(at + 4);
                int size = type switch { 3 or 8 => 2, 4 or 9 => 4, 5 or 10 => 8, _ => 1 } * (int)items;
                int valueAt = size <= 4 ? at + 8 : (int)U32(at + 8);
                tags[U16(at)] = new Tag(U16(at), type, items, tiff.AsSpan(valueAt, size).ToArray());
            }

            return (tags, U32((int)offset + 2 + (count * 12)));
        }

        (Dictionary<ushort, Tag> main, uint next) = Ifd(U32(4));
        Dictionary<ushort, Tag> exif = main.TryGetValue(0x8769, out Tag? e) ? Ifd(Value(e, little)).Tags : [];
        Dictionary<ushort, Tag> gps = main.TryGetValue(0x8825, out Tag? g) ? Ifd(Value(g, little)).Tags : [];
        return (main, exif, gps, next != 0);
    }

    /// <summary>A SHORT or LONG tag's value.</summary>
    public static uint Value(Tag tag, bool little) => tag.Type == 3
        ? (little ? BinaryPrimitives.ReadUInt16LittleEndian(tag.Value) : BinaryPrimitives.ReadUInt16BigEndian(tag.Value))
        : (little ? BinaryPrimitives.ReadUInt32LittleEndian(tag.Value) : BinaryPrimitives.ReadUInt32BigEndian(tag.Value));

    public static string Text(Tag tag) => Encoding.ASCII.GetString(tag.Value).TrimEnd('\0');

    /// <summary>A JPEG with an APP1 segment (EXIF or XMP) put right after SOI.</summary>
    public static byte[] WithApp1(byte[] jpeg, byte[] payload)
    {
        int length = payload.Length + 2;
        return [.. jpeg[..2], 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. payload, .. jpeg[2..]];
    }

    public sealed class Builder(bool little)
    {
        public Tag Ascii(ushort number, string text)
        {
            byte[] bytes = [.. Encoding.ASCII.GetBytes(text), 0];
            return new Tag(number, 2, (uint)bytes.Length, bytes);
        }

        public Tag Short(ushort number, ushort value) => new(number, 3, 1, U16(value));

        public Tag Long(ushort number, uint value) => new(number, 4, 1, U32(value));

        public Tag Rational(ushort number, uint numerator, uint denominator) => new(number, 5, 1, [.. U32(numerator), .. U32(denominator)]);

        public Tag Rationals(ushort number, params (uint N, uint D)[] values) =>
            new(number, 5, (uint)values.Length, values.SelectMany(v => U32(v.N).Concat(U32(v.D))).ToArray());

        public byte[] Build(List<Tag> main, List<Tag> exif, List<Tag> gps, bool thumbnail)
        {
            var output = new List<byte>();
            output.AddRange(little ? "II"u8.ToArray() : "MM"u8.ToArray());
            output.AddRange(U16(42));
            output.AddRange(U32(8));
            main = [.. main, Long(0x8769, 0), Long(0x8825, 0)];
            int mainPointerAt = WriteIfd(output, main, out int exifSlot, out int gpsSlot, out int nextSlot);
            _ = mainPointerAt;
            Patch(output, exifSlot, output.Count);
            WriteIfd(output, exif, out _, out _, out _);
            Patch(output, gpsSlot, output.Count);
            WriteIfd(output, gps, out _, out _, out _);
            if (thumbnail)
            {
                Patch(output, nextSlot, output.Count);
                WriteIfd(output, [Long(0x0201, 0), Long(0x0202, 4)], out _, out _, out _);
            }

            return [.. output];
        }

        private int WriteIfd(List<byte> output, List<Tag> tags, out int exifSlot, out int gpsSlot, out int nextSlot)
        {
            tags = [.. tags.OrderBy(t => t.Number)];
            int start = output.Count;
            int values = start + 2 + (tags.Count * 12) + 4;
            var data = new List<byte>();
            exifSlot = gpsSlot = -1;
            output.AddRange(U16((ushort)tags.Count));
            foreach (Tag tag in tags)
            {
                output.AddRange(U16(tag.Number));
                output.AddRange(U16(tag.Type));
                output.AddRange(U32(tag.Count));
                if (tag.Number == 0x8769)
                {
                    exifSlot = output.Count;
                }

                if (tag.Number == 0x8825)
                {
                    gpsSlot = output.Count;
                }

                if (tag.Value.Length <= 4)
                {
                    output.AddRange(tag.Value);
                    output.AddRange(new byte[4 - tag.Value.Length]);
                }
                else
                {
                    output.AddRange(U32((uint)(values + data.Count)));
                    data.AddRange(tag.Value);
                    if (data.Count % 2 == 1)
                    {
                        data.Add(0);
                    }
                }
            }

            nextSlot = output.Count;
            output.AddRange(U32(0));
            output.AddRange(data);
            return start;
        }

        private void Patch(List<byte> output, int at, int value)
        {
            byte[] bytes = U32((uint)value);
            for (int i = 0; i < 4; i++)
            {
                output[at + i] = bytes[i];
            }
        }

        private byte[] U16(ushort value)
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

        private byte[] U32(uint value)
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
