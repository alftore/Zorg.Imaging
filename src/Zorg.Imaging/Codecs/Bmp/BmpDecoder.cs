using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Formats;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Codecs.Bmp;

/// <summary>Decodes a BMP (Windows and OS/2 bitmaps).</summary>
/// <remarks>
/// <para>
/// 1, 2, 4 and 8 bits per pixel through a palette, 16 and 32 through channel masks (the defaults, or bit fields), and
/// 24; uncompressed, or run-length coded (RLE8 and RLE4), stored bottom up or top down. A V5 header's embedded ICC
/// profile becomes the colour space. 32-bit pixels use their alpha only when some pixel's alpha isn't 0, as browsers
/// do, since many writers leave that byte empty. Pixels a run-length file skips are transparent.
/// </para>
/// <para>
/// Hostile input: the size is checked against <see cref="ImageLimits.MaxPixels"/> before anything is allocated, and
/// run-length data is read no further than its pixels could need.
/// </para>
/// </remarks>
internal static class BmpDecoder
{
    public static async ValueTask<Image> DecodeAsync(DataReader reader, DecodeOptions options, CancellationToken cancellationToken)
    {
        reader.FormatName = "BMP";
        BmpHeader header = await ReadHeaderAsync(reader, cancellationToken).ConfigureAwait(false);
        long pixels = (long)header.Width * header.Height;
        if (pixels > options.Limits.MaxPixels)
        {
            throw ImageErrors.ExceedsLimits(string.Create(
                CultureInfo.InvariantCulture,
                $"The BMP image is {header.Width} x {header.Height}, {pixels} pixels, more than the {options.Limits.MaxPixels}-pixel limit (ImageLimits.MaxPixels)."));
        }

        uint[] palette = await ReadPaletteAsync(reader, header, cancellationToken).ConfigureAwait(false);
        ColorSpace colorSpace = ColorSpace.Srgb;
        if (header.Profile.Size > 0 && header.Profile.Offset < header.PixelOffset && header.Profile.Offset >= reader.Position)
        {
            colorSpace = await ReadProfileAsync(reader, header, cancellationToken).ConfigureAwait(false);
        }

        await reader.SkipAsync(header.PixelOffset - reader.Position, cancellationToken).ConfigureAwait(false);
        uint[] argb = new uint[pixels];
        bool alpha;
        if (header.Compression is BmpHeader.Rle8 or BmpHeader.Rle4)
        {
            alpha = await ReadRunLengthAsync(reader, header, palette, argb, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            alpha = await ReadRowsAsync(reader, header, palette, argb, cancellationToken).ConfigureAwait(false);
        }

        if (header.Profile.Size > 0 && header.Profile.Offset >= reader.Position)
        {
            colorSpace = await ReadProfileAsync(reader, header, cancellationToken).ConfigureAwait(false);
        }

        reader.Finish();
        PixelFormat format = options.PixelFormat != PixelFormat.None ? options.PixelFormat : alpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24;
        Image image = Argb.ToImage(argb, header.Width, header.Height, alpha, format);
        image.ColorSpace = colorSpace;
        return image;
    }

    /// <summary>Reads the file and DIB headers. The reader is at the signature.</summary>
    public static async ValueTask<BmpHeader> ReadHeaderAsync(DataReader reader, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> peek = await reader.PeekAsync(34, cancellationToken).ConfigureAwait(false);
        if (peek.Length < 18)
        {
            throw ImageErrors.Truncated("The BMP file ends inside its header.");
        }

        byte[] head = new byte[BmpHeader.LengthToRead(peek.Span)];
        await reader.ReadExactlyAsync(head, cancellationToken).ConfigureAwait(false);
        return BmpHeader.Parse(head);
    }

    private static async ValueTask<uint[]> ReadPaletteAsync(DataReader reader, BmpHeader header, CancellationToken cancellationToken)
    {
        // Indices past the palette show black, as browsers draw them.
        uint[] palette = new uint[256];
        Array.Fill(palette, 0xFF000000u);
        if (header.PaletteEntries == 0)
        {
            return palette;
        }

        long room = header.PixelOffset - reader.Position;
        int entries = (int)Math.Min(header.PaletteEntries, room / header.PaletteEntrySize);
        byte[] table = new byte[entries * header.PaletteEntrySize];
        await reader.ReadExactlyAsync(table, cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < entries; i++)
        {
            int at = i * header.PaletteEntrySize;
            palette[i] = 0xFF000000u | ((uint)table[at + 2] << 16) | ((uint)table[at + 1] << 8) | table[at];
        }

        return palette;
    }

    private static async ValueTask<ColorSpace> ReadProfileAsync(DataReader reader, BmpHeader header, CancellationToken cancellationToken)
    {
        if (header.Profile.Size > ColorSpace.MaxIccProfileBytes)
        {
            throw ImageErrors.ExceedsLimits(string.Create(CultureInfo.InvariantCulture, $"The BMP's ICC profile is {header.Profile.Size} bytes, more than {ColorSpace.MaxIccProfileBytes}."));
        }

        await reader.SkipAsync(header.Profile.Offset - reader.Position, cancellationToken).ConfigureAwait(false);
        byte[] profile = new byte[header.Profile.Size];
        await reader.ReadExactlyAsync(profile, cancellationToken).ConfigureAwait(false);
        string? problem = ColorSpace.CheckIccProfile(profile);
        return problem is null ? ColorSpace.FromCheckedIccProfile(profile) : throw ImageErrors.Corrupt(problem);
    }

    // Uncompressed rows, padded to four bytes, bottom up unless the header says otherwise.
    private static async ValueTask<bool> ReadRowsAsync(DataReader reader, BmpHeader header, uint[] palette, uint[] argb, CancellationToken cancellationToken)
    {
        int width = header.Width;
        byte[] row = new byte[header.Stride];
        bool anyAlpha = false;
        for (int stored = 0; stored < header.Height; stored++)
        {
            await reader.ReadExactlyAsync(row, cancellationToken).ConfigureAwait(false);
            int y = header.TopDown ? stored : header.Height - 1 - stored;
            Span<uint> target = argb.AsSpan(y * width, width);
            switch (header.BitCount)
            {
                case 24:
                    for (int x = 0; x < width; x++)
                    {
                        target[x] = 0xFF000000u | ((uint)row[(3 * x) + 2] << 16) | ((uint)row[(3 * x) + 1] << 8) | row[3 * x];
                    }

                    break;

                case 16 or 32:
                    for (int x = 0; x < width; x++)
                    {
                        uint p = header.BitCount == 16 ? (uint)(row[2 * x] | (row[(2 * x) + 1] << 8)) : BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(4 * x));
                        uint a = header.HasAlphaMask ? BmpHeader.Channel(p, header.AlphaMask) : 255;
                        anyAlpha |= header.HasAlphaMask && a != 0;
                        target[x] = (a << 24) | (BmpHeader.Channel(p, header.RedMask) << 16) | (BmpHeader.Channel(p, header.GreenMask) << 8) | BmpHeader.Channel(p, header.BlueMask);
                    }

                    break;

                default:
                    int bits = header.BitCount;
                    int perByte = 8 / bits;
                    int mask = (1 << bits) - 1;
                    for (int x = 0; x < width; x++)
                    {
                        int shift = 8 - (bits * ((x % perByte) + 1));
                        target[x] = palette[(row[x / perByte] >> shift) & mask];
                    }

                    break;
            }
        }

        if (header.HasAlphaMask && !anyAlpha)
        {
            // Every alpha is 0: the writer left the byte empty, so the image is opaque.
            for (int i = 0; i < argb.Length; i++)
            {
                argb[i] |= 0xFF000000u;
            }
        }

        return anyAlpha && Array.Exists(argb, p => p < 0xFF000000u);
    }

