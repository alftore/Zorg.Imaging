using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;

namespace Zorg.Imaging.OsCodecs.Heif;

/// <summary>
/// Reads what an AVIF or HEIC file says about its primary image from the boxes before the pixels (ISO/IEC 23008-12):
/// <c>ftyp</c>, then <c>meta</c> with its <c>pitm</c>, <c>iinf</c>, <c>iref</c> and <c>iprp</c>. Nothing is decoded.
/// </summary>
/// <remarks>
/// <para>
/// Hostile input: the reader works on one buffer it never reads past, every box size is checked against its parent,
/// nesting is at most <see cref="MaxDepth"/> deep, and the counts of items, properties, associations and references are
/// capped before they're used, so a file can't make it loop or allocate much. A damaged file throws
/// <see cref="ImageFormatException"/>; one whose <c>meta</c> box doesn't fit in the buffer throws
/// <see cref="ImageLimitException"/> naming <see cref="ImageLimits.MaxHeaderBytes"/>.
/// </para>
/// </remarks>
internal static class HeifReader
{
    /// <summary>The most items, properties or references read; real photos have tens.</summary>
    public const int MaxEntries = 4096;

    /// <summary>The most tiles a grid image may have (a 48 MP iPhone photo has 48 or 64).</summary>
    public const int MaxTiles = 1024;

    private const int MaxDepth = 8;

    private const string AlphaUrn = "urn:mpeg:mpegB:cicp:systems:auxiliary:alpha";
    private const string HevcAlphaUrn = "urn:mpeg:hevc:2015:auxid:1";

    /// <summary>Reads the header.</summary>
    /// <param name="head">The start of the file, up to <see cref="ImageLimits.MaxHeaderBytes"/>.</param>
    /// <param name="limits">The limits: the tiles' pixels are checked against <see cref="ImageLimits.MaxPixels"/>.</param>
    /// <returns>What the file says about its primary image.</returns>
    public static HeifHeader Read(ReadOnlySpan<byte> head, ImageLimits limits)
    {
        ReadOnlySpan<byte> meta = default;
        int metaStart = 0;
        bool sawFtyp = false;
        bool avifBrand = false;
        int at = 0;
        while (at < head.Length)
        {
            Box box = Box.At(head, at, head.Length, out bool cut);
            if (cut)
            {
                if (sawFtyp && box.Type == Fourcc("meta"))
                {
                    throw new ImageLimitException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"The HEIF file's meta box is longer than the {head.Length} bytes read, the limit (ImageLimits.MaxHeaderBytes)."));
                }

                throw Truncated("before its meta box");
            }

            ReadOnlySpan<byte> body = head.Slice(box.BodyStart, box.BodyLength);
            if (!sawFtyp)
            {
                if (box.Type != Fourcc("ftyp") || body.Length < 8)
                {
                    throw Corrupt("doesn't start with an ftyp box");
                }

                sawFtyp = true;
                avifBrand = HasBrand(body, "avif") || HasBrand(body, "avis");
            }
            else if (box.Type == Fourcc("meta"))
            {
                meta = body;
                metaStart = box.BodyStart;
                break;
            }

