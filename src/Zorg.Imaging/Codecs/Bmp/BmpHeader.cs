using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Codecs.Bmp;

/// <summary>
/// A BMP's file header and DIB header (Microsoft's BITMAPCOREHEADER, BITMAPINFOHEADER and its V2 to V5 extensions, and
/// OS/2's 64-byte header): the size, row order, bits per pixel, compression, channel masks, palette and profile.
/// </summary>
internal sealed class BmpHeader
{
    public const int Rgb = 0;
    public const int Rle8 = 1;
    public const int Rle4 = 2;
    public const int BitFields = 3;
    public const int AlphaBitFields = 6;

    /// <summary>The longest file and DIB header, with the 16 bytes of masks that may follow a 40-byte header.</summary>
    public const int MaxLength = 14 + 124 + 16;

    public int Width { get; private init; }

    public int Height { get; private init; }

    /// <summary>Whether the first row stored is the top one (a negative height); BMPs are usually stored bottom up.</summary>
    public bool TopDown { get; private init; }

    public int BitCount { get; private init; }

    public int Compression { get; private init; }

    public uint RedMask { get; private init; }

    public uint GreenMask { get; private init; }

    public uint BlueMask { get; private init; }

    public uint AlphaMask { get; private init; }

    /// <summary>Where the pixels start, from the start of the file.</summary>
    public long PixelOffset { get; private init; }

    /// <summary>How many bytes the file and DIB headers take, the masks after a 40-byte header included.</summary>
    public int Length { get; private init; }

    /// <summary>Palette entries stored after the headers.</summary>
    public int PaletteEntries { get; private init; }

    /// <summary>Bytes per palette entry: 3 for the 12-byte header, else 4.</summary>
    public int PaletteEntrySize { get; private init; }

    /// <summary>An embedded ICC profile's offset from the start of the file and its size, or 0 and 0.</summary>
    public (long Offset, int Size) Profile { get; private init; }

    /// <summary>The stated size of the pixel data, 0 when not given.</summary>
    public long ImageSize { get; private init; }

    /// <summary>Bytes in a stored row of uncompressed pixels: padded to four.</summary>
    public long Stride => (((long)Width * BitCount) + 31) / 32 * 4;

    public bool HasAlphaMask => AlphaMask != 0;

    /// <summary>How many header bytes to read: the DIB header's size says.</summary>
    /// <param name="head">At least the first 18 bytes.</param>
    public static int LengthToRead(ReadOnlySpan<byte> head)
    {
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(head[14..]);
        if (size is not (12 or 40 or 52 or 56 or 64 or 108 or 124))
        {
            throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The BMP's DIB header is {size} bytes; it should be 12, 40, 52, 56, 64, 108 or 124."));
        }

        // A 40-byte header with bit fields is followed by its masks.
        int compression = size >= 40 && head.Length >= 34 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(head[30..]) : 0;
        int masks = size == 40 ? compression == BitFields ? 12 : compression == AlphaBitFields ? 16 : 0 : 0;
        return 14 + (int)size + masks;
    }

