using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using Xunit;
using static Zorg.Imaging.Tests.Codecs.Png.PngTestFiles;

namespace Zorg.Imaging.Tests.Codecs.Png;

/// <summary>PNG's colour chunks: read into <see cref="ColorSpace"/>, and written back so the colours don't shift.</summary>
public class PngColourTests
{
    private static readonly int[] Samples = RandomSamples(6, 4, 8, 2, seed: 3);

    [Fact]
    public void An_untagged_file_and_an_sRGB_chunk_are_both_sRGB()
    {
        using Image plain = Image.Decode(Rgb());
        using Image tagged = Image.Decode(Rgb(("sRGB", [0])));

        Assert.Same(ColorSpace.Srgb, plain.ColorSpace);
        Assert.Same(ColorSpace.Srgb, tagged.ColorSpace);
    }

    [Theory]
    [InlineData(12, 13, "Display P3")]
    [InlineData(9, 16, "Rec. 2100 PQ")]
    [InlineData(9, 18, "Rec. 2100 HLG")]
    [InlineData(9, 1, "Rec. 2020")]
    [InlineData(1, 8, "Linear sRGB")]
    [InlineData(1, 13, "sRGB")]
    [InlineData(9, 14, "CICP 9/14")]
    public void A_cICP_chunk_gives_its_colour_space_and_is_written_back(int primaries, int transfer, string name)
    {
        byte[] cicp = [(byte)primaries, (byte)transfer, 0, 1];

        using Image image = Image.Decode(Rgb(("cICP", cicp)));
        byte[] saved = PngEncoderTests.Save(image);
        using Image again = Image.Decode(saved);

        Assert.Equal(name, image.ColorSpace.Name);
        Assert.Equal((primaries, transfer), (image.ColorSpace.CicpPrimaries, image.ColorSpace.CicpTransfer));
        Assert.Equal(image.ColorSpace, again.ColorSpace);
        Assert.Equal(Pixels(image), Pixels(again));
        if (name == "sRGB")
        {
            Assert.DoesNotContain("cICP", Chunks(saved).Select(c => c.Type));
        }
        else
        {
            Assert.Equal(cicp, ChunkData(saved, "cICP"));
        }
    }

    [Fact]
    public void A_narrow_range_cICP_is_kept_exactly()
    {
        byte[] cicp = [9, 16, 0, 0];

        using Image image = Image.Decode(Rgb(("cICP", cicp)));
        byte[] saved = PngEncoderTests.Save(image);

        Assert.Contains("narrow range", image.ColorSpace.Name, StringComparison.Ordinal);
        Assert.NotEqual(ColorSpace.Rec2020Pq, image.ColorSpace);
        Assert.True(image.ColorSpace.IsHdr);
        Assert.Equal(cicp, ChunkData(saved, "cICP"));
    }

    [Fact]
    public void An_ICC_profile_is_kept_byte_for_byte()
    {
        byte[] profile = FakeProfile(700, seed: 1);

        using Image image = Image.Decode(Rgb(("iCCP", Iccp("Display P3", profile))));
        byte[] saved = PngEncoderTests.Save(image);
        using Image again = Image.Decode(saved);

        Assert.Equal(profile, image.ColorSpace.IccProfile.ToArray());
        Assert.Null(image.ColorSpace.CicpPrimaries);
        Assert.False(image.ColorSpace.IsSrgb);
        Assert.Equal(image.ColorSpace, again.ColorSpace);
        Assert.Equal(profile, again.ColorSpace.IccProfile.ToArray());
    }

