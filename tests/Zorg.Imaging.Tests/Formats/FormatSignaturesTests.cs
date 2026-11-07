using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Tests.Formats;

public class FormatSignaturesTests
{
    public static TheoryData<string> SignatureNames { get; } = [.. SignatureSamples.Signatures.Keys];

    public static TheoryData<string> NonImageNames { get; } = [.. SignatureSamples.NonImages.Keys];

    [Theory]
    [MemberData(nameof(SignatureNames))]
    public void Every_signature_is_detected(string name)
    {
        (ImageFormat expected, byte[] bytes) = SignatureSamples.Signatures[name];
        Assert.Same(expected, FormatSignatures.Detect(bytes));
    }

    [Theory]
    [MemberData(nameof(SignatureNames))]
    public void Signature_followed_by_more_bytes_is_still_detected(string name)
    {
        (ImageFormat expected, byte[] bytes) = SignatureSamples.Signatures[name];
        byte[] longer = new byte[200];
        bytes.CopyTo(longer, 0);
        Assert.Same(expected, FormatSignatures.Detect(longer));
    }

    [Theory]
    [MemberData(nameof(SignatureNames))]
    public void Signature_cut_one_byte_short_is_not_detected(string name)
    {
        (_, byte[] bytes) = SignatureSamples.Signatures[name];
        Assert.Null(FormatSignatures.Detect(bytes.AsSpan(0, bytes.Length - 1)));
    }

    [Theory]
    [MemberData(nameof(NonImageNames))]
    public void Non_images_are_not_detected(string name) => Assert.Null(FormatSignatures.Detect(SignatureSamples.NonImages[name]));

    [Fact]
    public void Bytes_beyond_the_signature_length_are_ignored()
    {
        // A brand that only appears past byte 64 must not count, even though the box claims to be longer.
        byte[] bytes = new byte[128];
        SignatureSamples.Ftyp(128, "isom", "isom").CopyTo(bytes, 0);
        "avif"u8.CopyTo(bytes.AsSpan(100));
        Assert.Null(FormatSignatures.Detect(bytes));
    }

    [Fact]
    public void Brand_inside_the_first_64_bytes_of_a_longer_buffer_is_found()
    {
        byte[] bytes = new byte[128];
        SignatureSamples.Ftyp(128, "isom", "mp42", "avif").CopyTo(bytes, 0);
        Assert.Same(ImageFormat.Avif, FormatSignatures.Detect(bytes));
    }

    [Theory]
    [InlineData("avif")]
    [InlineData("avis")]
    public void Avif_major_brands(string major) => Assert.Same(ImageFormat.Avif, FormatSignatures.Detect(SignatureSamples.Ftyp(16, major)));

    [Theory]
    [InlineData("heic")]
    [InlineData("heix")]
    [InlineData("hevc")]
    [InlineData("hevx")]
    [InlineData("heim")]
    [InlineData("heis")]
    public void Heif_major_brands(string major) => Assert.Same(ImageFormat.Heif, FormatSignatures.Detect(SignatureSamples.Ftyp(16, major)));

    [Theory]
    [InlineData("mif1")]
    [InlineData("msf1")]
    public void Generic_heif_major_brand_is_heif(string major) => Assert.Same(ImageFormat.Heif, FormatSignatures.Detect(SignatureSamples.Ftyp(16, major)));

    [Fact]
    public void Compatible_avif_brand_wins_over_a_generic_major_brand() =>
        Assert.Same(ImageFormat.Avif, FormatSignatures.Detect(SignatureSamples.Ftyp(20, "mif1", "avif")));

    [Fact]
    public void Compatible_avif_brand_wins_over_an_earlier_heic_brand() =>
        Assert.Same(ImageFormat.Avif, FormatSignatures.Detect(SignatureSamples.Ftyp(28, "isom", "heic", "avis")));

    [Fact]
    public void Compatible_heic_brand_is_heif() =>
        Assert.Same(ImageFormat.Heif, FormatSignatures.Detect(SignatureSamples.Ftyp(24, "isom", "mp42", "heic")));

    [Fact]
    public void Compatible_heic_brand_wins_over_mif1() =>
        Assert.Same(ImageFormat.Heif, FormatSignatures.Detect(SignatureSamples.Ftyp(24, "mif1", "heic")));

