using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Tests.Codecs.Jpeg;
using Zorg.Imaging.Tests.Codecs.Png;

namespace Zorg.Imaging.Tests.Codecs.Webp;

/// <summary>WebP files from <c>cwebp</c>, lossless and lossy, in every effort and option it has, must decode exactly as <c>dwebp</c> decodes them.</summary>
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

        // Lossy: quality, effort, segments, the strong (normal) and simple loop filters, sharpness, and every alpha
        // method and filter. libwebp's decode is deterministic, so ours must match it exactly.
        string[] lossy =
        [
            "-q 80", "-q 5 -m 0", "-q 100 -m 6", "-q 50 -segments 1", "-q 70 -sns 100 -f 60 -sharpness 7",
            "-q 60 -nostrong -f 40", "-q 90 -f 0", "-q 75 -sharp_yuv", "-q 40 -segments 4 -f 100 -sharpness 3",
            "-q 80 -alpha_filter none", "-q 80 -alpha_filter best -alpha_q 50", "-q 80 -alpha_method 0", "-q 80 -exact -alpha_filter fast",
        ];
        foreach (string options in lossy)
        {
            data.Add(options, 517, 389, options.Contains("alpha", StringComparison.Ordinal));
            data.Add(options, 33, 9, true);
        }

        return data;
    }

    [Fact]
    public async Task A_large_lossy_file_decodes_the_same_on_one_core_and_on_many()
    {
        // Large enough for the row pipeline to use helper threads; the result must be dwebp's either way.
        string cwebp = Oracle.Require("cwebp");
        string dwebp = Oracle.Require("dwebp");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        using Image photo = JpegEncoderTests.Photo(1201, 901, PixelFormat.Rgba32, seed: 9);
        await File.WriteAllBytesAsync(folder.File("in.png"), PngEncoderTests.Save(photo), TestContext.Current.CancellationToken);
        (int written, _, string writeErrors) = await Oracle.RunAsync(cwebp, "-q", "75", "-quiet", folder.File("in.png"), "-o", folder.File("out.webp"));
        Assert.True(written == 0, writeErrors);
        (int read, _, string readErrors) = await Oracle.RunAsync(dwebp, "-quiet", "-pam", folder.File("out.webp"), "-o", folder.File("out.pam"));
        Assert.True(read == 0, readErrors);

        byte[] pam = await File.ReadAllBytesAsync(folder.File("out.pam"), TestContext.Current.CancellationToken);
        byte[] expected = pam[(pam.AsSpan().IndexOf("ENDHDR\n"u8) + 7)..];
        byte[] webp = await File.ReadAllBytesAsync(folder.File("out.webp"), TestContext.Current.CancellationToken);
        using Image one = Image.Decode(webp, new DecodeOptions { PixelFormat = PixelFormat.Rgba32, MaxDegreeOfParallelism = 1 });
        using Image many = Image.Decode(webp, new DecodeOptions { PixelFormat = PixelFormat.Rgba32, MaxDegreeOfParallelism = 8 });
        Assert.Equal(expected, one.GetPixelBytes().ToArray());
        Assert.Equal(expected, many.GetPixelBytes().ToArray());

        // A failure in a helper thread reaches the caller as the same exception type.
        byte[] cut = webp[..(webp.Length * 2 / 3)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(cut.AsSpan(4), cut.Length - 8);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(cut.AsSpan(16), cut.Length - 20);
        Assert.Throws<ImageFormatException>(() => Image.Decode(cut, new DecodeOptions { MaxDegreeOfParallelism = 8 }).Dispose());
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
