using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Tests.Codecs.Jpeg;
using Zorg.Imaging.Tests.Codecs.Png;

namespace Zorg.Imaging.Tests.Codecs.Webp;

/// <summary>Lossless WebP files from <c>cwebp</c>, in every effort and option it has, must decode exactly as <c>dwebp</c> decodes them.</summary>
public class WebpOracleTests
{
    public static TheoryData<string, int, int, bool> Cases()
    {
        var data = new TheoryData<string, int, int, bool>();
        foreach (string options in new[] { "-lossless -z 0", "-lossless -z 3", "-lossless -z 9", "-lossless -exact -m 6", "-near_lossless 60", "-lossless -q 100 -m 6" })
        {
            data.Add(options, 517, 389, false);
            data.Add(options, 301, 7, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Files_cwebp_writes_decode_exactly_as_dwebp_decodes_them(string options, int width, int height, bool alpha)
    {
        string cwebp = Oracle.Require("cwebp");
        string dwebp = Oracle.Require("dwebp");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        using Image photo = JpegEncoderTests.Photo(width, height, alpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24, seed: width);
        await File.WriteAllBytesAsync(folder.File("in.png"), PngEncoderTests.Save(photo), TestContext.Current.CancellationToken);

        string[] arguments = [.. options.Split(' '), "-quiet", folder.File("in.png"), "-o", folder.File("out.webp")];
        (int written, _, string writeErrors) = await Oracle.RunAsync(cwebp, arguments);
        Assert.True(written == 0, writeErrors);
        (int read, _, string readErrors) = await Oracle.RunAsync(dwebp, "-quiet", "-pam", folder.File("out.webp"), "-o", folder.File("out.pam"));
        Assert.True(read == 0, readErrors);

        byte[] pam = await File.ReadAllBytesAsync(folder.File("out.pam"), TestContext.Current.CancellationToken);
        byte[] expected = pam[(pam.AsSpan().IndexOf("ENDHDR\n"u8) + 7)..];
        using Image image = Image.Decode(await File.ReadAllBytesAsync(folder.File("out.webp"), TestContext.Current.CancellationToken), new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        Assert.Equal(expected, image.GetPixelBytes().ToArray());
    }
}