    /// <summary>Parses the headers.</summary>
    /// <param name="head">The first <see cref="LengthToRead"/> bytes of the file.</param>
    public static BmpHeader Parse(ReadOnlySpan<byte> head)
    {
        long pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(head[10..]);
        int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(head[14..]);
        ReadOnlySpan<byte> dib = head.Slice(14, size);
        if (size == 12)
        {
            int bits = BinaryPrimitives.ReadUInt16LittleEndian(dib[10..]);
            return Check(new BmpHeader
            {
                Width = BinaryPrimitives.ReadUInt16LittleEndian(dib[4..]),
                Height = BinaryPrimitives.ReadUInt16LittleEndian(dib[6..]),
                BitCount = bits,
                Compression = Rgb,
                PixelOffset = pixelOffset,
                Length = 14 + size,
                PaletteEntries = bits <= 8 ? 1 << bits : 0,
                PaletteEntrySize = 3,
            });
        }

        int width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        int bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        int compression = (int)BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        long imageSize = BinaryPrimitives.ReadUInt32LittleEndian(dib[20..]);
        long coloursUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
        if (size == 64 && compression is 3 or 4)
        {
            throw new UnsupportedImageFormatException(
                compression == 3 ? "OS/2 BMPs with Huffman coding aren't supported." : "OS/2 BMPs with 24-bit run lengths aren't supported.",
                ImageFormat.Bmp);
        }

        if (compression is 4 or 5)
        {
            throw new UnsupportedImageFormatException(
                compression == 4 ? "BMPs that hold a JPEG aren't supported." : "BMPs that hold a PNG aren't supported.",
                ImageFormat.Bmp);
        }

        if (compression is not (Rgb or Rle8 or Rle4 or BitFields or AlphaBitFields))
        {
            throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The BMP's compression is {compression}, which isn't a BMP compression."));
        }

        // The masks: in the header from 52 bytes, or just after a 40-byte one, or the defaults for the bit count.
        ReadOnlySpan<byte> masks = size >= 52 ? dib[40..] : head.Length >= 14 + 40 + 12 ? head[(14 + 40)..] : default;
        uint red = 0, green = 0, blue = 0, alpha = 0;
        if (compression is BitFields or AlphaBitFields)
        {
            if (bitCount is not (16 or 32))
            {
                throw ImageErrors.Corrupt("A BMP with bit fields must have 16 or 32 bits per pixel.");
            }

            red = BinaryPrimitives.ReadUInt32LittleEndian(masks);
            green = BinaryPrimitives.ReadUInt32LittleEndian(masks[4..]);
            blue = BinaryPrimitives.ReadUInt32LittleEndian(masks[8..]);
            alpha = size >= 56 || compression == AlphaBitFields ? BinaryPrimitives.ReadUInt32LittleEndian(masks[12..]) : 0;
        }
        else if (bitCount == 16)
        {
            (red, green, blue) = (0x7C00, 0x03E0, 0x001F);
        }
        else if (bitCount == 32)
        {
            (red, green, blue, alpha) = (0xFF0000, 0xFF00, 0xFF, 0xFF000000);
        }

        (long Offset, int Size) profile = (0, 0);
        if (size >= 124 && BinaryPrimitives.ReadUInt32BigEndian(dib[56..]) == 0x4D424544u)
        {
            // PROFILE_EMBEDDED ('MBED'): the offset counts from the start of the DIB header.
            profile = (14 + (long)BinaryPrimitives.ReadUInt32LittleEndian(dib[112..]), (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(dib[116..]), int.MaxValue));
        }

        int palette = bitCount <= 8 ? (int)(coloursUsed == 0 ? 1L << bitCount : Math.Min(coloursUsed, 1L << bitCount)) : 0;
        if (height == int.MinValue)
        {
            throw ImageErrors.Corrupt("The BMP's height is out of range.");
        }

        return Check(new BmpHeader
        {
            Width = width,
            Height = Math.Abs(height),
            TopDown = height < 0,
            BitCount = bitCount,
            Compression = compression,
            RedMask = red,
            GreenMask = green,
            BlueMask = blue,
            AlphaMask = alpha,
            PixelOffset = pixelOffset,
            Length = head.Length,
            PaletteEntries = palette,
            PaletteEntrySize = 4,
            Profile = profile,
            ImageSize = imageSize,
        });
    }

    private static BmpHeader Check(BmpHeader header)
    {
        if (header.Width <= 0 || header.Height <= 0)
        {
            throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The BMP is {header.Width} x {header.Height}; both sides must be at least 1."));
        }

        if (header.BitCount is not (1 or 2 or 4 or 8 or 16 or 24 or 32))
        {
            throw header.BitCount == 64
                ? new UnsupportedImageFormatException("BMPs of 64 bits per pixel aren't supported.", ImageFormat.Bmp)
                : ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The BMP has {header.BitCount} bits per pixel, which BMP doesn't allow."));
        }

        if ((header.Compression == Rle8 && header.BitCount != 8) || (header.Compression == Rle4 && header.BitCount != 4))
        {
            throw ImageErrors.Corrupt("The BMP's run-length compression doesn't match its bits per pixel.");
        }

        if (header.Compression is Rle8 or Rle4 && header.TopDown)
        {
            throw ImageErrors.Corrupt("A run-length BMP can't be stored top down.");
        }

        if (header.PixelOffset < header.Length)
        {
            throw ImageErrors.Corrupt("The BMP's pixels start inside its headers.");
        }

        return header;
    }

    /// <summary>A channel's value from a pixel by its mask, scaled to 0 to 255.</summary>
    public static uint Channel(uint pixel, uint mask)
    {
        if (mask == 0)
        {
            return 0;
        }

        int shift = BitOperations.TrailingZeroCount(mask);
        ulong most = mask >> shift;
        ulong value = (pixel & mask) >> shift;
        return (uint)(((value * 255) + (most / 2)) / most);
    }
}