    [Fact]
    public void A_cICP_wins_over_iCCP_and_both_are_written_back()
    {
        byte[] profile = FakeProfile(300, seed: 2);

        using Image image = Image.Decode(Rgb(("iCCP", Iccp("p", profile)), ("cICP", [12, 13, 0, 1]), ("sRGB", [0])));
        byte[] saved = PngEncoderTests.Save(image);
        using Image again = Image.Decode(saved);

        Assert.Equal((12, 13), (image.ColorSpace.CicpPrimaries, image.ColorSpace.CicpTransfer));
        Assert.Equal(profile, image.ColorSpace.IccProfile.ToArray());
        Assert.Equal(image.ColorSpace, again.ColorSpace);
        Assert.Equal(["IHDR", "cICP", "iCCP", "IDAT", "IEND"], Chunks(saved).Select(c => c.Type));
    }

    [Fact]
    public void An_iCCP_wins_over_sRGB_and_sRGB_over_gAMA()
    {
        byte[] profile = FakeProfile(200, seed: 3);

        using Image icc = Image.Decode(Rgb(("sRGB", [0]), ("iCCP", Iccp("p", profile))));
        using Image srgb = Image.Decode(Rgb(("gAMA", Gamma(100_000)), ("sRGB", [0])));

        Assert.Equal(profile, icc.ColorSpace.IccProfile.ToArray());
        Assert.Same(ColorSpace.Srgb, srgb.ColorSpace);
    }

    [Theory]
    [InlineData(45455)]
    [InlineData(45000)]
    [InlineData(45500)]
    public void Gamma_one_over_2_2_with_sRGB_primaries_or_none_is_sRGB(int gamma)
    {
        int[] srgbPrimaries = [31270, 32900, 64000, 33000, 30000, 60000, 15000, 6000];

        using Image alone = Image.Decode(Rgb(("gAMA", Gamma(gamma))));
        using Image withPrimaries = Image.Decode(Rgb(("gAMA", Gamma(gamma)), ("cHRM", Chromaticities(srgbPrimaries))));

        Assert.Same(ColorSpace.Srgb, alone.ColorSpace);
        Assert.Same(ColorSpace.Srgb, withPrimaries.ColorSpace);
    }

    [Fact]
    public void Other_gamma_and_chromaticities_are_written_back_as_they_were()
    {
        int[] p3 = [31270, 32900, 68000, 32000, 26500, 69000, 15000, 6000];
        byte[] gama = Gamma(100_000);
        byte[] chrm = Chromaticities(p3);

        using Image image = Image.Decode(Rgb(("gAMA", gama), ("cHRM", chrm)));
        byte[] saved = PngEncoderTests.Save(image);
        using Image again = Image.Decode(saved);

        Assert.Equal("PNG gamma 1", image.ColorSpace.Name);
        Assert.False(image.ColorSpace.IsSrgb);
        Assert.Equal(gama, ChunkData(saved, "gAMA"));
        Assert.Equal(chrm, ChunkData(saved, "cHRM"));
        Assert.Equal(image.ColorSpace, again.ColorSpace);
    }

    [Fact]
    public void Colour_chunks_after_the_image_data_are_ignored()
    {
        byte[] ihdr = Ihdr(6, 4, 8, 2);
        byte[] png = Assemble(ihdr, [], [Compress(ImageData(6, 4, 8, 2, Samples, false))], ("cICP", [12, 13, 0, 1]));

        using Image image = Image.Decode(png);

        Assert.Same(ColorSpace.Srgb, image.ColorSpace);
    }

    [Fact]
    public void A_second_chunk_of_a_kind_is_ignored()
    {
        using Image image = Image.Decode(Rgb(("cICP", [12, 13, 0, 1]), ("cICP", [9, 16, 0, 1])));

        Assert.Same(ColorSpace.DisplayP3, image.ColorSpace);
    }