    [Fact]
    public void Compatible_mif1_brand_is_heif() =>
        Assert.Same(ImageFormat.Heif, FormatSignatures.Detect(SignatureSamples.Ftyp(20, "isom", "mif1")));

    [Fact]
    public void Compatible_msf1_brand_is_heif() =>
        Assert.Same(ImageFormat.Heif, FormatSignatures.Detect(SignatureSamples.Ftyp(20, "isom", "msf1")));

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(8u)]
    [InlineData(15u)]
    public void Ftyp_box_smaller_than_16_bytes_is_not_detected(uint size) =>
        Assert.Null(FormatSignatures.Detect(SignatureSamples.Ftyp(size, "avif", "avif")));

    [Fact]
    public void Brands_after_the_end_of_the_box_are_ignored() =>
        Assert.Null(FormatSignatures.Detect(SignatureSamples.Ftyp(16, "isom", "avif", "heic")));

    [Fact]
    public void Brands_after_the_end_of_a_box_that_stops_mid_brand_are_ignored() =>
        Assert.Null(FormatSignatures.Detect(SignatureSamples.Ftyp(22, "isom", "mp42", "avif")));

    [Fact]
    public void Box_claiming_more_bytes_than_given_reads_only_what_is_there() =>
        Assert.Same(ImageFormat.Avif, FormatSignatures.Detect(SignatureSamples.Ftyp(4000, "isom", "avif")));

    [Fact]
    public void Box_claiming_more_bytes_than_given_without_a_known_brand_is_not_detected() =>
        Assert.Null(FormatSignatures.Detect(SignatureSamples.Ftyp(uint.MaxValue, "isom", "mp42")));

    [Theory]
    [InlineData(12u)]
    [InlineData(40u)]
    [InlineData(52u)]
    [InlineData(56u)]
    [InlineData(64u)]
    [InlineData(108u)]
    [InlineData(124u)]
    public void Bmp_with_each_known_header_size_is_detected(uint headerSize) =>
        Assert.Same(ImageFormat.Bmp, FormatSignatures.Detect(SignatureSamples.Bmp(headerSize)));

    [Theory]
    [InlineData(0u)]
    [InlineData(13u)]
    [InlineData(41u)]
    [InlineData(0x20202020u)]
    public void Bmp_with_an_unknown_header_size_is_not_detected(uint headerSize) =>
        Assert.Null(FormatSignatures.Detect(SignatureSamples.Bmp(headerSize)));

    [Fact]
    public void Ico_with_zero_images_is_not_detected() =>
        Assert.Null(FormatSignatures.Detect(new byte[] { 0, 0, 1, 0, 0, 0 }));

    [Fact]
    public void Cur_files_are_not_detected_as_ico() =>
        Assert.Null(FormatSignatures.Detect(new byte[] { 0, 0, 2, 0, 1, 0 }));

    [Fact]
    public void Empty_input_is_not_detected() => Assert.Null(FormatSignatures.Detect([]));

    [Fact]
    public void Every_possible_single_byte_is_not_detected()
    {
        Span<byte> one = stackalloc byte[1];
        for (int value = 0; value <= byte.MaxValue; value++)
        {
            one[0] = (byte)value;
            Assert.Null(FormatSignatures.Detect(one));
        }
    }

    [Fact]
    public void Every_prefix_of_every_signature_never_throws()
    {
        foreach ((_, byte[] bytes) in SignatureSamples.Signatures.Values)
        {
            for (int length = 0; length <= bytes.Length; length++)
            {
                _ = FormatSignatures.Detect(bytes.AsSpan(0, length));
            }
        }
    }

    [Fact]
    public void Detect_over_every_signature_allocates_nothing()
    {
        byte[][] samples = [.. SignatureSamples.Signatures.Values.Select(v => v.Bytes), .. SignatureSamples.NonImages.Values];
        int detected = 0;
        foreach (byte[] sample in samples)
        {
            detected += FormatSignatures.Detect(sample) is null ? 0 : 1; // warm-up
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 0; round < 100; round++)
        {
            foreach (byte[] sample in samples)
            {
                detected += FormatSignatures.Detect(sample) is null ? 0 : 1;
            }
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
        Assert.True(detected > 0);
    }
}

