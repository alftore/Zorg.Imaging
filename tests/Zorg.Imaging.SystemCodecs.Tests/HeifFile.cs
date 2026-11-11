using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Zorg.Imaging.OsCodecs.Tests;

/// <summary>Builds HEIF files box by box for tests: only the structure, with no coded pixels.</summary>
public sealed class HeifFile
{
    private readonly List<(uint Id, string Type)> _items = [];
    private readonly List<byte[]> _properties = [];
    private readonly Dictionary<uint, List<int>> _associations = [];
    private readonly List<(string Type, uint From, uint[] To)> _references = [];

    public string MajorBrand { get; init; } = "heic";

    public IReadOnlyList<string> Brands { get; init; } = ["mif1", "heic"];

    public uint Primary { get; set; } = 1;

    public bool WithHandler { get; init; } = true;

    /// <summary>An HEVC image of the given size, item 1, as the primary image.</summary>
    public static HeifFile Heic(int width, int height) => new HeifFile().Item(1, "hvc1").Property(1, Ispe(width, height));

    /// <summary>An AV1 image of the given size, item 1, as the primary image.</summary>
    public static HeifFile Avif(int width, int height) =>
        new HeifFile { MajorBrand = "avif", Brands = ["mif1", "avif"] }.Item(1, "av01").Property(1, Ispe(width, height));

    /// <summary>A grid of tiles, each of tileWidth × tileHeight, of the given type.</summary>
    public static HeifFile Grid(int width, int height, int tiles, int tileWidth, int tileHeight, string tileType = "hvc1")
    {
        var file = new HeifFile().Item(1, "grid").Property(1, Ispe(width, height));
        int ispe = file._properties.Count; // shared by every tile
        file._properties.Add(Ispe(tileWidth, tileHeight));
        uint[] ids = new uint[tiles];
        for (int i = 0; i < tiles; i++)
        {
            ids[i] = (uint)(2 + i);
            file.Item(ids[i], tileType);
            file.Associate(ids[i], ispe + 1);
        }

        file._references.Add(("dimg", 1, ids));
        return file;
    }

    public HeifFile Item(uint id, string type)
    {
        _items.Add((id, type));
        return this;
    }

    public HeifFile Property(uint item, byte[] property)
    {
        _properties.Add(property);
        return Associate(item, _properties.Count);
    }

    public HeifFile Associate(uint item, int index)
    {
        if (!_associations.TryGetValue(item, out List<int>? list))
        {
            list = [];
            _associations[item] = list;
        }

        list.Add(index);
        return this;
    }

    public HeifFile Reference(string type, uint from, params uint[] to)
    {
        _references.Add((type, from, to));
        return this;
    }

    /// <summary>Adds an alpha plane, item 99, for the primary image.</summary>
    public HeifFile WithAlpha()
    {
        Item(99, "hvc1");
        Property(99, FullBox("auxC", 0, 0, Encoding.ASCII.GetBytes("urn:mpeg:mpegB:cicp:systems:auxiliary:alpha\0")));
        return Reference("auxl", 99, Primary);
    }

    /// <summary>Adds an EXIF item, 98, describing the primary image.</summary>
    public HeifFile WithExif() => Item(98, "Exif").Reference("cdsc", 98, Primary);

    public static byte[] Ispe(int width, int height) => FullBox("ispe", 0, 0, U32((uint)width), U32((uint)height));

    public static byte[] Irot(int anticlockwise) => Box("irot", [(byte)anticlockwise]);

    public static byte[] Imir(int axis) => Box("imir", [(byte)axis]);

    public static byte[] Pixi(params byte[] bits) => FullBox("pixi", 0, 0, [(byte)bits.Length, .. bits]);

    public static byte[] Nclx(int primaries, int transfer, int matrix) =>
        Box("colr", Encoding.ASCII.GetBytes("nclx"), U16((ushort)primaries), U16((ushort)transfer), U16((ushort)matrix), [0x80]);

    public byte[] Build()
    {
        byte[] ftyp = Box("ftyp", Encoding.ASCII.GetBytes(MajorBrand), U32(0), [.. Brands.SelectMany(b => Encoding.ASCII.GetBytes(b))]);
        var meta = new List<byte[]>();
        if (WithHandler)
        {
            meta.Add(FullBox("hdlr", 0, 0, U32(0), Encoding.ASCII.GetBytes("pict"), new byte[12], [0]));
        }

        meta.Add(FullBox("pitm", 0, 0, U16((ushort)Primary)));
        meta.Add(FullBox("iinf", 0, 0, U16((ushort)_items.Count), [.. _items.SelectMany(i => FullBox("infe", 2, 0, U16((ushort)i.Id), U16(0), Encoding.ASCII.GetBytes(i.Type), [0]))]));
        if (_references.Count > 0)
        {
            meta.Add(FullBox("iref", 0, 0, [.. _references.SelectMany(r => Box(r.Type, U16((ushort)r.From), U16((ushort)r.To.Length), [.. r.To.SelectMany(t => U16((ushort)t))]))]));
        }

        byte[] ipco = Box("ipco", [.. _properties.SelectMany(p => p)]);
        var ipma = new List<byte> { };
        ipma.AddRange(U32((uint)_associations.Count));
        foreach ((uint item, List<int> indexes) in _associations)
        {
            ipma.AddRange(U16((ushort)item));
            ipma.Add((byte)indexes.Count);
            ipma.AddRange(indexes.Select(i => (byte)i));
        }

        meta.Add(Box("iprp", ipco, FullBox("ipma", 0, 0, [.. ipma])));
        return [.. ftyp, .. FullBox("meta", 0, 0, [.. meta.SelectMany(m => m)]), .. Box("mdat", new byte[16])];
    }

    public static byte[] Box(string type, params byte[][] parts)
    {
        int length = 8 + parts.Sum(p => p.Length);
        using var stream = new MemoryStream();
        stream.Write(U32((uint)length));
        stream.Write(Encoding.ASCII.GetBytes(type));
        foreach (byte[] part in parts)
        {
            stream.Write(part);
        }

        return stream.ToArray();
    }

    public static byte[] FullBox(string type, int version, int flags, params byte[][] parts) =>
        Box(type, [[(byte)version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags], .. parts]);

    public static byte[] U16(ushort value)
    {
        byte[] bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }

    public static byte[] U32(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }
}