    [Fact]
    public void An_ICC_profile_that_inflates_past_the_limit_is_refused_without_inflating_it_all()
    {
        // 64 MiB of zeros compresses to about 64 KiB.
        byte[] huge = new byte[64 * 1024 * 1024];
        "acsp"u8.CopyTo(huge.AsSpan(36));
        byte[] png = Rgb(("iCCP", Iccp("bomb", huge)));

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ImageLimitException>(() => Image.Decode(png));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 32 * 1024 * 1024, $"{allocated} bytes allocated");
    }

    [Fact]
    public void Broken_colour_chunks_are_refused()
    {
        byte[] profile = FakeProfile(200, seed: 4);
        byte[] notAProfile = new byte[200];
        byte[] damaged = Iccp("p", profile);
        damaged[^3] ^= 0xFF;

        (string Type, byte[] Data)[] cases =
        [
            ("cICP", [12, 13, 0]),
            ("sRGB", []),
            ("gAMA", [0, 0, 0, 0]),
            ("gAMA", [0, 1]),
            ("cHRM", new byte[31]),
            ("iCCP", [.. Encoding.ASCII.GetBytes("no terminator")]),
            ("iCCP", [0, 0, .. Compress(profile)]),
            ("iCCP", [(byte)'p', 0, 1, .. Compress(profile)]),
            ("iCCP", Iccp("p", notAProfile)),
            ("iCCP", damaged),
        ];
        foreach ((string type, byte[] data) in cases)
        {
            Exception? error = Record.Exception(() => Image.Decode(Rgb((type, data))));
            Assert.True(error is ImageFormatException, $"{type} {Convert.ToHexString(data.AsSpan(0, Math.Min(8, data.Length)))}: {error}");
        }
    }

    [Fact]
    public void Sixteen_bit_HDR_round_trips_exactly()
    {
        int[] samples = RandomSamples(9, 7, 16, 2, seed: 8);
        byte[] png = Build(9, 7, 16, 2, samples, before: ("cICP", [9, 16, 0, 1]));

        using Image image = Image.Decode(png);
        using Image again = Image.Decode(PngEncoderTests.Save(image));

        Assert.Equal(PixelFormat.Rgb48, image.PixelFormat);
        Assert.Same(ColorSpace.Rec2020Pq, image.ColorSpace);
        Assert.True(again.ColorSpace.IsHdr);
        Assert.Equal(PixelFormat.Rgb48, again.PixelFormat);
        Assert.Equal(Pixels(image), Pixels(again));
    }

    [Fact]
    public void Orientation_and_Clone_keep_the_colour_space()
    {
        byte[] png = Rgb(("cICP", [12, 13, 0, 1]), ("eXIf", ExifWithOrientation(6)));

        using Image image = Image.Decode(png);
        using Image copy = image.Clone();

        Assert.Equal((4, 6), (image.Width, image.Height));
        Assert.Same(ColorSpace.DisplayP3, image.ColorSpace);
        Assert.Same(ColorSpace.DisplayP3, copy.ColorSpace);
    }

    /// <summary>Bytes that pass as an ICC profile: a size field, the <c>acsp</c> signature at 36, and noise.</summary>
    internal static byte[] FakeProfile(int length, int seed)
    {
        byte[] profile = new byte[length];
        new Random(seed).NextBytes(profile);
        BinaryPrimitives.WriteUInt32BigEndian(profile, (uint)length);
        "acsp"u8.CopyTo(profile.AsSpan(36));
        return profile;
    }

    internal static byte[] Iccp(string name, byte[] profile) => [.. Encoding.Latin1.GetBytes(name), 0, 0, .. Compress(profile)];

    private static byte[] Rgb(params (string Type, byte[] Data)[] before) => Build(6, 4, 8, 2, Samples, before: before);

    private static byte[] Gamma(int value)
    {
        byte[] data = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)value);
        return data;
    }

    private static byte[] Chromaticities(int[] values)
    {
        byte[] data = new byte[32];
        for (int i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(i * 4), (uint)values[i]);
        }

        return data;
    }

    private static byte[] ChunkData(byte[] png, string type)
    {
        int offset = Chunks(png).Single(c => c.Type == type).Offset;
        int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
        return png.AsSpan(offset + 8, length).ToArray();
    }
}
