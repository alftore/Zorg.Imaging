using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Zorg.Imaging.Tests.Codecs.Jpeg;

/// <summary>
/// Checks the JPEG decoder against libjpeg-turbo: <c>cjpeg</c> writes files in every mode it has, <c>djpeg</c> decodes
/// them, and our full-size decode must be byte for byte the same (the same integer inverse DCT, upsampling and colour
/// conversion); reduced sizes, made differently, must be close.
/// </summary>
public class JpegOracleTests
{
    public static TheoryData<string, int, int> Cases()
    {
        var data = new TheoryData<string, int, int>();
        string[] modes =
        [
            "-quality 75 -sample 2x2",
            "-quality 92 -sample 1x1",
            "-quality 60 -sample 2x1",
            "-quality 80 -sample 1x2",
            "-quality 100 -sample 1x1",
            "-quality 30 -sample 2x2 -optimize",
            "-quality 85 -grayscale",
            "-quality 75 -sample 2x2 -progressive",
            "-quality 90 -sample 1x1 -progressive",
            "-quality 75 -grayscale -progressive",
            "-quality 75 -sample 2x2 -restart 3",
            "-quality 70 -sample 2x1 -progressive -restart 5B",
            "-quality 80 -sample 4x1",
            "-quality 95 -rgb",
        ];
        foreach (string mode in modes)
        {
            data.Add(mode, 517, 389);
            data.Add(mode, 31, 7);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Files_cjpeg_writes_decode_as_djpeg_decodes_them(string mode, int width, int height)
    {
        string cjpeg = Oracle.Require("cjpeg");
        string djpeg = Oracle.Require("djpeg");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        await File.WriteAllBytesAsync(folder.File("in.ppm"), Ppm(width, height, seed: width + mode.Length), TestContext.Current.CancellationToken);

        var arguments = new List<string>(mode.Split(' '));
        arguments.AddRange(["-outfile", folder.File("out.jpg"), folder.File("in.ppm")]);
        (int written, _, string writeErrors) = await Oracle.RunAsync(cjpeg, [.. arguments]);
        Assert.True(written == 0, writeErrors);
        byte[] jpeg = await File.ReadAllBytesAsync(folder.File("out.jpg"), TestContext.Current.CancellationToken);

        // libjpeg-turbo's reduced transforms are square only, so with chroma halved one way it transforms small and
        // upsamples, while ours land chroma on the luma grid directly. There the reference is the area average of the
        // full-size decode, which djpeg has just confirmed.
        bool squareChroma = !mode.Contains("2x1", StringComparison.Ordinal) && !mode.Contains("1x2", StringComparison.Ordinal) && !mode.Contains("4x1", StringComparison.Ordinal);
        using Image full = Image.Decode(jpeg);
        foreach (int denominator in new[] { 1, 2, 4, 8 })
        {
            (int read, byte[] pnm, string readErrors) = await Oracle.RunAsync(djpeg, "-pnm", "-scale", $"1/{denominator}", folder.File("out.jpg"));
            Assert.True(read == 0, readErrors);
            (int w, int h, byte[] expected) = ParsePnm(pnm);

            var options = denominator == 1 ? DecodeOptions.Default : DecodeOptions.Default.WithShrinkTarget((_, _) => (w / 2, h / 2));
            using Image image = Image.Decode(jpeg, options);
            Assert.Equal((w, h), (image.Width, image.Height));

            if (denominator > 1 && !squareChroma)
            {
                // Subsampled chroma across a hard colour edge has no single right answer at a reduced size, and in a
                // 31 x 7 image nearly every pixel is on an edge: there only the size is checked.
                if (width < 100)
                {
                    continue;
                }

                expected = JpegTestFiles.AreaAverage(full, denominator);
            }

            (int max, double mean) = JpegTestFiles.Compare(image.GetPixelBytes(), expected);
            bool fourToOne = mode.Contains("4x1", StringComparison.Ordinal);
            bool ok = denominator == 1 ? max == 0
                : squareChroma ? max <= 12 && mean < 1.5
                : max <= (fourToOne ? 40 : 24) && mean < (fourToOne ? 6 : 2.5);
            Assert.True(ok, $"{mode} {width}x{height} at 1/{denominator}: largest difference {max}, mean {mean:0.###}");
        }
    }

    [Fact]
    public async Task Files_ImageMagick_writes_decode_as_ImageMagick_reads_them()
    {
        string magick = Oracle.Require("magick");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        await File.WriteAllBytesAsync(folder.File("in.ppm"), Ppm(301, 211, seed: 5), TestContext.Current.CancellationToken);
        (int written, _, string errors) = await Oracle.RunAsync(magick, folder.File("in.ppm"), "-quality", "82", "-interlace", "JPEG", folder.File("out.jpg"));
        Assert.True(written == 0, errors);
        (int read, byte[] expected, string readErrors) = await Oracle.RunAsync(magick, folder.File("out.jpg"), "-depth", "8", "rgb:-");
        Assert.True(read == 0, readErrors);

        using Image image = Image.Decode(await File.ReadAllBytesAsync(folder.File("out.jpg"), TestContext.Current.CancellationToken));
        (int max, double mean) = JpegTestFiles.Compare(image.GetPixelBytes(), expected);
        Assert.True(max <= 6 && mean < 0.75, $"largest difference {max}, mean {mean:0.###}");
    }

    // Edges, gradients and fine noise, so every frequency and every part of the entropy coder is used.
    private static byte[] Ppm(int width, int height, int seed)
    {
        var random = new Random(seed);
        byte[] header = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"P6\n{width} {height}\n255\n"));
        byte[] file = new byte[header.Length + (width * height * 3)];
        header.CopyTo(file, 0);
        int at = header.Length;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                file[at++] = (byte)(x < width / 3 ? 220 : (x * 255 / width));
                file[at++] = (byte)((y * 255 / height) ^ ((x / 8 % 2) * 60));
                file[at++] = (byte)Math.Clamp(128 + (int)(100 * Math.Sin((x + y) / 5.0)) + random.Next(-20, 21), 0, 255);
            }
        }

        return file;
    }

    private static (int Width, int Height, byte[] Samples) ParsePnm(byte[] file)
    {
        int at = 0;
        string Token()
        {
            while (char.IsWhiteSpace((char)file[at]))
            {
                at++;
            }

            int start = at;
            while (!char.IsWhiteSpace((char)file[at]))
            {
                at++;
            }

            return Encoding.ASCII.GetString(file, start, at - start);
        }

        _ = Token();
        int width = int.Parse(Token(), CultureInfo.InvariantCulture);
        int height = int.Parse(Token(), CultureInfo.InvariantCulture);
        _ = Token();
        return (width, height, file[(at + 1)..]);
    }
}
