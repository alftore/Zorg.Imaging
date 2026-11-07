using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Metadata;

/// <summary>Reads only the Orientation tag (0x0112) from an EXIF block (CIPA DC-008), for the header check.</summary>
/// <remarks>
/// <para>
/// The block is the same TIFF structure in JPEG APP1 (after <c>Exif\0\0</c>), PNG <c>eXIf</c> and WebP <c>EXIF</c>.
/// A leading <c>Exif\0\0</c> is accepted everywhere, because some WebP and PNG writers leave it in.
/// </para>
/// <para>Never throws: anything malformed reads as 1, the same as no orientation.</para>
/// </remarks>
internal static class ExifOrientation
{
    /// <summary>The bytes of an EXIF block worth passing to <see cref="Read"/>: enough for IFD0 in any real file.</summary>
    public const int MaxBytesNeeded = 64 * 1024;

    /// <summary>The first read from a block: the TIFF header and IFD0 of a typical camera file.</summary>
    private const int FirstRead = 512;

    private const ushort OrientationTag = 0x0112;
    private const ushort TypeShort = 3;

    /// <summary>Reads the orientation.</summary>
    /// <param name="exif">The EXIF block, starting at the TIFF header or at <c>Exif\0\0</c>. It may be cut short.</param>
    /// <returns>The orientation, 1 to 8; 1 when it's missing or invalid.</returns>
    public static int Read(ReadOnlySpan<byte> exif)
    {
        if (exif.StartsWith("Exif\0\0"u8))
        {
            exif = exif[6..];
        }

        if (exif.Length < 8)
        {
            return 1;
        }

        bool littleEndian;
        if (exif.StartsWith("II*\0"u8))
        {
            littleEndian = true;
        }
        else if (exif.StartsWith("MM\0*"u8))
        {
            littleEndian = false;
        }
        else
        {
            return 1;
        }

        uint ifd = ReadUInt32(exif[4..], littleEndian);
        if (ifd < 8 || ifd > (uint)exif.Length - 2)
        {
            return 1;
        }

        int count = ReadUInt16(exif[(int)ifd..], littleEndian);
        int entries = (int)ifd + 2;
        for (int i = 0; i < count; i++)
        {
            int entry = entries + (i * 12);
            if (entry > exif.Length - 12)
            {
                return 1;
            }

            ReadOnlySpan<byte> field = exif.Slice(entry, 12);
            if (ReadUInt16(field, littleEndian) != OrientationTag)
            {
                continue;
            }

            if (ReadUInt16(field[2..], littleEndian) != TypeShort || ReadUInt32(field[4..], littleEndian) != 1)
            {
                return 1;
            }

            int value = ReadUInt16(field[8..], littleEndian);
            return value is >= 1 and <= 8 ? value : 1;
        }

        return 1;
    }

    /// <summary>
    /// Reads the orientation from an EXIF block in a file, reading only as far as the end of IFD0. Thumbnails, maker notes
    /// and the other IFDs that make a block large come after it, so a camera JPEG with a 60 KB block needs a few hundred
    /// bytes.
    /// </summary>
    /// <param name="reader">The reader, positioned after <paramref name="prefix"/>.</param>
    /// <param name="prefix">Bytes of the block the caller has already read.</param>
    /// <param name="length">The length of the whole block, including <paramref name="prefix"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The orientation, and how many bytes were read after <paramref name="prefix"/>; the caller skips the rest.</returns>
    public static async ValueTask<(int Orientation, int BytesRead)> ReadAsync(
        HeaderReader reader, ReadOnlyMemory<byte> prefix, long length, CancellationToken cancellationToken)
    {
        int limit = (int)Math.Min(length, MaxBytesNeeded);
        if (limit <= prefix.Length)
        {
            return (Read(prefix.Span[..limit]), 0);
        }

        byte[] block = new byte[Math.Min(limit, Math.Max(FirstRead, prefix.Length))];
        prefix.CopyTo(block);
        int filled = prefix.Length;
        while (true)
        {
            await reader.ReadExactlyAsync(block.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            filled = block.Length;

            long needed = Math.Min(BytesNeeded(block), limit);
            if (needed <= block.Length)
            {
                return (Read(block), filled - prefix.Length);
            }

            Array.Resize(ref block, (int)needed);
        }
    }

    /// <summary>How many bytes from the start of the block <see cref="Read"/> needs, judged from the bytes so far.</summary>
    /// <param name="exif">The start of the block.</param>
    /// <returns>A length past the end of <paramref name="exif"/> when more is needed; otherwise at most its length.</returns>
    internal static long BytesNeeded(ReadOnlySpan<byte> exif)
    {
        int start = exif.StartsWith("Exif\0\0"u8) ? 6 : 0;
        ReadOnlySpan<byte> tiff = exif[start..];
        if (tiff.Length < 8)
        {
            return start + 8;
        }

        bool littleEndian = tiff.StartsWith("II*\0"u8);
        if (!littleEndian && !tiff.StartsWith("MM\0*"u8))
        {
            return exif.Length;
        }

        uint ifd = ReadUInt32(tiff[4..], littleEndian);
        if (ifd < 8)
        {
            return exif.Length;
        }

        if (ifd > (uint)tiff.Length - 2)
        {
            return start + (long)ifd + 2;
        }

        int count = ReadUInt16(tiff[(int)ifd..], littleEndian);
        return start + (long)ifd + 2 + (count * 12L);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(data) : BinaryPrimitives.ReadUInt16BigEndian(data);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(data) : BinaryPrimitives.ReadUInt32BigEndian(data);
}
