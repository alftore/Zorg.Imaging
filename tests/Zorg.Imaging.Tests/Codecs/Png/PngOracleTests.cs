using System;
using System.Buffers.Binary;
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

        // At 16 bits: ImageMagick scales every depth up the same way, so the values must match exactly.
        (int read16, byte[] expected16, string read16Errors) = await Oracle.RunAsync(magick, folder.File("out.png"), "-depth", "16", "-endian", "MSB", "rgba:-");
        Assert.True(read16 == 0, read16Errors);

        using Image wide = Image.Decode(png, new DecodeOptions { PixelFormat = PixelFormat.Rgba64 });
        Assert.Equal(BigEndian(expected16), wide.GetPixelBytes().ToArray());
    }

    [Theory]
    [InlineData(PixelFormat.L8, 0)]
    [InlineData(PixelFormat.L8, 9)]
    [InlineData(PixelFormat.Rgb24, 1)]
    [InlineData(PixelFormat.Rgb24, 6)]
    [InlineData(PixelFormat.Rgba32, 0)]
    [InlineData(PixelFormat.Rgba32, 6)]
    [InlineData(PixelFormat.Bgra32, 9)]
    [InlineData(PixelFormat.L16, 6)]
    [InlineData(PixelFormat.Rgb48, 1)]
    [InlineData(PixelFormat.Rgba64, 6)]
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

        (int exit16, byte[] rgba64, string errors16) = await Oracle.RunAsync(magick, folder.File("ours.png"), "-depth", "16", "-endian", "MSB", "rgba:-");
        Assert.True(exit16 == 0, errors16);
        using Image asRgba64 = Image.Decode(PngEncoderTests.Save(image), new DecodeOptions { PixelFormat = PixelFormat.Rgba64 });
        Assert.Equal(asRgba64.GetPixelBytes().ToArray(), BigEndian(rgba64));
    }

    public static TheoryData<string> Layouts() => PngOptimiserTests.Layouts();

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task Reduced_files_read_back_in_ImageMagick_as_the_image(string name)
    {
        string magick = Oracle.Require("magick");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        (Image image, PngEncoderOptions options) = PngOptimiserTests.Build(name);
        using (image)
        {
            await File.WriteAllBytesAsync(folder.File("ours.png"), PngEncoderTests.Save(image, options), TestContext.Current.CancellationToken);

            (int exit, byte[] rgba64, string errors) = await Oracle.RunAsync(magick, folder.File("ours.png"), "-depth", "16", "-endian", "MSB", "rgba:-");

            Assert.True(exit == 0, errors);
            Assert.Equal("", errors.Trim());
            Assert.Equal(PngOptimiserTests.Expected(image, options.CleanTransparentPixels), BigEndian(rgba64));
        }
    }

    [Theory]
    [InlineData(PixelFormat.Rgb24, 0)]
    [InlineData(PixelFormat.Rgba32, 0)]
    [InlineData(PixelFormat.Rgb24, 40)]
    [InlineData(PixelFormat.L8, 0)]
    public async Task Optimized_files_are_close_to_oxipng(PixelFormat format, int colours)
    {
        string oxipng = Oracle.Require("oxipng");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        using Image image = PngEncoderTests.Smooth(400, 300, format, seed: 9);
        if (colours > 0)
        {
            Span<byte> pixels = image.GetPixelBytes();
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = (byte)(pixels[i] / (256 / 4) * 60);
            }
        }

        byte[] plain = PngEncoderTests.Save(image, new PngEncoderOptions { ReduceColorType = false });
        byte[] ours = PngEncoderTests.Save(image, new PngEncoderOptions { Optimize = true });
        await File.WriteAllBytesAsync(folder.File("in.png"), plain, TestContext.Current.CancellationToken);

        (int exit, _, string errors) = await Oracle.RunAsync(oxipng, "-o", "2", "--strip", "safe", "--out", folder.File("out.png"), folder.File("in.png"));

        Assert.True(exit == 0, errors);
        long theirs = new FileInfo(folder.File("out.png")).Length;
        // oxipng compresses with libdeflate at level 11, a few per cent ahead of zlib at 9; layouts and filters match.
        Assert.True(ours.Length <= theirs * 1.15, $"ours {ours.Length}, oxipng {theirs}");
    }

    // ImageMagick's 16-bit samples, big-endian, into the machine's order as an Rgba64 image holds them.
    private static byte[] BigEndian(byte[] samples)
    {
        byte[] native = new byte[samples.Length];
        for (int i = 0; i + 1 < samples.Length; i += 2)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(native.AsSpan(i), BinaryPrimitives.ReadUInt16BigEndian(samples.AsSpan(i)));
        }

        return native;
    }
}
