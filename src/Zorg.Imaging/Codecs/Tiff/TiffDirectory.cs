using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Codecs.Tiff;

/// <summary>
/// A TIFF's header and first image file directory (TIFF 6.0, part 1): the tags a baseline reader needs, with their
/// values read from wherever the file keeps them.
/// </summary>
internal sealed class TiffDirectory
{
    public const int None = 1;
    public const int Lzw = 5;
    public const int Deflate = 8;
    public const int AdobeDeflate = 32946;
    public const int PackBits = 32773;

    public const int WhiteIsZero = 0;
    public const int BlackIsZero = 1;
    public const int Rgb = 2;
    public const int Palette = 3;
    public const int Separated = 5;

    // A directory with more entries than this isn't an image directory anyone wrote.
    private const int MaxEntries = 4096;

    public bool BigEndian { get; private init; }

    public int Width { get; private init; }

    public int Height { get; private init; }

    public int[] BitsPerSample { get; private init; } = [1];

    public int SamplesPerPixel { get; private init; } = 1;

    public int Compression { get; private init; } = None;

    public int Photometric { get; private init; } = -1;

    public int Planar { get; private init; } = 1;

    public int Predictor { get; private init; } = 1;

    public int Orientation { get; private init; } = 1;

    public int SampleFormat { get; private init; } = 1;

    public int RowsPerStrip { get; private init; }

    public int TileWidth { get; private init; }

    public int TileHeight { get; private init; }

    public long[] Offsets { get; private init; } = [];

    public long[] ByteCounts { get; private init; } = [];

    public ushort[] ColorMap { get; private init; } = [];

    /// <summary>The ExtraSamples values: 1 for associated (premultiplied) alpha, 2 for unassociated, 0 for anything else.</summary>
    public int[] ExtraSamples { get; private init; } = [];

    public byte[]? IccProfile { get; private init; }

    public int InkSet { get; private init; } = 1;

    public bool Tiled => TileWidth > 0;

    public int Bits => BitsPerSample[0];

    /// <summary>Parses the header and the first directory of a whole file in memory.</summary>
    public static TiffDirectory Read(ReadOnlySpan<byte> file, ImageLimits limits)
    {
        if (file.Length < 8)
        {
            throw ImageErrors.Truncated("The TIFF file ends inside its header.");
        }

        bool big = file[0] == (byte)'M';
        int magic = U16(file[2..], big);
        if (magic == 43)
        {
            throw new UnsupportedImageFormatException("BigTIFF files aren't supported.", ImageFormat.Tiff);
        }

        if (magic != 42 || (file[0] != file[1]) || (file[0] is not ((byte)'I' or (byte)'M')))
        {
            throw ImageErrors.Corrupt("The TIFF file doesn't start with a TIFF header.");
        }

        long ifd = U32(file[4..], big);
        if (ifd < 8 || ifd + 2 > file.Length)
        {
            throw ImageErrors.Corrupt("The TIFF file's first directory isn't inside the file.");
        }

        int count = U16(file[(int)ifd..], big);
        if (count == 0 || count > MaxEntries || ifd + 2 + (12L * count) > file.Length)
        {
            throw ImageErrors.Corrupt("The TIFF file's first directory is damaged or truncated.");
        }

        var values = new Dictionary<int, long[]>();
        byte[]? icc = null;
        for (int e = 0; e < count; e++)
        {
            ReadOnlySpan<byte> entry = file.Slice((int)ifd + 2 + (12 * e), 12);
            int tag = U16(entry, big);
            int type = U16(entry[2..], big);
            long n = U32(entry[4..], big);
            int size = type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 13 => 4, 5 or 10 or 12 => 8, 16 or 17 or 18 => 8, _ => 0 };
            if (size == 0 || n == 0)
            {
                continue;
            }

            long bytes = n * size;
            long at = bytes <= 4 ? ifd + 2 + (12 * e) + 8 : U32(entry[8..], big);
            if (at < 0 || at + bytes > file.Length)
            {
                throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The TIFF file's tag {tag} points outside the file."));
            }

            if (tag == 34675)
            {
                if (bytes > ColorSpace.MaxIccProfileBytes)
                {
                    throw ImageErrors.ExceedsLimits(string.Create(CultureInfo.InvariantCulture, $"The TIFF's ICC profile is {bytes} bytes, more than {ColorSpace.MaxIccProfileBytes}."));
                }

                icc = file.Slice((int)at, (int)bytes).ToArray();
                continue;
            }

            // Only the tags a baseline reader uses are kept; their value counts are bounded by the image they describe.
            if (tag is not (256 or 257 or 258 or 259 or 262 or 273 or 274 or 277 or 278 or 279 or 284 or 317 or 320 or 322 or 323 or 324 or 325 or 332 or 338 or 339))
            {
                continue;
            }

            if (n > 1 << 24)
            {
                throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The TIFF file's tag {tag} has {n} values, more than any image needs."));
            }

            long[] read = new long[n];
            ReadOnlySpan<byte> data = file.Slice((int)at, (int)bytes);
            for (int i = 0; i < n; i++)
            {
                read[i] = size switch
                {
                    1 => data[i],
                    2 => U16(data[(2 * i)..], big),
                    4 => U32(data[(4 * i)..], big),
                    _ => (long)Math.Min(big ? BinaryPrimitives.ReadUInt64BigEndian(data[(8 * i)..]) : BinaryPrimitives.ReadUInt64LittleEndian(data[(8 * i)..]), long.MaxValue),
                };
            }

            values[tag] = read;
        }

