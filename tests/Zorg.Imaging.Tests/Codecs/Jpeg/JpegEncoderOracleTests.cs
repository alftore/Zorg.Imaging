using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Metadata;

namespace Zorg.Imaging.Tests.Codecs.Jpeg;

/// <summary>Checks the files the JPEG encoder writes with libjpeg-turbo, ImageMagick (LittleCMS underneath) and exiftool.</summary>
public class JpegEncoderOracleTests
{
    private static string Text(byte[] output) => Encoding.UTF8.GetString(output).Trim();

    [Theory]
    [InlineData(80, JpegChromaSubsampling.Yuv420, 517, 389, PixelFormat.Rgb24)]
    [InlineData(92, JpegChromaSubsampling.Yuv444, 517, 389, PixelFormat.Rgb24)]
    [InlineData(35, JpegChromaSubsampling.Yuv420, 31, 7, PixelFormat.Rgb24)]
    [InlineData(100, JpegChromaSubsampling.Yuv444, 9, 17, PixelFormat.Rgb24)]
    [InlineData(75, JpegChromaSubsampling.Yuv420, 300, 200, PixelFormat.L8)]
    public async Task Djpeg_decodes_our_files_as_we_do(int quality, JpegChromaSubsampling subsampling, int width, int height, PixelFormat format)
    {
        string djpeg = Oracle.Require("djpeg");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        using Image photo = JpegEncoderTests.Photo(width, height, format);
        byte[] jpeg = JpegEncoderTests.Save(photo, new JpegEncoderOptions { Quality = quality, ChromaSubsampling = subsampling });
        await File.WriteAllBytesAsync(folder.File("ours.jpg"), jpeg, TestContext.Current.CancellationToken);

        (int exit, byte[] pnm, string errors) = await Oracle.RunAsync(djpeg, "-pnm", folder.File("ours.jpg"));
        Assert.True(exit == 0 && errors.Length == 0, errors);
        int header = 0;
        for (int newlines = 0; newlines < 3; header++)
        {
            newlines += pnm[header] == '\n' ? 1 : 0;
        }

        using Image decoded = Image.Decode(jpeg);
        (int max, double mean) = JpegTestFiles.Compare(decoded.GetPixelBytes(), pnm.AsSpan(header));
        Assert.True(max <= 6 && mean < 0.75, $"largest difference {max}, mean {mean:0.###}");
    }

    [Theory]
    [InlineData(50)]
    [InlineData(80)]
    [InlineData(95)]
    public async Task ImageMagick_reads_the_quality_we_asked_for(int quality)
    {
        string magick = Oracle.Require("magick");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        using Image photo = JpegEncoderTests.Photo(64, 48);
        await File.WriteAllBytesAsync(folder.File("ours.jpg"), JpegEncoderTests.Save(photo, new JpegEncoderOptions { Quality = quality }), TestContext.Current.CancellationToken);

        (int exit, byte[] output, string errors) = await Oracle.RunAsync(magick, "identify", "-format", "%Q", folder.File("ours.jpg"));
        Assert.True(exit == 0, errors);
        Assert.Equal(quality.ToString(CultureInfo.InvariantCulture), Text(output));
    }

    [Theory]
    [InlineData(80, "2x2", JpegChromaSubsampling.Yuv420)]
    [InlineData(90, "1x1", JpegChromaSubsampling.Yuv444)]
    public async Task Our_files_are_no_larger_than_cjpeg_optimize_makes(int quality, string sample, JpegChromaSubsampling subsampling)
    {
        string cjpeg = Oracle.Require("cjpeg");
        string magick = Oracle.Require("magick");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        using Image photo = JpegEncoderTests.Photo(640, 480);
        await File.WriteAllBytesAsync(folder.File("in.png"), Png.PngEncoderTests.Save(photo), TestContext.Current.CancellationToken);
        (int converted, _, string convertErrors) = await Oracle.RunAsync(magick, folder.File("in.png"), "ppm:" + folder.File("in.ppm"));
        Assert.True(converted == 0, convertErrors);
        (int exit, _, string errors) = await Oracle.RunAsync(cjpeg, "-quality", $"{quality}", "-sample", sample, "-optimize", "-outfile", folder.File("theirs.jpg"), folder.File("in.ppm"));
        Assert.True(exit == 0, errors);

        long theirs = new FileInfo(folder.File("theirs.jpg")).Length;
        long ours = JpegEncoderTests.Save(photo, new JpegEncoderOptions { Quality = quality, ChromaSubsampling = subsampling }).Length;
        Assert.True(ours <= theirs * 1.03, $"ours {ours} bytes, cjpeg -optimize {theirs}");
    }

