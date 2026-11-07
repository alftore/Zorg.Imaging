using System;
using System.Buffers.Binary;

namespace Zorg.Imaging.Formats;

/// <summary>Level 1 of the format check: magic bytes.</summary>
internal static class FormatSignatures
{
    /// <summary>The PNG signature.</summary>
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The JPEG XL container signature box.</summary>
    private static ReadOnlySpan<byte> JpegXlContainer => [0x00, 0x00, 0x00, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A];

    /// <summary>Recognises the format from the first bytes. Allocates nothing and never throws.</summary>
    /// <param name="head">The first bytes, up to <see cref="ImageFormats.MaxSignatureLength"/>.</param>
    /// <returns>The format, or <see langword="null"/> when the bytes match no signature or there are too few of them.</returns>
    public static ImageFormat? Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length > ImageFormats.MaxSignatureLength)
        {
            head = head[..ImageFormats.MaxSignatureLength];
        }

        if (head.Length < 2)
        {
            return null;
        }

        if (head.StartsWith(PngSignature))
        {
            return ImageFormat.Png;
        }

        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return ImageFormat.Jpeg;
        }

        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8))
        {
            return ImageFormat.Gif;
        }

        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return ImageFormat.Webp;
        }

        if (head.Length >= 16 && head.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return DetectIsoBmff(head);
        }

        if ((head[0] == 0xFF && head[1] == 0x0A) || head.StartsWith(JpegXlContainer))
        {
            return ImageFormat.JpegXl;
        }

        if (head.StartsWith("II*\0"u8) || head.StartsWith("MM\0*"u8))
        {
            return ImageFormat.Tiff;
        }

        if (head.Length >= 18 && head[0] == (byte)'B' && head[1] == (byte)'M' && IsBmpHeaderSize(BinaryPrimitives.ReadUInt32LittleEndian(head[14..])))
        {
            return ImageFormat.Bmp;
        }

        if (head.Length >= 6 && head[0] == 0 && head[1] == 0 && head[2] == 1 && head[3] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(head[4..]) != 0)
        {
            return ImageFormat.Ico;
        }

        if (head.StartsWith("8BPS"u8))
        {
            return ImageFormat.Psd;
        }

        if (head.StartsWith("qoif"u8))
        {
            return ImageFormat.Qoi;
        }

        return null;
    }

    /// <summary>Whether a BMP info header size is one of the known header versions.</summary>
    private static bool IsBmpHeaderSize(uint size) => size is 12 or 40 or 52 or 56 or 64 or 108 or 124;

    /// <summary>Reads the brands of an ISO-BMFF <c>ftyp</c> box and decides between AVIF and HEIF.</summary>
    private static ImageFormat? DetectIsoBmff(ReadOnlySpan<byte> head)
    {
        uint size = BinaryPrimitives.ReadUInt32BigEndian(head);
        if (size < 16)
        {
            return null;
        }

        int end = (int)Math.Min(size, (uint)head.Length);
        ReadOnlySpan<byte> major = head.Slice(8, 4);
        if (IsAvifBrand(major))
        {
            return ImageFormat.Avif;
        }

        if (IsHeicBrand(major))
        {
            return ImageFormat.Heif;
        }

        bool heic = false;
        bool mif = IsMifBrand(major);
        for (int offset = 16; offset + 4 <= end; offset += 4)
        {
            ReadOnlySpan<byte> brand = head.Slice(offset, 4);
            if (IsAvifBrand(brand))
            {
                return ImageFormat.Avif;
            }

            heic |= IsHeicBrand(brand);
            mif |= IsMifBrand(brand);
        }

        return heic || mif ? ImageFormat.Heif : null;
    }

    /// <summary>Whether the brand is <c>avif</c> or <c>avis</c>.</summary>
    private static bool IsAvifBrand(ReadOnlySpan<byte> brand) => brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8);

    /// <summary>Whether the brand is one of the HEVC-based HEIF brands.</summary>
    private static bool IsHeicBrand(ReadOnlySpan<byte> brand) =>
        brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("hevc"u8)
        || brand.SequenceEqual("hevx"u8) || brand.SequenceEqual("heim"u8) || brand.SequenceEqual("heis"u8);

    /// <summary>Whether the brand is the generic HEIF structural brand <c>mif1</c> or <c>msf1</c>.</summary>
    private static bool IsMifBrand(ReadOnlySpan<byte> brand) => brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8);
}