        long One(int tag, long fallback) => values.TryGetValue(tag, out long[]? v) ? v[0] : fallback;
        long width = One(256, 0);
        long height = One(257, 0);
        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue)
        {
            throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The TIFF is {width} x {height}; both sides must be at least 1."));
        }

        if (width * height > limits.MaxPixels)
        {
            throw ImageErrors.ExceedsLimits(string.Create(
                CultureInfo.InvariantCulture,
                $"The TIFF image is {width} x {height}, {width * height} pixels, more than the {limits.MaxPixels}-pixel limit (ImageLimits.MaxPixels)."));
        }

        int samples = (int)Math.Clamp(One(277, 1), 1, 16);
        int[] bits = values.TryGetValue(258, out long[]? b) ? Array.ConvertAll(b, v => (int)Math.Min(v, 64)) : [1];
        int[] extra = values.TryGetValue(338, out long[]? x) ? Array.ConvertAll(x, v => (int)Math.Min(v, 3)) : [];
        bool tiled = values.ContainsKey(322);
        var directory = new TiffDirectory
        {
            BigEndian = big,
            Width = (int)width,
            Height = (int)height,
            BitsPerSample = bits,
            SamplesPerPixel = samples,
            Compression = (int)One(259, None),
            Photometric = (int)One(262, -1),
            Planar = (int)One(284, 1),
            Predictor = (int)One(317, 1),
            Orientation = (int)One(274, 1),
            SampleFormat = (int)One(339, 1),
            RowsPerStrip = (int)Math.Clamp(One(278, height), 1, height),
            TileWidth = tiled ? (int)Math.Clamp(One(322, 0), 0, int.MaxValue) : 0,
            TileHeight = tiled ? (int)Math.Clamp(One(323, 0), 0, int.MaxValue) : 0,
            Offsets = values.TryGetValue(tiled ? 324 : 273, out long[]? o) ? o : [],
            ByteCounts = values.TryGetValue(tiled ? 325 : 279, out long[]? c) ? c : [],
            ColorMap = values.TryGetValue(320, out long[]? m) ? Array.ConvertAll(m, v => (ushort)v) : [],
            ExtraSamples = extra,
            IccProfile = icc,
            InkSet = (int)One(332, 1),
        };
        return directory.Check();
    }

    // The combinations this reader decodes, and the chunk tables matching the image.
    private TiffDirectory Check()
    {
        if (Compression is not (None or Lzw or Deflate or AdobeDeflate or PackBits))
        {
            string name = Compression switch
            {
                2 or 3 or 4 => "CCITT fax",
                6 or 7 => "JPEG",
                34712 => "JPEG 2000",
                50001 => "WebP",
                _ => string.Create(CultureInfo.InvariantCulture, $"type {Compression}"),
            };
            throw new UnsupportedImageFormatException($"TIFF files with {name} compression aren't supported.", ImageFormat.Tiff);
        }

        if (SampleFormat != 1)
        {
            throw new UnsupportedImageFormatException("TIFF files of signed or floating-point samples aren't supported.", ImageFormat.Tiff);
        }

        int colour = Photometric switch
        {
            WhiteIsZero or BlackIsZero or Palette => 1,
            Rgb => 3,
            Separated when InkSet == 1 => 4,
            6 => throw new UnsupportedImageFormatException("TIFF files in YCbCr aren't supported.", ImageFormat.Tiff),
            8 or 9 or 10 => throw new UnsupportedImageFormatException("TIFF files in CIE Lab aren't supported.", ImageFormat.Tiff),
            -1 => throw ImageErrors.Corrupt("The TIFF file has no PhotometricInterpretation tag."),
            _ => throw new UnsupportedImageFormatException(
                string.Create(CultureInfo.InvariantCulture, $"TIFF files with photometric interpretation {Photometric} aren't supported."),
                ImageFormat.Tiff),
        };

        if (SamplesPerPixel < colour)
        {
            throw ImageErrors.Corrupt("The TIFF file has fewer samples per pixel than its colours need.");
        }

        foreach (int bits in BitsPerSample)
        {
            if (bits != Bits)
            {
                throw new UnsupportedImageFormatException("TIFF files with different bit depths per sample aren't supported.", ImageFormat.Tiff);
            }
        }

        bool indexed = Photometric is Palette or WhiteIsZero or BlackIsZero;
        if (Bits is not (8 or 16) && !(indexed && SamplesPerPixel == 1 && Bits is 1 or 2 or 4))
        {
            throw new UnsupportedImageFormatException(
                string.Create(CultureInfo.InvariantCulture, $"TIFF files of {Bits} bits per sample aren't supported here."),
                ImageFormat.Tiff);
        }

        if (Photometric == Palette && (Bits > 8 || ColorMap.Length < 3 << Bits))
        {
            throw ImageErrors.Corrupt("The TIFF file's colour map is missing or too short.");
        }

        if (Planar is not (1 or 2))
        {
            throw ImageErrors.Corrupt("The TIFF file's PlanarConfiguration isn't 1 or 2.");
        }

        if (Predictor is not (1 or 2))
        {
            throw new UnsupportedImageFormatException("TIFF files with the floating-point predictor aren't supported.", ImageFormat.Tiff);
        }

        if (Predictor == 2 && Bits < 8)
        {
            throw ImageErrors.Corrupt("The TIFF file uses the horizontal predictor on samples of fewer than 8 bits.");
        }

        if (Tiled && (TileWidth <= 0 || TileHeight <= 0 || TileWidth % 16 != 0 || TileHeight % 16 != 0))
        {
            throw ImageErrors.Corrupt("The TIFF file's tiles aren't a positive multiple of 16 on each side.");
        }

        // A tile may overhang a small image, but not by more than an honest writer would.
        if (Tiled && (TileWidth > Math.Max(Width, 1024) + 15 || TileHeight > Math.Max(Height, 1024) + 15))
        {
            throw ImageErrors.Corrupt("The TIFF file's tiles are far larger than its image.");
        }

        long chunks = (long)ChunksAcross * ChunksDown * (Planar == 2 ? SamplesPerPixel : 1);
        if (Offsets.Length < chunks || ByteCounts.Length < chunks)
        {
            throw ImageErrors.Corrupt("The TIFF file has fewer strip or tile offsets than its image needs.");
        }

        return this;
    }

    /// <summary>Chunks (strips or tiles) across the image.</summary>
    public int ChunksAcross => Tiled ? (int)(((long)Width + TileWidth - 1) / TileWidth) : 1;

    /// <summary>Chunks down the image.</summary>
    public int ChunksDown => Tiled ? (int)(((long)Height + TileHeight - 1) / TileHeight) : (int)(((long)Height + RowsPerStrip - 1) / RowsPerStrip);

    private static int U16(ReadOnlySpan<byte> span, bool big) => big ? BinaryPrimitives.ReadUInt16BigEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span);

    private static long U32(ReadOnlySpan<byte> span, bool big) => big ? BinaryPrimitives.ReadUInt32BigEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span);
}