/// <summary>Byte sequences for the format-check tests, built in code.</summary>
internal static class SignatureSamples
{
    public static IReadOnlyDictionary<string, (ImageFormat Format, byte[] Bytes)> Signatures { get; } =
        new Dictionary<string, (ImageFormat, byte[])>
        {
            ["png"] = (ImageFormat.Png, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            ["jpeg"] = (ImageFormat.Jpeg, [0xFF, 0xD8, 0xFF]),
            ["gif87a"] = (ImageFormat.Gif, "GIF87a"u8.ToArray()),
            ["gif89a"] = (ImageFormat.Gif, "GIF89a"u8.ToArray()),
            ["webp"] = (ImageFormat.Webp, [.. "RIFF"u8, 0x24, 0, 0, 0, .. "WEBP"u8]),
            ["avif"] = (ImageFormat.Avif, Ftyp(16, "avif")),
            ["avis"] = (ImageFormat.Avif, Ftyp(16, "avis")),
            ["heic"] = (ImageFormat.Heif, Ftyp(16, "heic")),
            ["mif1"] = (ImageFormat.Heif, Ftyp(16, "mif1")),
            ["jxl-codestream"] = (ImageFormat.JpegXl, [0xFF, 0x0A]),
            ["jxl-container"] = (ImageFormat.JpegXl, [0, 0, 0, 0x0C, 0x4A, 0x58, 0x4C, 0x20, 0x0D, 0x0A, 0x87, 0x0A]),
            ["tiff-le"] = (ImageFormat.Tiff, [0x49, 0x49, 0x2A, 0x00]),
            ["tiff-be"] = (ImageFormat.Tiff, [0x4D, 0x4D, 0x00, 0x2A]),
            ["bmp"] = (ImageFormat.Bmp, Bmp(40)),
            ["ico"] = (ImageFormat.Ico, [0, 0, 1, 0, 1, 0]),
            ["psd"] = (ImageFormat.Psd, "8BPS"u8.ToArray()),
            ["qoi"] = (ImageFormat.Qoi, "qoif"u8.ToArray()),
        };

    public static IReadOnlyDictionary<string, byte[]> NonImages { get; } = new Dictionary<string, byte[]>
    {
        ["empty"] = [],
        ["one-byte"] = [0x89],
        ["pdf"] = Encoding.ASCII.GetBytes("%PDF-1.7\n%âãÏÓ\n"),
        ["zip"] = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00, 0x08, 0x00],
        ["text"] = Encoding.ASCII.GetBytes("Hello, this is plain text, not an image at all."),
        ["html"] = Encoding.ASCII.GetBytes("<!DOCTYPE html><html><body>hi</body></html>"),
        ["svg"] = Encoding.ASCII.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1\" height=\"1\"/>"),
        ["svg-xml"] = Encoding.ASCII.GetBytes("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"/>"),
        ["bm-text"] = Encoding.ASCII.GetBytes("BM is a text file that happens to start with two letters."),
        ["riff-wave"] = [.. "RIFF"u8, 0x24, 0, 0, 0, .. "WAVEfmt "u8, 0x10, 0, 0, 0, 1, 0, 1, 0],
        ["mp4"] = Ftyp(24, "isom", "isom", "mp42"),
        ["mov"] = Ftyp(20, "qt  "),
        ["zeros"] = new byte[64],
        ["ones"] = Enumerable.Repeat((byte)0xFF, 64).ToArray(),
    };

    /// <summary>Builds an ISO-BMFF <c>ftyp</c> box head with the given claimed size.</summary>
    /// <param name="size">The box size written at offset 0.</param>
    /// <param name="major">The major brand.</param>
    /// <param name="compatible">The compatible brands.</param>
    /// <returns>The box bytes, as many as the brands fill (at least 16).</returns>
    public static byte[] Ftyp(uint size, string major, params string[] compatible)
    {
        byte[] bytes = new byte[16 + (compatible.Length * 4)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, size);
        "ftyp"u8.CopyTo(bytes.AsSpan(4));
        Encoding.ASCII.GetBytes(major).CopyTo(bytes, 8);
        for (int i = 0; i < compatible.Length; i++)
        {
            Encoding.ASCII.GetBytes(compatible[i]).CopyTo(bytes, 16 + (i * 4));
        }

        return bytes;
    }

    /// <summary>Builds the first 18 bytes of a BMP with the given info-header size.</summary>
    /// <param name="headerSize">The value at offset 14.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Bmp(uint headerSize)
    {
        byte[] bytes = new byte[18];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), headerSize);
        return bytes;
    }
}
