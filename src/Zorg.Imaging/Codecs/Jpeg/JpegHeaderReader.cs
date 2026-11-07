using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Formats;
using Zorg.Imaging.Metadata;

namespace Zorg.Imaging.Codecs.Jpeg;

/// <summary>
/// Reads the JPEG headers for <see cref="Image.Identify"/>, from ITU-T T.81 (marker syntax, frame header) and JFIF.
/// </summary>
internal static class JpegHeaderReader
{
    private const byte MarkerPrefix = 0xFF;
    private const byte Tem = 0x01;
    private const byte Soi = 0xD8;
    private const byte Eoi = 0xD9;
    private const byte Sos = 0xDA;
    private const byte App1 = 0xE1;
    private const byte App2 = 0xE2;

    private static ReadOnlySpan<byte> ExifPrefix => "Exif\0\0"u8;

    private static ReadOnlySpan<byte> IccPrefix => "ICC_PROFILE\0"u8;

    /// <summary>Reads the headers. The reader is at offset 0, before the signature.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="options">The options.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What the headers say.</returns>
    public static async ValueTask<ImageInfo> ReadAsync(HeaderReader reader, IdentifyOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);

        byte[] scratch = new byte[IccPrefix.Length];
        await reader.ReadExactlyAsync(scratch.AsMemory(0, 2), cancellationToken).ConfigureAwait(false);
        if (scratch[0] != MarkerPrefix || scratch[1] != Soi)
        {
            throw ImageErrors.Corrupt("The JPEG file doesn't start with a start-of-image marker.");
        }

        bool hasExif = false;
        bool hasIcc = false;
        int orientation = 1;

        while (true)
        {
            byte marker = await ReadMarkerAsync(reader, cancellationToken).ConfigureAwait(false);

            if (marker == Tem || marker is >= 0xD0 and <= 0xD7)
            {
                continue;
            }

            if (marker is Soi or Eoi or Sos or 0x00)
            {
                throw ImageErrors.Corrupt("The JPEG file has no frame header.");
            }

            await reader.ReadExactlyAsync(scratch.AsMemory(0, 2), cancellationToken).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadUInt16BigEndian(scratch);
            if (length < 2)
            {
                throw ImageErrors.Corrupt("The JPEG file has a segment with an invalid length.");
            }

            int dataLength = length - 2;

            if (IsFrameMarker(marker))
            {
                if (dataLength < 6)
                {
                    throw ImageErrors.Corrupt("The JPEG frame header is too short.");
                }

                await reader.ReadExactlyAsync(scratch.AsMemory(0, 6), cancellationToken).ConfigureAwait(false);
                int height = BinaryPrimitives.ReadUInt16BigEndian(scratch.AsSpan(1));
                int width = BinaryPrimitives.ReadUInt16BigEndian(scratch.AsSpan(3));
                if (width == 0)
                {
                    throw ImageErrors.Corrupt("The JPEG file has a width of 0.");
                }

                if (height == 0)
                {
                    throw ImageErrors.Corrupt("The JPEG file declares its height after the scan (DNL marker), which isn't supported.");
                }

                return new ImageInfo(ImageFormat.Jpeg, width, height)
                {
                    Orientation = orientation,
                    HasAlpha = false,
                    IsAnimated = false,
                    FrameCount = 1,
                    HasExif = hasExif,
                    HasIccProfile = hasIcc,
                };
            }

            if (marker is App1 or App2)
            {
                int prefixLength = Math.Min(dataLength, scratch.Length);
                await reader.ReadExactlyAsync(scratch.AsMemory(0, prefixLength), cancellationToken).ConfigureAwait(false);
                int rest = dataLength - prefixLength;
                bool isExif = marker == App1 && scratch.AsSpan(0, prefixLength).StartsWith(ExifPrefix);
                bool isIcc = marker == App2 && scratch.AsSpan(0, prefixLength).StartsWith(IccPrefix);

                if (isExif && !hasExif)
                {
                    (orientation, int read) = await ExifOrientation.ReadAsync(
                        reader, scratch.AsMemory(0, prefixLength), dataLength, cancellationToken).ConfigureAwait(false);
                    rest -= read;
                }

                hasExif |= isExif;
                hasIcc |= isIcc;
                await reader.SkipAsync(rest, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await reader.SkipAsync(dataLength, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<byte> ReadMarkerAsync(HeaderReader reader, CancellationToken cancellationToken)
    {
        byte value = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
        if (value != MarkerPrefix)
        {
            throw ImageErrors.Corrupt("The JPEG file has data where a marker was expected.");
        }

        // Any number of 0xFF fill bytes may precede the marker code. Each iteration consumes a byte.
        do
        {
            value = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
        }
        while (value == MarkerPrefix);

        return value;
    }

    private static bool IsFrameMarker(byte marker) =>
        marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
}
