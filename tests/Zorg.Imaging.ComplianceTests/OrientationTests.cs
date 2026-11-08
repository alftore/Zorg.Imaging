using System;
using System.IO;
using Xunit;

namespace Zorg.Imaging.ComplianceTests;

/// <summary>
/// exif-orientation-examples by recurser: one landscape and one portrait photo, each saved with EXIF orientations 0
/// to 8, so that every file looks the same once its orientation is applied (guarantee 2: orientation is applied).
/// </summary>
public class OrientationTests
{
    private const string Name = "orientation";

    public static TheoryData<string, int> Files()
    {
        var data = new TheoryData<string, int>();
        foreach (string shape in (string[])["Landscape", "Portrait"])
        {
            for (int orientation = 0; orientation <= 8; orientation++)
            {
                data.Add(shape, orientation);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Every_orientation_decodes_upright(string shape, int orientation)
    {
        string folder = Corpus.Require(Name);
        using Image upright = Decode(folder, shape, 1);
        byte[] jpeg = File.ReadAllBytes(Path.Combine(folder, $"{shape}_{orientation}.jpg"));
        using Image image = Image.Decode(jpeg, new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });

        Assert.Equal((upright.Width, upright.Height), (image.Width, image.Height));
        Assert.Equal(shape == "Landscape", image.Width > image.Height);

        // The files are separate JPEG encodings of the turned photo, so the pixels differ by compression noise only.
        Assert.InRange(MeanDifference(upright, image), 0, 4);

        IdentifyResult identified = Image.Identify(jpeg);
        Assert.True(identified.Succeeded, identified.Message);
        Assert.Equal((image.Width, image.Height), (identified.Info.DisplayWidth, identified.Info.DisplayHeight));
    }

    private static Image Decode(string folder, string shape, int orientation) =>
        Image.Decode(File.ReadAllBytes(Path.Combine(folder, $"{shape}_{orientation}.jpg")), new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });

    private static double MeanDifference(Image a, Image b)
    {
        ReadOnlySpan<byte> x = a.GetPixelBytes();
        ReadOnlySpan<byte> y = b.GetPixelBytes();
        long total = 0;
        for (int i = 0; i < x.Length; i++)
        {
            total += Math.Abs(x[i] - y[i]);
        }

        return (double)total / x.Length;
    }
}
