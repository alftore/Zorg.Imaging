using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Formats;
using Zorg.Imaging.Metadata;

namespace Zorg.Imaging.Codecs.Webp;

/// <summary>
/// Reads the WebP headers for <see cref="Image.Identify"/>, from RFC 9649 (container and lossless) and RFC 6386 (VP8
/// frame header).
/// </summary>
internal static class WebpHeaderReader
{
    private const int ChunkHeaderLength = 8;
    private const int Vp8xLength = 10;
    private const byte IccFlag = 0x20;
    private const byte AlphaFlag = 0x10;
    private const byte ExifFlag = 0x08;
    private const byte AnimationFlag = 0x02;

    /// <summary>Reads the headers. The reader is at offset 0, before the signature.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="options">The options.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What the headers say.</returns>
    public static async ValueTask<ImageInfo> ReadAsync(HeaderReader reader, IdentifyOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);

        byte[] buffer = new byte[12];
        await reader.ReadExactlyAsync(buffer.AsMemory(0, 12), cancellationToken).ConfigureAwait(false);
        if (!buffer.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !buffer.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            throw ImageErrors.Corrupt("The WebP file doesn't start with a RIFF WEBP header.");
        }

        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4));
        if (riffSize < 4)
        {
            throw ImageErrors.Corrupt("The WebP file has an invalid RIFF size.");
        }

        long end = 8L + riffSize;

        (string fourCc, uint size) = await ReadChunkHeaderAsync(reader, end, buffer, first: true, cancellationToken).ConfigureAwait(false);
        switch (fourCc)
        {
            case "VP8 ":
            {
                if (size < 10)
                {
                    throw ImageErrors.Corrupt("The WebP VP8 chunk is too short.");
                }

                await reader.ReadExactlyAsync(buffer.AsMemory(0, 10), cancellationToken).ConfigureAwait(false);
                if ((buffer[0] & 1) != 0)
                {
                    throw ImageErrors.Corrupt("The WebP VP8 chunk doesn't start with a key frame.");
                }

                if (buffer[3] != 0x9D || buffer[4] != 0x01 || buffer[5] != 0x2A)
                {
                    throw ImageErrors.Corrupt("The WebP VP8 chunk has no start code.");
                }

                int width = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(6)) & 0x3FFF;
                int height = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(8)) & 0x3FFF;
                return Still(width, height, hasAlpha: false, options);
            }

            case "VP8L":
            {
                if (size < 5)
                {
                    throw ImageErrors.Corrupt("The WebP VP8L chunk is too short.");
                }

                await reader.ReadExactlyAsync(buffer.AsMemory(0, 5), cancellationToken).ConfigureAwait(false);
                if (buffer[0] != 0x2F)
                {
                    throw ImageErrors.Corrupt("The WebP VP8L chunk has a bad signature.");
                }

                uint bits = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(1));
                if ((bits >> 29) != 0)
                {
                    throw ImageErrors.Corrupt("The WebP VP8L chunk has an unsupported version.");
                }

                int width = (int)(bits & 0x3FFF) + 1;
                int height = (int)((bits >> 14) & 0x3FFF) + 1;
                return Still(width, height, hasAlpha: ((bits >> 28) & 1) != 0, options);
            }

            case "VP8X":
                return await ReadExtendedAsync(reader, options, end, size, buffer, cancellationToken).ConfigureAwait(false);

            default:
                throw ImageErrors.Corrupt("The WebP file doesn't start with a VP8, VP8L or VP8X chunk.");
        }
    }

    private static async ValueTask<ImageInfo> ReadExtendedAsync(
        HeaderReader reader,
        IdentifyOptions options,
        long end,
        uint size,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        if (size < Vp8xLength)
        {
            throw ImageErrors.Corrupt("The WebP VP8X chunk is too short.");
        }

        await reader.ReadExactlyAsync(buffer.AsMemory(0, Vp8xLength), cancellationToken).ConfigureAwait(false);
        byte flags = buffer[0];
        int width = ReadUInt24(buffer.AsSpan(4)) + 1;
        int height = ReadUInt24(buffer.AsSpan(7)) + 1;
        bool animated = (flags & AnimationFlag) != 0;
        bool hasExif = (flags & ExifFlag) != 0;
        bool countFrames = animated && options.CountFrames;

        int orientation = 1;
        int frames = 0;
        bool readIcc = options.ReadColorSpace && (flags & IccFlag) != 0;
        ColorSpace? colorSpace = options.ReadColorSpace ? ColorSpace.Srgb : null;
        if (countFrames || hasExif || readIcc)
        {
            await SkipChunkRestAsync(reader, end, size, Vp8xLength, cancellationToken).ConfigureAwait(false);
            bool exifFound = false;

            // Each iteration consumes at least the 8-byte chunk header, so this ends at the RIFF end.
            while (end - reader.Position >= ChunkHeaderLength)
            {
                (string fourCc, uint chunkSize) = await ReadChunkHeaderAsync(reader, end, buffer, first: false, cancellationToken).ConfigureAwait(false);
                int read = 0;
                if (readIcc && fourCc == "ICCP")
                {
                    readIcc = false;
                    if (chunkSize > ColorSpace.MaxIccProfileBytes)
                    {
                        throw ImageErrors.ExceedsLimits("The WebP file's ICC profile is more than the profile size limit.");
                    }

                    byte[] profile = new byte[chunkSize];
                    await reader.ReadExactlyAsync(profile, cancellationToken).ConfigureAwait(false);
                    read = (int)chunkSize;
                    string? problem = ColorSpace.CheckIccProfile(profile);
                    colorSpace = problem is null ? ColorSpace.FromCheckedIccProfile(profile) : throw ImageErrors.Corrupt(problem);
                }
                else if (hasExif && !exifFound && fourCc == "EXIF")
                {
                    exifFound = true;
                    (orientation, read) = await ExifOrientation.ReadAsync(
                        reader, ReadOnlyMemory<byte>.Empty, chunkSize, cancellationToken).ConfigureAwait(false);
                }
                else if (countFrames && fourCc == "ANMF")
                {
                    frames++;
                    if (frames > options.Limits.MaxFrames)
                    {
                        throw ImageErrors.ExceedsLimits(Identifier.FramesOverLimit(options.Limits));
                    }
                }

                await SkipChunkRestAsync(reader, end, chunkSize, read, cancellationToken).ConfigureAwait(false);

                if ((exifFound || !hasExif) && !countFrames && !readIcc)
                {
                    break;
                }
            }
        }

        return new ImageInfo(ImageFormat.Webp, width, height)
        {
            Orientation = orientation,
            HasAlpha = (flags & AlphaFlag) != 0,
            HasIccProfile = (flags & IccFlag) != 0,
            HasExif = hasExif,
            IsAnimated = animated,
            FrameCount = animated ? (countFrames ? frames : null) : 1,
            ColorSpace = colorSpace,
        };
    }

    private static async ValueTask<(string FourCc, uint Size)> ReadChunkHeaderAsync(
        HeaderReader reader,
        long end,
        byte[] buffer,
        bool first,
        CancellationToken cancellationToken)
    {
        if (end - reader.Position < ChunkHeaderLength)
        {
            throw ImageErrors.Corrupt(first ? "The WebP file has no image chunk." : "The WebP file has a partial chunk header.");
        }

        await reader.ReadExactlyAsync(buffer.AsMemory(0, ChunkHeaderLength), cancellationToken).ConfigureAwait(false);
        string fourCc = string.Create(4, buffer, static (span, source) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                // Only the known tags matter; anything else just needs to compare unequal.
                span[i] = (char)source[i];
            }
        });
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4));
        if (reader.Position + size > end)
        {
            throw ImageErrors.Corrupt("The WebP file has a chunk that extends past the end of the file.");
        }

        return (fourCc, size);
    }

    /// <summary>Skips what is left of a chunk after <paramref name="consumed"/> data bytes, with its padding byte.</summary>
    private static ValueTask SkipChunkRestAsync(HeaderReader reader, long end, uint size, int consumed, CancellationToken cancellationToken)
    {
        long skip = size - consumed + (size & 1);

        // Some writers leave off the padding byte of the last chunk.
        skip = Math.Min(skip, end - reader.Position);
        return reader.SkipAsync(skip, cancellationToken);
    }

    private static int ReadUInt24(ReadOnlySpan<byte> data) => data[0] | (data[1] << 8) | (data[2] << 16);

    // A simple file (VP8 or VP8L alone) has no place for a profile, so it is sRGB.
    private static ImageInfo Still(int width, int height, bool hasAlpha, IdentifyOptions options) =>
        new(ImageFormat.Webp, width, height)
        {
            HasAlpha = hasAlpha,
            FrameCount = 1,
            ColorSpace = options.ReadColorSpace ? ColorSpace.Srgb : null,
        };
}
