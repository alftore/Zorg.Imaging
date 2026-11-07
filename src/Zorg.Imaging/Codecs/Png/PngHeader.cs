using System;
using System.Buffers.Binary;
using System.Globalization;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Codecs.Png;

/// <summary>A checked IHDR chunk.</summary>
internal readonly record struct PngHeader(int Width, int Height, int BitDepth, int ColourType, bool Interlaced)
{
    /// <summary>Samples per pixel: 1 grey, 3 RGB, 1 palette index, 2 grey and alpha, 4 RGBA.</summary>
    public int Channels => ColourType switch
    {
        2 => 3,
        4 => 2,
        6 => 4,
        _ => 1,
    };

    /// <summary>Bits per pixel in the file.</summary>
    public int BitsPerPixel => Channels * BitDepth;

    /// <summary>The byte distance the Sub, Average and Paeth filters look back: whole pixels, at least one byte.</summary>
    public int FilterBytesPerPixel => Math.Max(1, BitsPerPixel / 8);

    /// <summary>Parses and checks IHDR's 13 bytes.</summary>
    /// <param name="ihdr">The chunk data.</param>
    /// <returns>The header.</returns>
    /// <exception cref="ImageFormatException">A field is out of range.</exception>
    public static PngHeader Parse(ReadOnlySpan<byte> ihdr)
    {
        uint width = BinaryPrimitives.ReadUInt32BigEndian(ihdr);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(ihdr[4..]);
        int depth = ihdr[8];
        int colour = ihdr[9];

        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
        {
            throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The PNG file's IHDR gives a size of {width} x {height}; each side must be 1 to 2^31 - 1."));
        }

        bool depthValid = colour switch
        {
            0 => depth is 1 or 2 or 4 or 8 or 16,
            3 => depth is 1 or 2 or 4 or 8,
            2 or 4 or 6 => depth is 8 or 16,
            _ => throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The PNG file's IHDR has colour type {colour}, which doesn't exist.")),
        };
        if (!depthValid)
        {
            throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The PNG file's IHDR has bit depth {depth}, which colour type {colour} doesn't allow."));
        }

        if (ihdr[10] != 0 || ihdr[11] != 0 || ihdr[12] > 1)
        {
            throw ImageErrors.Corrupt("The PNG file's IHDR has an unknown compression, filter or interlace method.");
        }

        return new PngHeader((int)width, (int)height, depth, colour, ihdr[12] == 1);
    }

    /// <summary>The bytes of one row of <paramref name="pixels"/> pixels, without the filter byte.</summary>
    /// <param name="pixels">The row's width in pixels.</param>
    /// <returns>The byte count.</returns>
    public int RowBytes(int pixels) => (int)((((long)pixels * BitsPerPixel) + 7) / 8);

    /// <summary>Checks the size against the limits, and against what memory can hold, before anything is allocated.</summary>
    /// <param name="limits">The limits.</param>
    /// <exception cref="ImageLimitException">The image is too large.</exception>
    public void CheckLimits(ImageLimits limits)
    {
        long pixels = (long)Width * Height;
        if (pixels > limits.MaxPixels)
        {
            throw ImageErrors.ExceedsLimits(string.Create(
                CultureInfo.InvariantCulture,
                $"The PNG image is {Width} x {Height}, {pixels} pixels, more than the {limits.MaxPixels}-pixel limit (ImageLimits.MaxPixels)."));
        }

        // Four bytes a pixel is the most any decoded layout takes; a raw row may take eight (16-bit RGBA).
        if (pixels * 4 > Array.MaxLength || ((long)Width * BitsPerPixel / 8) + 1 > Array.MaxLength)
        {
            throw ImageErrors.ExceedsLimits(string.Create(
                CultureInfo.InvariantCulture,
                $"The PNG image is {Width} x {Height}, too large to hold in memory as one image."));
        }
    }

    /// <summary>Creates the image the rows are decoded into.</summary>
    /// <param name="format">Its layout.</param>
    /// <returns>The image.</returns>
    public Image CreateImage(PixelFormat format) => new(Width, Height, format);
}
