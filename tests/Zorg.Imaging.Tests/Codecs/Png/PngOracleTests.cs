using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Zorg.Imaging.Tests.Codecs.Png;

/// <summary>Checks the PNG codec against ImageMagick (libpng underneath), both ways.</summary>
public class PngOracleTests
{
    public static TheoryData<int, int, bool> ImageMagickKinds()
    {
        var data = new TheoryData<int, int, bool>();
        (int Colour, int Depth)[] kinds = [(0, 1), (0, 2), (0, 4), (0, 8), (0, 16), (2, 8), (2, 16), (3, 2), (3, 8), (4, 8), (4, 16), (6, 8), (6, 16)];
        foreach ((int colour, int depth) in kinds)
        {
            data.Add(colour, depth, false);
            data.Add(colour, depth, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ImageMagickKinds))]
    public async Task Files_ImageMagick_writes_decode_to_the_pixels_it_reads(int colourType, int depth, bool interlaced)
    {
        string magick = Oracle.Require("magick");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        using Image source = PngEncoderTests.Smooth(53, 37, PixelFormat.Rgba32, seed: depth);
        await File.WriteAllBytesAsync(folder.File("source.png"), PngEncoderTests.Save(source), TestContext.Current.CancellationToken);

        var arguments = new List<string> { folder.File("source.png") };
        if (colourType is 0 or 4)
        {
            arguments.AddRange(["-colorspace", "Gray"]);
        }

        if (colourType is 0 or 2)
        {
            arguments.AddRange(["-alpha", "off"]);
        }

        if (depth == 16)
        {
            // A blur in ImageMagick's 16-bit (or float) pipeline gives samples that aren't multiples of 257.
            arguments.AddRange(["-blur", "0x0.7"]);
        }

        if (colourType == 3)
        {
            arguments.AddRange(["-colors", (1 << depth).ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        arguments.AddRange(["-depth", $"{depth}", "-define", $"png:bit-depth={depth}", "-define", $"png:color-type={colourType}"]);
        if (interlaced)
        {
            arguments.AddRange(["-interlace", "PNG"]);
        }

        arguments.Add("png:" + folder.File("out.png"));
        (int written, _, string writeErrors) = await Oracle.RunAsync(magick, [.. arguments]);
        Assert.True(written == 0, writeErrors);

        byte[] png = await File.ReadAllBytesAsync(folder.File("out.png"), TestContext.Current.CancellationToken);
        Assert.Equal(colourType, png[25]);
        Assert.Equal(depth, png[24]);
        Assert.Equal(interlaced ? 1 : 0, png[28]);

        (int read, byte[] expected, string readErrors) = await Oracle.RunAsync(magick, folder.File("out.png"), "-depth", "8", "rgba:-");
        Assert.True(read == 0, readErrors);

        using Image decoded = Image.Decode(png, new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        Assert.Equal(expected, decoded.GetPixelBytes().ToArray());
    }

    [Theory]
    [InlineData(PixelFormat.L8, 0)]
    [InlineData(PixelFormat.L8, 9)]
    [InlineData(PixelFormat.Rgb24, 1)]
    [InlineData(PixelFormat.Rgb24, 6)]
    [InlineData(PixelFormat.Rgba32, 0)]
    [InlineData(PixelFormat.Rgba32, 6)]
    [InlineData(PixelFormat.Bgra32, 9)]
    public async Task Files_we_write_read_back_in_ImageMagick_without_complaint(PixelFormat format, int level)
    {
        string magick = Oracle.Require("magick");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        using Image image = PngEncoderTests.Smooth(300, 250, format, seed: level);
        await File.WriteAllBytesAsync(folder.File("ours.png"), PngEncoderTests.Save(image, new PngEncoderOptions { CompressionLevel = level }), TestContext.Current.CancellationToken);

        (int exit, byte[] rgba, string errors) = await Oracle.RunAsync(magick, folder.File("ours.png"), "-depth", "8", "rgba:-");

        Assert.True(exit == 0, errors);
        Assert.Equal("", errors.Trim());
        using Image asRgba = Image.Decode(PngEncoderTests.Save(image), new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        Assert.Equal(asRgba.GetPixelBytes().ToArray(), rgba);
    }
}
