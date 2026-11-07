using System;
using System.Buffers.Binary;

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

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(data) : BinaryPrimitives.ReadUInt16BigEndian(data);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(data) : BinaryPrimitives.ReadUInt32BigEndian(data);
}
