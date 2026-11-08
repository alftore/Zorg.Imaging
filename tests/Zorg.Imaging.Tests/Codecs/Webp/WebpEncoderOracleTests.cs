using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Tests.Codecs.Jpeg;
using Zorg.Imaging.Tests.Codecs.Png;

namespace Zorg.Imaging.Tests.Codecs.Webp;

/// <summary>Our lossless files must decode in libwebp's <c>dwebp</c> to exactly the pixels we wrote, at about cwebp's size.</summary>
public class WebpEncoderOracleTests
{
    public static TheoryData<string, int, int, int> Cases() => new()
    {
        { "photo", 517, 389, 4 },
        { "photo", 517, 389, 0 },
        { "photo", 517, 389, 6 },
        { "alpha", 301, 7, 4 },
        { "grey", 64, 300, 4 },
        { "graphic", 400, 300, 4 },
        { "two", 129, 65, 4 },
        { "palette", 333, 111, 2 },
        { "noise", 50, 50, 4 },
    };

    private static Image Picture(string kind, int width, int height) => kind switch
    {
        "photo" => JpegEncoderTests.Photo(width, height, PixelFormat.Rgb24, seed: width),
        "alpha" => JpegEncoderTests.Photo(width, height, PixelFormat.Rgba32, seed: width),
        "grey" => JpegEncoderTests.Photo(width, height, PixelFormat.L8, seed: width),
        "graphic" => WebpEncoderTests.Graphic(width, height, 12),
        "two" => WebpEncoderTests.Graphic(width, height, 2),
        "palette" => WebpEncoderTests.Graphic(width, height, 200, alpha: true),
        _ => Noise(width, height),
    };

    private static Image Noise(int width, int height)
    {
        var image = new Image(width, height, PixelFormat.Rgba32);
        new Random(width).NextBytes(image.GetPixelBytes());
        return image;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Dwebp_decodes_our_files_exactly_and_they_are_about_cwebps_size(string kind, int width, int height, int effort)
    {
        string dwebp = Oracle.Require("dwebp");
        string cwebp = Oracle.Require("cwebp");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        using Image picture = Picture(kind, width, height);
        byte[] ours = WebpEncoderTests.Save(picture, new WebpEncoderOptions { Lossless = true, Effort = effort });
        await File.WriteAllBytesAsync(folder.File("ours.webp"), ours, TestContext.Current.CancellationToken);

        (int read, _, string readErrors) = await Oracle.RunAsync(dwebp, "-quiet", "-pam", folder.File("ours.webp"), "-o", folder.File("ours.pam"));
        Assert.True(read == 0, readErrors);
        byte[] pam = await File.ReadAllBytesAsync(folder.File("ours.pam"), TestContext.Current.CancellationToken);
        Assert.Equal(WebpEncoderTests.Rgba(picture), pam[(pam.AsSpan().IndexOf("ENDHDR\n"u8) + 7)..]);

        // cwebp at the same effort, -exact so it keeps the colour under transparent pixels as we do.
        await File.WriteAllBytesAsync(folder.File("in.png"), PngEncoderTests.Save(picture), TestContext.Current.CancellationToken);
        (int written, _, string writeErrors) = await Oracle.RunAsync(cwebp, "-lossless", "-exact", "-m", effort.ToString(System.Globalization.CultureInfo.InvariantCulture), "-quiet", folder.File("in.png"), "-o", folder.File("cwebp.webp"));
        Assert.True(written == 0, writeErrors);
        long theirs = new FileInfo(folder.File("cwebp.webp")).Length;
        TestContext.Current.TestOutputHelper?.WriteLine($"{kind} {width}x{height} effort {effort}: ours {ours.Length} bytes, cwebp {theirs}");
        Assert.True(ours.Length <= (theirs * 1.25) + 64, $"ours {ours.Length} bytes, cwebp {theirs}");
    }
}
