using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Codecs.Tiff;

/// <summary>
/// Reads a TIFF's first directory for <see cref="Image.Identify"/>: it skips ahead to the directory and reads only the
/// values kept in the entries themselves (size, samples, photometric interpretation, extra samples, orientation), so
/// even a directory at the end of a large file costs the skip and a few hundred bytes.
/// </summary>
internal static class TiffHeaderReader
{
    /// <summary>Reads the headers. The reader is at offset 0, before the signature.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="options">The options.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What the headers say.</returns>
    public static async ValueTask<ImageInfo> ReadAsync(HeaderReader reader, IdentifyOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);
        byte[] head = new byte[8];
        await reader.ReadExactlyAsync(head, cancellationToken).ConfigureAwait(false);
        bool big = head[0] == (byte)'M';
        int magic = big ? BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(2)) : BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(2));
        if (magic == 43)
        {
            throw new UnsupportedImageFormatException("BigTIFF files aren't supported.", ImageFormat.Tiff);
        }

        long ifd = big ? BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4)) : BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
        if (magic != 42 || ifd < 8)
        {
            throw ImageErrors.Corrupt("The TIFF file doesn't start with a TIFF header.");
        }

        await reader.SkipAsync(ifd - 8, cancellationToken).ConfigureAwait(false);
        byte[] two = new byte[2];
        await reader.ReadExactlyAsync(two, cancellationToken).ConfigureAwait(false);
        int count = big ? BinaryPrimitives.ReadUInt16BigEndian(two) : BinaryPrimitives.ReadUInt16LittleEndian(two);
        if (count is 0 or > 4096)
        {
            throw ImageErrors.Corrupt("The TIFF file's first directory is damaged.");
        }

        byte[] entries = new byte[12 * count];
        await reader.ReadExactlyAsync(entries, cancellationToken).ConfigureAwait(false);
        long width = 0, height = 0, samples = 1, photometric = -1, extra = 0, orientation = 1;
        bool icc = false, exif = false;
        for (int e = 0; e < count; e++)
        {
            ReadOnlySpan<byte> entry = entries.AsSpan(12 * e, 12);
            int tag = big ? BinaryPrimitives.ReadUInt16BigEndian(entry) : BinaryPrimitives.ReadUInt16LittleEndian(entry);
            int type = big ? BinaryPrimitives.ReadUInt16BigEndian(entry[2..]) : BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]);
            long value = type switch
            {
                3 => big ? BinaryPrimitives.ReadUInt16BigEndian(entry[8..]) : BinaryPrimitives.ReadUInt16LittleEndian(entry[8..]),
                4 => big ? BinaryPrimitives.ReadUInt32BigEndian(entry[8..]) : BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]),
                1 => entry[8],
                _ => -1,
            };
            switch (tag)
            {
                case 256:
                    width = value;
                    break;
                case 257:
                    height = value;
                    break;
                case 262:
                    photometric = value;
                    break;
                case 274:
                    orientation = value;
                    break;
                case 277:
                    samples = value;
                    break;
                case 338:
                    extra = value;
                    break;
                case 34675:
                    icc = true;
                    break;
                case 34665:
                    exif = true;
                    break;
            }
        }

        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue)
        {
            throw ImageErrors.Corrupt("The TIFF file's directory has no usable size.");
        }

        int colour = photometric switch { 2 or 6 or 8 => 3, 5 => 4, _ => 1 };
        return new ImageInfo(ImageFormat.Tiff, (int)width, (int)height)
        {
            HasAlpha = samples > colour && extra is 1 or 2,
            Orientation = orientation is >= 1 and <= 8 ? (int)orientation : 1,
            HasIccProfile = icc,
            HasExif = exif,
            ColorSpace = options.ReadColorSpace && !icc ? ColorSpace.Srgb : null,
        };
    }
}