    [Fact]
    public async Task Exiftool_reads_the_profile_we_write()
    {
        string exiftool = Oracle.Require("exiftool");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        using Image photo = JpegEncoderTests.Photo(32, 32);
        photo.ColorSpace = ColorSpace.DisplayP3;
        await File.WriteAllBytesAsync(folder.File("p3.jpg"), JpegEncoderTests.Save(photo), TestContext.Current.CancellationToken);

        (int exit, byte[] output, string errors) = await Oracle.RunAsync(exiftool, "-s3", "-ProfileDescription", "-ProfileClass", "-ColorSpaceData", "-ProfileVersion", folder.File("p3.jpg"));
        Assert.True(exit == 0, errors);
        string[] lines = Text(output).Split('\n', StringSplitOptions.TrimEntries);
        Assert.Equal(["Display P3", "Display Device Profile", "RGB", "4.3.0"], lines);
    }

    [Fact]
    public async Task ImageMagick_converts_our_display_p3_to_srgb_as_the_colour_maths_says()
    {
        // A flat Display P3 colour, written with our profile, converted by ImageMagick (LittleCMS) to sRGB using an sRGB
        // profile we also wrote. The expected value is the published P3-to-sRGB conversion, so this checks the profile
        // bytes, not our own arithmetic.
        string magick = Oracle.Require("magick");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        using var flat = new Image(64, 64, PixelFormat.Rgb24) { ColorSpace = ColorSpace.DisplayP3 };
        foreach (ref Rgb24 pixel in System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Rgb24>(flat.GetPixelBytes()))
        {
            pixel = new Rgb24(200, 100, 50);
        }

        await File.WriteAllBytesAsync(folder.File("p3.jpg"), JpegEncoderTests.Save(flat, new JpegEncoderOptions { Quality = 100, ChromaSubsampling = JpegChromaSubsampling.Yuv444 }), TestContext.Current.CancellationToken);
        byte[] srgb = IccProfileWriter.Write("sRGB", [0.3127, 0.3290, 0.64, 0.33, 0.30, 0.60, 0.15, 0.06], IccProfileWriter.Curve.Srgb);
        await File.WriteAllBytesAsync(folder.File("srgb.icc"), srgb, TestContext.Current.CancellationToken);

        (int exit, byte[] rgb, string errors) = await Oracle.RunAsync(
            magick, folder.File("p3.jpg"), "-intent", "Relative", "-profile", folder.File("srgb.icc"), "-crop", "1x1+32+32", "-depth", "8", "rgb:-");
        Assert.True(exit == 0, errors);

        (int r, int g, int b) = P3ToSrgb(200, 100, 50);
        Assert.True(Math.Abs(rgb[0] - r) <= 3 && Math.Abs(rgb[1] - g) <= 3 && Math.Abs(rgb[2] - b) <= 3, $"({rgb[0]}, {rgb[1]}, {rgb[2]}), expected ({r}, {g}, {b})");
    }

    // Display P3 to sRGB: both use the sRGB curve; the linear matrix is the published one (D65, no adaptation needed).
    private static (int R, int G, int B) P3ToSrgb(int r, int g, int b)
    {
        static double Linear(int v) => v / 255.0 <= 0.04045 ? v / 255.0 / 12.92 : Math.Pow(((v / 255.0) + 0.055) / 1.055, 2.4);
        static int Encode(double l)
        {
            l = Math.Clamp(l, 0, 1);
            return (int)Math.Round((l <= 0.0031308 ? l * 12.92 : (1.055 * Math.Pow(l, 1 / 2.4)) - 0.055) * 255);
        }

        double lr = Linear(r);
        double lg = Linear(g);
        double lb = Linear(b);
        return (
            Encode((1.2249401 * lr) - (0.2249404 * lg)),
            Encode((-0.0420569 * lr) + (1.0420571 * lg)),
            Encode((-0.0196376 * lr) - (0.0786361 * lg) + (1.0982735 * lb)));
    }
}
