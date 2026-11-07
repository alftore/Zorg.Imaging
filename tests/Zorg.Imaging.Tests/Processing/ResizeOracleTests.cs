using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Tests.Codecs.Png;

namespace Zorg.Imaging.Tests.Processing;

/// <summary>
/// Checks the resampler against ImageMagick's <c>-resize</c>, which uses the same filters, centres and edge handling
/// in floating point. Shrinking is compared in linear light (<c>-colorspace RGB</c>), enlarging in sRGB.
/// </summary>
public class ResizeOracleTests
{
    public static TheoryData<string, string, int, int, bool> Cases()
    {
        var data = new TheoryData<string, string, int, int, bool>();
        (string Ours, string Theirs)[] filters = [("Bilinear", "Triangle"), ("Catmull-Rom", "Catrom"), ("Mitchell", "Mitchell"), ("Lanczos3", "Lanczos")];
        foreach ((string ours, string theirs) in filters)
        {
            data.Add(ours, theirs, 23, 17, true);
            data.Add(ours, theirs, 7, 30, false);
            data.Add(ours, theirs, 151, 97, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Resizing_matches_ImageMagick(string ours, string theirs, int width, int height, bool alpha)
    {
        string magick = Oracle.Require("magick");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        using Image source = Photo(64, 48, alpha);
        await File.WriteAllBytesAsync(folder.File("in.png"), PngEncoderTests.Save(source), TestContext.Current.CancellationToken);

        bool shrinking = width < source.Width || height < source.Height;
        var arguments = new List<string> { folder.File("in.png") };
        if (shrinking)
        {
            arguments.AddRange(["-colorspace", "RGB"]);
        }

        arguments.AddRange(["-filter", theirs, "-resize", string.Create(CultureInfo.InvariantCulture, $"{width}x{height}!")]);
        if (shrinking)
        {
            arguments.AddRange(["-colorspace", "sRGB"]);
        }

        arguments.AddRange(["-depth", "8", "rgba:-"]);
        (int exit, byte[] expected, string errors) = await Oracle.RunAsync(magick, [.. arguments]);
        Assert.True(exit == 0, errors);

        using Image resized = source.Resize(width, height, ResizeTests.Filter(ours));
        byte[] actual = resized.GetPixelBytes().ToArray();
        Assert.Equal(expected.Length, actual.Length);

        int worst = 0;
        for (int i = 0; i < actual.Length; i += 4)
        {
            int a = actual[i + 3];
            worst = Math.Max(worst, Math.Abs(a - expected[i + 3]));

            // A nearly transparent pixel's colour is divided by a tiny alpha, so its rounding says little.
            if (a >= 16)
            {
                for (int k = 0; k < 3; k++)
                {
                    worst = Math.Max(worst, Math.Abs(actual[i + k] - expected[i + k]));
                }
            }
        }

        Assert.True(worst <= 1, $"largest difference {worst}");
    }

    // Edges, gradients and noise, with alpha that varies, so every part of the filter matters.
    private static Image Photo(int width, int height, bool alpha)
    {
        var random = new Random(width * height);
        var image = new Image(width, height, PixelFormat.Rgba32);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba32> row = image.GetRow<Rgba32>(y);
            for (int x = 0; x < width; x++)
            {
                byte r = (byte)(x < width / 2 ? 230 : 20);
                byte g = (byte)((y * 255 / (height - 1)) ^ (((x / 4) + (y / 4)) % 2 == 0 ? 0 : 40));
                byte b = (byte)random.Next(256);
                byte a = alpha ? (byte)Math.Clamp(((x + y) * 5) - 40, 0, 255) : (byte)255;
                row[x] = new Rgba32(r, g, b, a);
            }
        }

        return image;
    }
}
