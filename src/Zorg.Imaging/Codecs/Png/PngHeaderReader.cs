using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Formats;
using Zorg.Imaging.Metadata;

namespace Zorg.Imaging.Codecs.Png;

/// <summary>Reads the PNG (and APNG) headers for <see cref="Image.Identify"/>, from IHDR up to the first IDAT.</summary>
internal static class PngHeaderReader
{
    private const uint Ihdr = 0x49484452;
    private const uint Idat = 0x49444154;
    private const uint Iend = 0x49454E44;
    private const uint Actl = 0x6163544C;
    private const uint Trns = 0x74524E53;
    private const uint Iccp = 0x69434350;
    private const uint Exif = 0x65584966;
    private const uint CgBI = 0x43674249;

    private const int SignatureLength = 8;
    private const int IhdrLength = 13;
    private const int CrcLength = 4;
    private const long MaxChunkLength = int.MaxValue;

    /// <summary>Reads the headers. The reader is at offset 0, before the signature.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="options">The options.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What the headers say.</returns>
    public static async ValueTask<ImageInfo> ReadAsync(HeaderReader reader, IdentifyOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);

        await reader.SkipAsync(SignatureLength, cancellationToken).ConfigureAwait(false);

        byte[] head = new byte[8];
        await reader.ReadExactlyAsync(head, cancellationToken).ConfigureAwait(false);
        CheckFirstChunk(head);

        byte[] ihdr = new byte[IhdrLength + CrcLength];
        await reader.ReadExactlyAsync(ihdr, cancellationToken).ConfigureAwait(false);
        (int width, int height, int colourType) = ParseIhdr(ihdr);

        bool hasAlpha = colourType is 4 or 6;
        bool hasIcc = false;
        bool hasExif = false;
        int orientation = 1;
        int frames = 1;
        bool sawActl = false;

        while (true)
        {
            await reader.ReadExactlyAsync(head, cancellationToken).ConfigureAwait(false);
            long length = BinaryPrimitives.ReadUInt32BigEndian(head);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
            CheckChunkHeader(length, head);

            if (type == Idat)
            {
                break;
            }

            if (type == Iend)
            {
                throw ImageErrors.Corrupt("The PNG file has no image data: its IEND chunk comes before any IDAT chunk.");
            }

            long skip = length + CrcLength;
            switch (type)
            {
                case Actl when !sawActl:
                    if (length != 8)
                    {
                        throw ImageErrors.Corrupt("The PNG file's acTL chunk isn't 8 bytes long.");
                    }

                    byte[] actl = new byte[8];
                    await reader.ReadExactlyAsync(actl, cancellationToken).ConfigureAwait(false);
                    uint declared = BinaryPrimitives.ReadUInt32BigEndian(actl);
                    if (declared == 0)
                    {
                        throw ImageErrors.Corrupt("The PNG file's acTL chunk declares zero frames.");
                    }

                    sawActl = true;
                    frames = (int)Math.Min(declared, int.MaxValue);
                    skip = CrcLength;
                    break;

                case Trns:
                    hasAlpha = true;
                    break;

                case Iccp:
                    hasIcc = true;
                    break;

                case Exif when !hasExif:
                    hasExif = true;
                    (orientation, int read) = await ExifOrientation.ReadAsync(
                        reader, ReadOnlyMemory<byte>.Empty, length, cancellationToken).ConfigureAwait(false);
                    skip = length - read + CrcLength;
                    break;

                default:
                    break;
            }

            await reader.SkipAsync(skip, cancellationToken).ConfigureAwait(false);
        }

        return new ImageInfo(ImageFormat.Png, width, height)
        {
            Orientation = orientation,
            HasAlpha = hasAlpha,
            IsAnimated = frames > 1,
            FrameCount = frames,
            HasIccProfile = hasIcc,
            HasExif = hasExif,
        };
    }

    private static void CheckFirstChunk(byte[] head)
    {
        uint type = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
        if (type == CgBI)
        {
            throw ImageErrors.Corrupt("The PNG file is in Apple's iOS-optimised format (it starts with a CgBI chunk), which isn't a standard PNG.");
        }

        if (type != Ihdr)
        {
            throw ImageErrors.Corrupt("The PNG file's first chunk isn't IHDR.");
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(head) != IhdrLength)
        {
            throw ImageErrors.Corrupt("The PNG file's IHDR chunk isn't 13 bytes long.");
        }
    }

    private static (int Width, int Height, int ColourType) ParseIhdr(byte[] ihdr)
    {
        ReadOnlySpan<byte> data = ihdr.AsSpan(0, IhdrLength);
        uint crc = Crc32.Append(Crc32.Compute("IHDR"u8), data);
        if (crc != BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(IhdrLength)))
        {
            throw ImageErrors.Corrupt("The PNG file's IHDR chunk fails its checksum.");
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(data);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
        {
            throw ImageErrors.Corrupt("The PNG file's width or height is zero or over 2147483647.");
        }

        int bitDepth = data[8];
        int colourType = data[9];
        bool valid = colourType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 or 4 or 6 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            _ => false,
        };
        if (!valid)
        {
            throw ImageErrors.Corrupt("The PNG file's bit depth and colour type aren't a valid combination.");
        }

        if (data[10] != 0)
        {
            throw ImageErrors.Corrupt("The PNG file's compression method isn't 0.");
        }

        if (data[11] != 0)
        {
            throw ImageErrors.Corrupt("The PNG file's filter method isn't 0.");
        }

        if (data[12] > 1)
        {
            throw ImageErrors.Corrupt("The PNG file's interlace method isn't 0 or 1.");
        }

        return ((int)width, (int)height, colourType);
    }

    private static void CheckChunkHeader(long length, byte[] head)
    {
        if (length > MaxChunkLength)
        {
            throw ImageErrors.Corrupt("The PNG file has a chunk longer than 2147483647 bytes.");
        }

        for (int i = 4; i < 8; i++)
        {
            int b = head[i] | 0x20;
            if (b is < 'a' or > 'z')
            {
                throw ImageErrors.Corrupt("The PNG file has a chunk whose type isn't four letters.");
            }
        }
    }
}