    // RLE8 and RLE4 (the BMP specification's run-length coding): runs of one index (two alternating, for RLE4),
    // literal runs padded to a word, and escapes for the end of a row, the end of the image and a move. Pixels never
    // drawn stay transparent.
    private static async ValueTask<bool> ReadRunLengthAsync(DataReader reader, BmpHeader header, uint[] palette, uint[] argb, CancellationToken cancellationToken)
    {
        int width = header.Width;
        int height = header.Height;
        bool four = header.Compression == BmpHeader.Rle4;

        // A pixel costs at most two bytes, and each row and move a few more; anything past that can't be honest.
        long cap = (2 * (long)width * height) + (4L * height) + 4096;
        long size = header.ImageSize > 0 ? Math.Min(header.ImageSize, cap) : cap;
        int most = (int)Math.Min(size, Array.MaxLength);
        byte[] data = new byte[Math.Min(most, 1 << 16)];
        int length = 0;
        while (length < most)
        {
            if (length == data.Length)
            {
                Array.Resize(ref data, (int)Math.Min((long)data.Length * 2, most));
            }

            int read = await reader.ReadSomeAsync(data.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        int x = 0;
        int y = 0;
        long drawn = 0;
        int at = 0;
        void Put(int index)
        {
            if (x < width && y < height)
            {
                argb[((height - 1 - y) * width) + x] = palette[index];
                drawn++;
            }

            x++;
        }

        while (at + 1 < length && y < height)
        {
            int count = data[at];
            int value = data[at + 1];
            at += 2;
            if (count > 0)
            {
                for (int k = 0; k < count; k++)
                {
                    Put(four ? (k % 2 == 0 ? value >> 4 : value & 0xF) : value);
                }

                continue;
            }

            if (value == 0)
            {
                (x, y) = (0, y + 1);
            }
            else if (value == 1)
            {
                break;
            }
            else if (value == 2)
            {
                if (at + 1 >= length)
                {
                    break;
                }

                x += data[at];
                y += data[at + 1];
                at += 2;
            }
            else
            {
                int bytes = four ? (value + 1) / 2 : value;
                if (at + bytes > length)
                {
                    break;
                }

                for (int k = 0; k < value; k++)
                {
                    Put(four ? (k % 2 == 0 ? data[at + (k / 2)] >> 4 : data[at + (k / 2)] & 0xF) : data[at + k]);
                }

                at += bytes + (bytes & 1);
            }
        }

        return drawn < (long)width * height && Array.Exists(argb, p => p == 0);
    }
}
