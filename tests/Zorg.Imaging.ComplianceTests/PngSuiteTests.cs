using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Tests;

namespace Zorg.Imaging.ComplianceTests;

/// <summary>
/// PngSuite by Willem van Schaik: every colour type, bit depth and interlace, ancillary chunks, odd sizes, and the
/// broken files, whose names start with <c>x</c>.
/// </summary>
public class PngSuiteTests
{
    private const string Name = "pngsuite";

    public static TheoryData<string> Valid => Corpus.Files(Name, ".png", file => !file.StartsWith('x'));

    public static TheoryData<string> Broken => Corpus.Files(Name, ".png", file => file.StartsWith('x'));

    [Theory]
    [MemberData(nameof(Valid))]
    public void Every_valid_file_decodes_identifies_and_round_trips(string file)
    {
        byte[] png = File.ReadAllBytes(Path.Combine(Corpus.Require(Name), file));
        using Image image = Image.Decode(png, new DecodeOptions { PixelFormat = PixelFormat.Rgba64 });

        IdentifyResult identified = Image.Identify(png);
        Assert.True(identified.Succeeded, identified.Message);
        Assert.Equal((image.Width, image.Height), (identified.Info.Width, identified.Info.Height));

        // Our encoder, with every reduction on, gives the same pixels back.
        using var saved = new MemoryStream();
        image.SaveAsPng(saved);
        using Image again = Image.Decode(saved.ToArray(), new DecodeOptions { PixelFormat = PixelFormat.Rgba64 });
        Assert.Equal(image.GetPixelBytes().ToArray(), again.GetPixelBytes().ToArray());
    }

    [Theory]
    [MemberData(nameof(Valid))]
    public async Task Every_valid_file_decodes_to_what_ImageMagick_decodes(string file)
    {
        string path = Path.Combine(Corpus.Require(Name), file);
        string magick = Oracle.Require("magick");
        using Image image = Image.Decode(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken), new DecodeOptions { PixelFormat = PixelFormat.Rgba64 });

        // 16 bits a sample, little-endian, as Rgba64 lies in memory. ImageMagick writes the stored values: no gamma.
        (int code, byte[] output, string errors) = await Oracle.RunAsync(magick, path, "-depth", "16", "-endian", "LSB", "rgba:-");
        Assert.True(code == 0, errors);
        Assert.Equal(output, image.GetPixelBytes().ToArray());
    }

    [Theory]
    [MemberData(nameof(Broken))]
    public void Every_broken_file_fails_with_our_exception_and_identify_never_throws(string file)
    {
        byte[] png = File.ReadAllBytes(Path.Combine(Corpus.Require(Name), file));
        Assert.ThrowsAny<ImageException>(() => Image.Decode(png).Dispose());
        Exception? error = Record.Exception(() => Image.Identify(png));
        Assert.Null(error);
    }
}