            at = box.End;
        }

        if (meta.IsEmpty)
        {
            throw sawFtyp ? Truncated("before its meta box") : Truncated("before its ftyp box");
        }

        return ReadMeta(meta, metaStart, avifBrand, limits);
    }

    private static HeifHeader ReadMeta(ReadOnlySpan<byte> meta, int metaStart, bool avifBrand, ImageLimits limits)
    {
        // meta is a FullBox: version and flags, then child boxes.
        ReadOnlySpan<byte> children = FullBoxBody(meta, "meta", out _);
        int childrenStart = metaStart + 4;
        var locations = new Dictionary<uint, Location>();
        var xmpItems = new HashSet<uint>();
        long idatStart = -1;
        int idatLength = 0;
        uint primary = 0;
        bool sawPrimary = false;
        var itemTypes = new Dictionary<uint, uint>();
        var references = new List<Reference>();
        ReadOnlySpan<byte> ipco = default;
        ReadOnlySpan<byte> ipma = default;
        bool pict = false;

        foreach (Box box in Children(children, depth: 1))
        {
            ReadOnlySpan<byte> body = children.Slice(box.BodyStart, box.BodyLength);
            switch (box.Type)
            {
                case var t when t == Fourcc("hdlr"):
                    ReadOnlySpan<byte> hdlr = FullBoxBody(body, "hdlr", out _);
                    pict = hdlr.Length >= 8 && Fourcc(hdlr.Slice(4, 4)) == Fourcc("pict");
                    break;
                case var t when t == Fourcc("pitm"):
                    ReadOnlySpan<byte> pitm = FullBoxBody(body, "pitm", out int pitmVersion);
                    primary = pitmVersion == 0 ? Need(pitm, 2, "pitm").ReadU16(0) : Need(pitm, 4, "pitm").ReadU32(0);
                    sawPrimary = true;
                    break;
                case var t when t == Fourcc("iinf"):
                    ReadItemInfo(body, itemTypes, xmpItems);
                    break;
                case var t when t == Fourcc("iloc"):
                    ReadLocations(body, locations);
                    break;
                case var t when t == Fourcc("idat"):
                    idatStart = childrenStart + box.BodyStart;
                    idatLength = box.BodyLength;
                    break;
                case var t when t == Fourcc("iref"):
                    ReadReferences(body, references);
                    break;
                case var t when t == Fourcc("iprp"):
                    foreach (Box child in Children(body, depth: 2))
                    {
                        if (child.Type == Fourcc("ipco"))
                        {
                            ipco = body.Slice(child.BodyStart, child.BodyLength);
                        }
                        else if (child.Type == Fourcc("ipma") && ipma.IsEmpty)
                        {
                            ipma = body.Slice(child.BodyStart, child.BodyLength);
                        }
                    }

                    break;
            }
        }

        if (!pict)
        {
            throw Corrupt("has no picture handler (hdlr 'pict')");
        }

        if (!sawPrimary || !itemTypes.TryGetValue(primary, out uint primaryType))
        {
            throw Corrupt("doesn't name a primary image it describes");
        }

        List<Property> properties = ReadProperties(ipco);
        Dictionary<uint, List<int>> associations = ReadAssociations(ipma);

        ImageFormat format = primaryType == Fourcc("av01") ? ImageFormat.Avif
            : primaryType == Fourcc("hvc1") ? ImageFormat.Heif
            : primaryType == Fourcc("grid") ? (TileType(primary, references, itemTypes) == Fourcc("av01") ? ImageFormat.Avif : ImageFormat.Heif)
            : throw new UnsupportedImageFormatException(
                string.Create(CultureInfo.InvariantCulture, $"HEIF images coded as '{FourccText(primaryType)}' aren't supported."),
                ImageFormat.Heif);
        if (format == ImageFormat.Heif && avifBrand && primaryType != Fourcc("hvc1"))
        {
            format = ImageFormat.Avif;
        }

        Property? size = Find(properties, associations, primary, "ispe")
            ?? throw Corrupt("doesn't give its primary image's size (ispe)");
        int width = size.Value.Width;
        int height = size.Value.Height;
        if (width <= 0 || height <= 0)
        {
            throw Corrupt("gives a size of zero for its primary image");
        }

        int tiles = 0;
        if (primaryType == Fourcc("grid"))
        {
            long tilePixels = 0;
            foreach (Reference reference in references)
            {
                if (reference.Type != Fourcc("dimg") || reference.From != primary)
                {
                    continue;
                }

                foreach (uint tile in reference.To)
                {
                    if (++tiles > MaxTiles)
                    {
                        throw new ImageLimitException(string.Create(CultureInfo.InvariantCulture, $"The HEIF image has more than {MaxTiles} tiles."));
                    }

                    if (Find(properties, associations, tile, "ispe") is { } tileSize)
                    {
                        tilePixels += (long)tileSize.Width * tileSize.Height;
                    }
                }
            }

            if (tiles == 0)
            {
                throw Corrupt("has a grid image with no tiles");
            }

            // Tiles may overhang the picture a little, but not by more than the picture itself.
            if (tilePixels > 2 * limits.MaxPixels)
            {
                throw new ImageLimitException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The HEIF image's tiles have {tilePixels} pixels, more than twice the limit (ImageLimits.MaxPixels)."));
            }
        }

        if ((long)width * height > limits.MaxPixels)
        {
            throw new ImageLimitException(string.Create(
                CultureInfo.InvariantCulture,
                $"The HEIF image is {width}x{height} pixels before cropping, more than the limit (ImageLimits.MaxPixels)."));
        }

        int rotation = Find(properties, associations, primary, "irot")?.Value ?? 0;
        int mirror = Find(properties, associations, primary, "imir")?.Value ?? -1;
        Crop crop = Find(properties, associations, primary, "clap") is { } clap ? CleanAperture(clap, width, height) : new Crop(0, 0, width, height);
        Property? pixi = Find(properties, associations, primary, "pixi");
        Property? colour = Find(properties, associations, primary, "colr");
        bool alpha = HasAlpha(primary, references, properties, associations);
        ItemData? exifItem = Describing(primary, references, id => itemTypes.GetValueOrDefault(id) == Fourcc("Exif"), locations, idatStart, idatLength);
        ItemData? xmpItem = Describing(primary, references, xmpItems.Contains, locations, idatStart, idatLength);
        bool exif = HasExif(primary, references, itemTypes);
        return new HeifHeader(format, width, height, crop, rotation, mirror, alpha, pixi?.Value ?? 8, colour?.Colour, tiles, exif)
        {
            ExifItem = exifItem,
            XmpItem = xmpItem,
        };
    }

    private static void ReadItemInfo(ReadOnlySpan<byte> iinf, Dictionary<uint, uint> itemTypes, HashSet<uint> xmpItems)
    {
        ReadOnlySpan<byte> body = FullBoxBody(iinf, "iinf", out int version);
        int countBytes = version == 0 ? 2 : 4;
        Need(body, countBytes, "iinf");
        uint count = version == 0 ? body.ReadU16(0) : body.ReadU32(0);
        if (count > MaxEntries)
        {
            throw Corrupt("lists too many items");
        }

        ReadOnlySpan<byte> entries = body[countBytes..];
        foreach (Box box in Children(entries, depth: 2))
        {
            if (box.Type != Fourcc("infe"))
            {
                continue;
            }

            ReadOnlySpan<byte> infe = FullBoxBody(entries.Slice(box.BodyStart, box.BodyLength), "infe", out int infeVersion);
            if (infeVersion < 2)
            {
                continue; // Versions 0 and 1 have no item type; HEIF requires 2 or 3.
            }

            int idBytes = infeVersion == 2 ? 2 : 4;
            Need(infe, idBytes + 2 + 4, "infe");
            uint id = idBytes == 2 ? infe.ReadU16(0) : infe.ReadU32(0);
            uint type = infe.ReadU32(idBytes + 2);
            if (itemTypes.Count >= MaxEntries)
            {
                throw Corrupt("lists too many items");
            }

            itemTypes.TryAdd(id, type);

            // A mime item names its content type after its name: XMP is application/rdf+xml.
            if (type == Fourcc("mime"))
            {
                ReadOnlySpan<byte> strings = infe[(idBytes + 2 + 4)..];
                int nameEnd = strings.IndexOf((byte)0);
                if (nameEnd >= 0)
                {
                    ReadOnlySpan<byte> contentType = strings[(nameEnd + 1)..];
                    int typeEnd = contentType.IndexOf((byte)0);
                    if ((typeEnd < 0 ? contentType : contentType[..typeEnd]).SequenceEqual("application/rdf+xml"u8))
                    {
                        xmpItems.Add(id);
                    }
                }
            }
        }
    }

    private static void ReadReferences(ReadOnlySpan<byte> iref, List<Reference> references)
    {
        ReadOnlySpan<byte> body = FullBoxBody(iref, "iref", out int version);
        int idBytes = version == 0 ? 2 : 4;
        int total = 0;
        foreach (Box box in Children(body, depth: 2))
        {
            ReadOnlySpan<byte> entry = body.Slice(box.BodyStart, box.BodyLength);
            Need(entry, idBytes + 2, "iref");
            uint from = idBytes == 2 ? entry.ReadU16(0) : entry.ReadU32(0);
            int count = entry.ReadU16(idBytes);
            Need(entry, idBytes + 2 + (count * idBytes), "iref");
            total += count;
            if (total > MaxEntries || references.Count >= MaxEntries)
            {
                throw Corrupt("has too many item references");
            }

            uint[] to = new uint[count];
            for (int i = 0; i < count; i++)
            {
                int offset = idBytes + 2 + (i * idBytes);
                to[i] = idBytes == 2 ? entry.ReadU16(offset) : entry.ReadU32(offset);
            }

            references.Add(new Reference(box.Type, from, to));
        }
    }

    private static List<Property> ReadProperties(ReadOnlySpan<byte> ipco)
    {
        var properties = new List<Property>();
        foreach (Box box in Children(ipco, depth: 3))
        {
            if (properties.Count >= MaxEntries)
            {
                throw Corrupt("has too many item properties");
            }

            ReadOnlySpan<byte> body = ipco.Slice(box.BodyStart, box.BodyLength);
            properties.Add(ReadProperty(box.Type, body));
        }

        return properties;
    }

    private static Property ReadProperty(uint type, ReadOnlySpan<byte> body)
    {
        if (type == Fourcc("ispe"))
        {
            ReadOnlySpan<byte> ispe = Need(FullBoxBody(body, "ispe", out _), 8, "ispe");
            uint w = ispe.ReadU32(0);
            uint h = ispe.ReadU32(4);
            return new Property(type) { Width = w > int.MaxValue ? -1 : (int)w, Height = h > int.MaxValue ? -1 : (int)h };
        }

        if (type == Fourcc("irot"))
        {
            return new Property(type) { Value = Need(body, 1, "irot")[0] & 3 };
        }

        if (type == Fourcc("imir"))
        {
            return new Property(type) { Value = Need(body, 1, "imir")[0] & 1 };
        }

        if (type == Fourcc("pixi"))
        {
            ReadOnlySpan<byte> pixi = FullBoxBody(body, "pixi", out _);
            int channels = Need(pixi, 1, "pixi")[0];
            Need(pixi, 1 + channels, "pixi");
            int bits = 0;
            for (int i = 0; i < channels; i++)
            {
                bits = Math.Max(bits, pixi[1 + i]);
            }

            return new Property(type) { Value = bits == 0 ? 8 : bits };
        }

        if (type == Fourcc("colr"))
        {
            Need(body, 4, "colr");
            uint kind = body.ReadU32(0);
            if (kind == Fourcc("nclx") && body.Length >= 4 + 7)
            {
                // 2 is "unspecified": HEIF readers take sRGB's (BT.709 primaries, sRGB transfer) for it.
                int primaries = body.ReadU16(4) is 2 ? 1 : body.ReadU16(4);
                int transfer = body.ReadU16(6) is 2 ? 13 : body.ReadU16(6);
                return new Property(type) { Colour = ColourOf(() => ColorSpace.FromCicp(primaries, transfer)) };
            }

            if (kind == Fourcc("prof") || kind == Fourcc("rICC"))
            {
                byte[] profile = body[4..].ToArray();
                return new Property(type) { Colour = ColourOf(() => ColorSpace.FromIccProfile(profile)) };
            }

            return new Property(type);
        }

        if (type == Fourcc("clap"))
        {
            ReadOnlySpan<byte> clap = Need(body, 32, "clap");
            int[] values = new int[8];
            for (int i = 0; i < 8; i++)
            {
                values[i] = (int)clap.ReadU32(i * 4);
            }

            return new Property(type) { Aperture = values };
        }

        if (type == Fourcc("auxC"))
        {
            ReadOnlySpan<byte> auxC = FullBoxBody(body, "auxC", out _);
            int end = auxC.IndexOf((byte)0);
            string urn = System.Text.Encoding.ASCII.GetString(end < 0 ? auxC : auxC[..end]);
            return new Property(type) { Alpha = urn is AlphaUrn or HevcAlphaUrn };
        }

        return new Property(type);
    }

    // A colour space the library can't use (an odd ICC profile, an unknown CICP code) means "say nothing", not a broken file.
    private static ColorSpace? ColourOf(Func<ColorSpace> make)
    {
        try
        {
            return make();
        }
        catch (Exception e) when (e is ArgumentException or ImageException or NotSupportedException)
        {
            return null;
        }
    }

    private static Dictionary<uint, List<int>> ReadAssociations(ReadOnlySpan<byte> ipma)
    {
        var associations = new Dictionary<uint, List<int>>();
        if (ipma.IsEmpty)
        {
            return associations;
        }

        ReadOnlySpan<byte> body = FullBoxBody(ipma, "ipma", out int version, out int flags);
        uint count = Need(body, 4, "ipma").ReadU32(0);
        if (count > MaxEntries)
        {
            throw Corrupt("has too many property associations");
        }

        int at = 4;
        int idBytes = version < 1 ? 2 : 4;
        int indexBytes = (flags & 1) != 0 ? 2 : 1;
        for (uint i = 0; i < count; i++)
        {
            Need(body, at + idBytes + 1, "ipma");
            uint item = idBytes == 2 ? body.ReadU16(at) : body.ReadU32(at);
            int n = body[at + idBytes];
            at += idBytes + 1;
            Need(body, at + (n * indexBytes), "ipma");
            if (!associations.TryGetValue(item, out List<int>? list))
            {
                list = [];
                associations[item] = list;
            }

            for (int j = 0; j < n; j++)
            {
                int index = indexBytes == 2 ? body.ReadU16(at) & 0x7FFF : body[at] & 0x7F;
                at += indexBytes;
                list.Add(index); // 1-based into ipco; 0 means none.
            }
        }

        return associations;
    }

    private static Property? Find(List<Property> properties, Dictionary<uint, List<int>> associations, uint item, string type)
    {
        uint wanted = Fourcc(type);
        if (!associations.TryGetValue(item, out List<int>? indexes))
        {
            return null;
        }

        foreach (int index in indexes)
        {
            if (index >= 1 && index <= properties.Count && properties[index - 1].Type == wanted)
            {
                return properties[index - 1];
            }
        }

        return null;
    }

    private static uint TileType(uint grid, List<Reference> references, Dictionary<uint, uint> itemTypes)
    {
        foreach (Reference reference in references)
        {
            if (reference.Type == Fourcc("dimg") && reference.From == grid && reference.To.Length > 0)
            {
                return itemTypes.GetValueOrDefault(reference.To[0]);
            }
        }

        return 0;
    }

    private static bool HasAlpha(uint primary, List<Reference> references, List<Property> properties, Dictionary<uint, List<int>> associations)
    {
        foreach (Reference reference in references)
        {
            if (reference.Type != Fourcc("auxl") || Array.IndexOf(reference.To, primary) < 0)
            {
                continue;
            }

            if (Find(properties, associations, reference.From, "auxC") is { Alpha: true })
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasExif(uint primary, List<Reference> references, Dictionary<uint, uint> itemTypes)
    {
        foreach (Reference reference in references)
        {
            if (reference.Type == Fourcc("cdsc") && Array.IndexOf(reference.To, primary) >= 0
                && itemTypes.GetValueOrDefault(reference.From) == Fourcc("Exif"))
            {
                return true;
            }
        }

        return false;
    }

    // iloc (ISO/IEC 14496-12 8.11.3): where each item's bytes are, as extents in the file (construction method 0) or
    // in the idat box (1). Method 2 (from another item) isn't used for metadata and is skipped.
    private static void ReadLocations(ReadOnlySpan<byte> iloc, Dictionary<uint, Location> locations)
    {
        ReadOnlySpan<byte> body = FullBoxBody(iloc, "iloc", out int version);
        if (version > 2)
        {
            return;
        }

        Need(body, 2, "iloc");
        int offsetSize = body[0] >> 4, lengthSize = body[0] & 15, baseSize = body[1] >> 4;
        int indexSize = version is 1 or 2 ? body[1] & 15 : 0;
        if (!ValidSize(offsetSize) || !ValidSize(lengthSize) || !ValidSize(baseSize) || !ValidSize(indexSize))
        {
            throw Corrupt("has an iloc box with an invalid field size");
        }

        int at = 2;
        uint count;
        if (version < 2)
        {
            count = Need(body, at + 2, "iloc").ReadU16(at);
            at += 2;
        }
        else
        {
            count = Need(body, at + 4, "iloc").ReadU32(at);
            at += 4;
        }

        if (count > MaxEntries)
        {
            throw Corrupt("locates too many items");
        }

        for (uint i = 0; i < count; i++)
        {
            int idBytes = version < 2 ? 2 : 4;
            Need(body, at + idBytes, "iloc");
            uint id = idBytes == 2 ? body.ReadU16(at) : body.ReadU32(at);
            at += idBytes;
            int method = 0;
            if (version is 1 or 2)
            {
                method = Need(body, at + 2, "iloc").ReadU16(at) & 15;
                at += 2;
            }

            Need(body, at + 2 + baseSize + 2, "iloc");
            at += 2; // data_reference_index
            ulong baseOffset = ReadSized(body, at, baseSize);
            at += baseSize;
            int extents = body.ReadU16(at);
            at += 2;
            int extentBytes = (indexSize + offsetSize + lengthSize) * extents;
            Need(body, at + extentBytes, "iloc");
            var offsets = new ulong[extents];
            var lengths = new ulong[extents];
            for (int e = 0; e < extents; e++)
            {
                at += indexSize;
                offsets[e] = ReadSized(body, at, offsetSize);
                at += offsetSize;
                lengths[e] = ReadSized(body, at, lengthSize);
                at += lengthSize;
            }

            locations.TryAdd(id, new Location(method, baseOffset, offsets, lengths));
        }
    }

    private static bool ValidSize(int size) => size is 0 or 4 or 8;

    private static ulong ReadSized(ReadOnlySpan<byte> body, int at, int size) => size switch
    {
        4 => body.ReadU32(at),
        8 => BinaryPrimitives.ReadUInt64BigEndian(body[at..]),
        _ => 0,
    };

    // The first item that a cdsc reference says describes the primary image, matching the test, with its bytes'
    // places in the file. Null when there's none or its location makes no sense.
    private static ItemData? Describing(
        uint primary,
        List<Reference> references,
        Func<uint, bool> isWanted,
        Dictionary<uint, Location> locations,
        long idatStart,
        int idatLength)
    {
        foreach (Reference reference in references)
        {
            if (reference.Type != Fourcc("cdsc") || Array.IndexOf(reference.To, primary) < 0 || !isWanted(reference.From)
                || !locations.TryGetValue(reference.From, out Location location))
            {
                continue;
            }

            if (location.Method is not (0 or 1) || (location.Method == 1 && idatStart < 0) || location.Offsets.Length == 0)
            {
                return null;
            }

            var offsets = new long[location.Offsets.Length];
            var lengths = new long[location.Offsets.Length];
            for (int e = 0; e < offsets.Length; e++)
            {
                ulong offset = location.BaseOffset + location.Offsets[e];
                ulong length = location.Lengths[e];
                if (offset < location.BaseOffset || length > int.MaxValue || offset > int.MaxValue)
                {
                    return null;
                }

                if (location.Method == 1)
                {
                    if (offset + length > (ulong)idatLength)
                    {
                        return null;
                    }

                    offset += (ulong)idatStart;
                }

                offsets[e] = (long)offset;
                lengths[e] = (long)length;
            }

            return new ItemData(offsets, lengths);
        }

        return null;
    }

    /// <summary>An item's bytes from the whole file, or null when they don't fit in it or are longer than the limit.</summary>
    /// <param name="file">The whole file.</param>
    /// <param name="item">Where the item is.</param>
    /// <param name="max">The most bytes to accept.</param>
    /// <returns>The bytes, or null.</returns>
    public static byte[]? ReadItem(ReadOnlySpan<byte> file, ItemData item, int max)
    {
        long total = 0;
        for (int e = 0; e < item.Offsets.Length; e++)
        {
            // A length of 0 means "to the end of the file".
            long length = item.Lengths[e] == 0 ? file.Length - item.Offsets[e] : item.Lengths[e];
            if (item.Offsets[e] < 0 || length < 0 || item.Offsets[e] + length > file.Length)
            {
                return null;
            }

            total += length;
            if (total > max)
            {
                return null;
            }
        }

        byte[] bytes = new byte[total];
        int at = 0;
        for (int e = 0; e < item.Offsets.Length; e++)
        {
            int length = (int)(item.Lengths[e] == 0 ? file.Length - item.Offsets[e] : item.Lengths[e]);
            file.Slice((int)item.Offsets[e], length).CopyTo(bytes.AsSpan(at));
            at += length;
        }

        return bytes;
    }

    /// <summary>
    /// The TIFF structure in an Exif item: after a 4-byte offset that says how far past it the TIFF header starts
    /// (6 when the item keeps JPEG's <c>Exif\0\0</c>). Null when it isn't one.
    /// </summary>
    /// <param name="item">The Exif item's bytes.</param>
    /// <returns>The TIFF bytes, byte order mark first, or null.</returns>
    public static byte[]? ExifTiff(byte[] item)
    {
        if (item.Length < 4)
        {
            return null;
        }

        uint skip = BinaryPrimitives.ReadUInt32BigEndian(item);
        if (skip > (uint)(item.Length - 4 - 8))
        {
            return null;
        }

        ReadOnlySpan<byte> tiff = item.AsSpan(4 + (int)skip);
        bool tiffHeader = tiff.StartsWith("II*\0"u8) || tiff.StartsWith("MM\0*"u8);
        return tiffHeader ? tiff.ToArray() : null;
    }

    /// <summary>
    /// The rectangle a <c>clap</c> box keeps (ISO/IEC 14496-12): a size centred on the image, moved by the offsets.
    /// Every value must come out a whole number of pixels inside the image, as MIAF requires.
    /// </summary>
    private static Crop CleanAperture(Property clap, int width, int height)
    {
        int[] v = clap.Aperture!;
        uint widthN = (uint)v[0], widthD = (uint)v[1], heightN = (uint)v[2], heightD = (uint)v[3];
        int horizontalN = v[4], verticalN = v[6];
        uint horizontalD = (uint)v[5], verticalD = (uint)v[7];
        if (widthD == 0 || heightD == 0 || horizontalD == 0 || verticalD == 0
            || widthN % widthD != 0 || heightN % heightD != 0)
        {
            throw Corrupt("has a clean aperture (clap) that isn't a whole number of pixels");
        }

        long cropWidth = widthN / widthD;
        long cropHeight = heightN / heightD;

        // left = (width - cropWidth) / 2 + horizontal offset, kept exact as halves.
        long leftTwice = (width - cropWidth) + (2L * horizontalN / horizontalD);
        long topTwice = (height - cropHeight) + (2L * verticalN / verticalD);
        if ((2L * horizontalN) % horizontalD != 0 || (2L * verticalN) % verticalD != 0 || leftTwice % 2 != 0 || topTwice % 2 != 0)
        {
            throw Corrupt("has a clean aperture (clap) that isn't a whole number of pixels");
        }

        long left = leftTwice / 2;
        long top = topTwice / 2;
        if (cropWidth < 1 || cropHeight < 1 || left < 0 || top < 0 || left + cropWidth > width || top + cropHeight > height)
        {
            throw Corrupt("has a clean aperture (clap) outside the image");
        }

        return new Crop((int)left, (int)top, (int)cropWidth, (int)cropHeight);
    }

    /// <summary>
    /// The EXIF orientation for HEIF's transforms: <c>irot</c> turns anticlockwise by 90° steps, then <c>imir</c>
    /// mirrors about a vertical (0) or horizontal (1) axis. -1 means no mirror.
    /// </summary>
    internal static int Orientation(int anticlockwise, int mirror) => (anticlockwise & 3, mirror) switch
    {
        (0, -1) => 1,
        (1, -1) => 8,
        (2, -1) => 3,
        (3, -1) => 6,
        (0, 0) => 2,
        (0, 1) => 4,
        (1, 0) => 7,
        (1, 1) => 5,
        (2, 0) => 4,
        (2, 1) => 2,
        (3, 0) => 5,
        (3, 1) => 7,
        _ => 1,
    };

    private static bool HasBrand(ReadOnlySpan<byte> ftyp, string brand)
    {
        uint wanted = Fourcc(brand);
        if (Fourcc(ftyp[..4]) == wanted)
        {
            return true;
        }

        for (int at = 8; at + 4 <= ftyp.Length; at += 4)
        {
            if (Fourcc(ftyp.Slice(at, 4)) == wanted)
            {
                return true;
            }
        }

        return false;
    }

    private static ReadOnlySpan<byte> FullBoxBody(ReadOnlySpan<byte> body, string name, out int version) =>
        FullBoxBody(body, name, out version, out _);

    private static ReadOnlySpan<byte> FullBoxBody(ReadOnlySpan<byte> body, string name, out int version, out int flags)
    {
        Need(body, 4, name);
        version = body[0];
        flags = (body[1] << 16) | (body[2] << 8) | body[3];
        return body[4..];
    }

    private static ReadOnlySpan<byte> Need(ReadOnlySpan<byte> body, int length, string name) =>
        body.Length >= length ? body : throw Corrupt(string.Create(CultureInfo.InvariantCulture, $"has a {name} box that is too short"));

    private static List<Box> Children(ReadOnlySpan<byte> parent, int depth)
    {
        if (depth > MaxDepth)
        {
            throw Corrupt("nests its boxes too deeply");
        }

        var boxes = new List<Box>();
        int at = 0;
        while (at < parent.Length)
        {
            if (boxes.Count >= MaxEntries)
            {
                throw Corrupt("has too many boxes");
            }

            Box box = Box.At(parent, at, parent.Length, out bool cut);
            if (cut)
            {
                throw Corrupt("has a box that runs past its parent");
            }

            boxes.Add(box);
            at = box.End;
        }

        return boxes;
    }

    internal static uint Fourcc(string text) =>
        ((uint)text[0] << 24) | ((uint)text[1] << 16) | ((uint)text[2] << 8) | text[3];

    private static uint Fourcc(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static string FourccText(uint type) => string.Create(4, type, static (span, t) =>
    {
        for (int i = 0; i < 4; i++)
        {
            char c = (char)((t >> (24 - (8 * i))) & 0xFF);
            span[i] = c is >= ' ' and <= '~' ? c : '?';
        }
    });

    private static ImageFormatException Corrupt(string what) => new("The HEIF file " + what + ".");

    private static ImageFormatException Truncated(string where) => new("The HEIF file ends " + where + ".");

    private static ushort ReadU16(this ReadOnlySpan<byte> span, int at) => BinaryPrimitives.ReadUInt16BigEndian(span[at..]);

    private static uint ReadU32(this ReadOnlySpan<byte> span, int at) => BinaryPrimitives.ReadUInt32BigEndian(span[at..]);

    /// <summary>A box's type and where its body is, within the buffer it was read from.</summary>
    private readonly record struct Box(uint Type, int BodyStart, int BodyLength)
    {
        public int End => BodyStart + BodyLength;

        // The box at an offset. cut is true when the header or body runs past limit.
        public static Box At(ReadOnlySpan<byte> data, int at, int limit, out bool cut)
        {
            cut = false;
            if (limit - at < 8)
            {
                cut = true;
                return new Box(limit - at >= 8 ? Fourcc(data.Slice(at + 4, 4)) : 0, at, 0);
            }

            ulong size = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
            uint type = Fourcc(data.Slice(at + 4, 4));
            int header = 8;
            if (size == 1)
            {
                if (limit - at < 16)
                {
                    cut = true;
                    return new Box(type, at, 0);
                }

                size = BinaryPrimitives.ReadUInt64BigEndian(data[(at + 8)..]);
                header = 16;
            }
            else if (size == 0)
            {
                size = (ulong)(limit - at);
            }

            if (size < (ulong)header)
            {
                throw new ImageFormatException("The HEIF file has a box shorter than its own header.");
            }

            if (size > (ulong)(limit - at))
            {
                cut = true;
                return new Box(type, at + header, 0);
            }

            return new Box(type, at + header, (int)size - header);
        }
    }

    private sealed record Reference(uint Type, uint From, uint[] To);

    private readonly record struct Location(int Method, ulong BaseOffset, ulong[] Offsets, ulong[] Lengths);

    private readonly record struct Property(uint Type)
    {
        public int Width { get; init; }

        public int Height { get; init; }

        public int Value { get; init; }

        public bool Alpha { get; init; }

        public ColorSpace? Colour { get; init; }

        // clap: width N/D, height N/D, horizontal offset N/D, vertical offset N/D (offsets signed).
        public int[]? Aperture { get; init; }
    }
}

/// <summary>What an AVIF or HEIC file says about its primary image.</summary>
/// <param name="Format">AVIF or HEIF (HEIC).</param>
/// <param name="Width">The stored width, as coded.</param>
/// <param name="Height">The stored height, as coded.</param>
/// <param name="Crop">The part the <c>clap</c> box keeps, or the whole image.</param>
/// <param name="Rotation">The <c>irot</c> turn, anticlockwise in 90° steps, 0 to 3.</param>
/// <param name="Mirror">The <c>imir</c> axis: 0 mirrors left to right, 1 top to bottom, -1 none.</param>
/// <param name="HasAlpha">Whether an alpha plane goes with it.</param>
/// <param name="BitDepth">The deepest channel, 8 to 16.</param>
/// <param name="ColorSpace">The colour space from <c>colr</c>, or <see langword="null"/>.</param>
/// <param name="Tiles">How many tiles a grid image has, or 0 for a single image.</param>
/// <param name="HasExif">Whether an EXIF item describes it.</param>
internal sealed record HeifHeader(ImageFormat Format, int Width, int Height, Crop Crop, int Rotation, int Mirror, bool HasAlpha, int BitDepth, ColorSpace? ColorSpace, int Tiles, bool HasExif)
{
    /// <summary>The EXIF orientation equal to <see cref="Rotation"/> then <see cref="Mirror"/>.</summary>
    public int Orientation => HeifReader.Orientation(Rotation, Mirror);

    /// <summary>Whether the image is shown cropped, turned or mirrored.</summary>
    public bool IsTransformed => Rotation != 0 || Mirror >= 0 || Crop.Width != Width || Crop.Height != Height;

    /// <summary>The width as shown: cropped, then turned.</summary>
    public int DisplayWidth => Rotation % 2 == 1 ? Crop.Height : Crop.Width;

    /// <summary>The height as shown: cropped, then turned.</summary>
    public int DisplayHeight => Rotation % 2 == 1 ? Crop.Width : Crop.Height;

    /// <summary>Where the Exif item describing the image is, or null.</summary>
    public ItemData? ExifItem { get; init; }

    /// <summary>Where the XMP item describing the image is, or null.</summary>
    public ItemData? XmpItem { get; init; }
}

/// <summary>An item's bytes as extents of the file: offsets and lengths (0 meaning to the end).</summary>
internal sealed record ItemData(long[] Offsets, long[] Lengths);

/// <summary>A rectangle of the stored image.</summary>
internal readonly record struct Crop(int X, int Y, int Width, int Height);
