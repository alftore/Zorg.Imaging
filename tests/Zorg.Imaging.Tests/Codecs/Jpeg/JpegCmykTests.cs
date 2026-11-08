using System;
using System.Collections.Generic;
using Xunit;
using Zorg.Imaging.Codecs.Jpeg;
using Zorg.Imaging.Tests.Metadata;
using static Zorg.Imaging.Tests.Codecs.Jpeg.JpegTestFiles;

namespace Zorg.Imaging.Tests.Codecs.Jpeg;

public class JpegCmykTests
{
    [Theory]
    [InlineData("ycck")]
    [InlineData("ycck-subsampled")]
    public void Ycck_files_decode_as_ImageMagick_converts_them(string name)
    {
        using Image image = Image.Decode(Sample(name));
        Assert.Equal(PixelFormat.Rgb24, image.PixelFormat);
        Assert.True(image.ColorSpace.IsSrgb);
        (int width, int height, _, byte[] expected) = Pnm(name);
        Assert.Equal((width, height), (image.Width, image.Height));
        (int max, double mean) = Compare(image.GetPixelBytes(), expected);
        Assert.True(max <= 3 && mean < 0.6, $"largest {max}, mean {mean:0.###}");
    }

    [Fact]
    public void Requested_formats_and_shrink_on_load_work_for_cmyk()
    {
        byte[] jpeg = Sample("ycck-subsampled");
        using Image rgb = Image.Decode(jpeg);
        using Image rgba = Image.Decode(jpeg, new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        Rgb24 p = rgb.GetRow<Rgb24>(10)[20];
        Assert.Equal(new Rgba32(p.R, p.G, p.B, 255), rgba.GetRow<Rgba32>(10)[20]);

        using Image half = Image.Decode(jpeg, DecodeOptions.Default.WithShrinkTarget((w, h) => (w / 4, h / 4)));
        Assert.Equal((34, 23), (half.Width, half.Height));
    }

    [Fact]
    public void The_three_storages_give_the_inks()
    {
        JpegCmyk plain = JpegCmyk.For(-1, default);
        JpegCmyk inverted = JpegCmyk.For(0, default);
        JpegCmyk ycck = JpegCmyk.For(2, default);
        Assert.Equal((10, 20, 30, 40), plain.Inks(10, 20, 30, 40));
        Assert.Equal((245, 235, 225, 215), inverted.Inks(10, 20, 30, 40));

        // YCCK: grey Y with neutral chroma gives equal inks of Y; K is inverted.
        Assert.Equal((100, 100, 100, 255), ycck.Inks(100, 128, 128, 0));
        Assert.Equal((0, 0, 0, 0), ycck.Inks(0, 128, 128, 255));
        Assert.False(plain.HasProfile);
    }

    [Fact]
    public void The_device_formula_is_paper_times_ink()
    {
        Assert.Equal((255, 255, 255), JpegCmyk.Device(0, 0, 0, 0));
        Assert.Equal((0, 0, 0), JpegCmyk.Device(0, 0, 0, 255));
        Assert.Equal((0, 255, 255), JpegCmyk.Device(255, 0, 0, 0));
        Assert.Equal((64, 128, 191), JpegCmyk.Device(191, 127, 64, 0));
        Assert.Equal((96, 96, 96), JpegCmyk.Device(80, 80, 80, 115));
    }

    [Fact]
    public void An_embedded_cmyk_profile_does_the_conversion()
    {
        // A profile that encodes the device formula itself: the result is close to the profile-less decode.
        byte[] profile = IccTestProfiles.NaiveCmyk(IccTestProfiles.TagType.Lut16, points: 17);
        byte[] plain = Sample("ycck");
        byte[] profiled = WithIcc(plain, profile);
        using Image withProfile = Image.Decode(profiled);
        using Image without = Image.Decode(plain);
        Assert.True(withProfile.ColorSpace.IsSrgb);
        (int max, double mean) = Compare(withProfile.GetPixelBytes(), without.GetPixelBytes());
        Assert.True(max <= 6 && mean < 1.5, $"largest {max}, mean {mean:0.###}");
        Assert.True(JpegCmyk.For(2, profile).HasProfile);

        // An RGB profile on a CMYK file says nothing about its inks: the device formula is used.
        Assert.False(JpegCmyk.For(2, IccTestProfiles.SrgbLut(IccTestProfiles.TagType.Lut16, lab: false, points: 2)).HasProfile);
    }

    [Fact]
    public void Identify_reads_a_cmyk_file()
    {
        IdentifyResult result = Image.Identify(Sample("ycck"));
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal((67, 45), (result.Info.Width, result.Info.Height));
    }

    [Fact]
    public async System.Threading.Tasks.Task A_profiled_cmyk_file_converts_as_LittleCMS_does()
    {
        string magick = Oracle.Require("magick");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        byte[] jpeg = WithIcc(Sample("ycck"), IccTestProfiles.NaiveCmyk(IccTestProfiles.TagType.AToB, points: 9));
        await System.IO.File.WriteAllBytesAsync(folder.File("in.jpg"), jpeg, TestContext.Current.CancellationToken);
        byte[] srgb = Zorg.Imaging.Metadata.IccProfileWriter.Write("sRGB", [0.3127, 0.3290, 0.64, 0.33, 0.30, 0.60, 0.15, 0.06], Zorg.Imaging.Metadata.IccProfileWriter.Curve.Srgb);
        await System.IO.File.WriteAllBytesAsync(folder.File("srgb.icc"), srgb, TestContext.Current.CancellationToken);
        (int code, byte[] output, string errors) = await Oracle.RunAsync(magick, folder.File("in.jpg"), "-intent", "Perceptual", "-profile", folder.File("srgb.icc"), "-depth", "8", "rgb:-");
        Assert.True(code == 0, errors);

        using Image ours = Image.Decode(jpeg);
        (int max, double mean) = Compare(ours.GetPixelBytes(), output);
        Assert.True(max <= 4 && mean < 1.0, $"largest {max}, mean {mean:0.###}");
    }

    // The file with the profile in APP2 chunks of at most 65,519 bytes, after SOI.
    private static byte[] WithIcc(byte[] jpeg, byte[] profile)
    {
        const int Chunk = 65519;
        int count = (profile.Length + Chunk - 1) / Chunk;
        var segments = new List<(int, byte[])>();
        for (int i = 0; i < count; i++)
        {
            byte[] part = profile.AsSpan(i * Chunk, Math.Min(Chunk, profile.Length - (i * Chunk))).ToArray();
            segments.Add((0xE2, [.. "ICC_PROFILE\0"u8, (byte)(i + 1), (byte)count, .. part]));
        }

        return WithSegments(jpeg, [.. segments]);
    }
}
