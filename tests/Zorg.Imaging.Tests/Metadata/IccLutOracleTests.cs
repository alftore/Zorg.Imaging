using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Metadata;
using Zorg.Imaging.Tests.Codecs.Jpeg;
using static Zorg.Imaging.Tests.Metadata.IccTestProfiles;

namespace Zorg.Imaging.Tests.Metadata;

/// <summary>Lookup-table profiles converted to sRGB by us and by LittleCMS, through ImageMagick's <c>-profile</c>.</summary>
public class IccLutOracleTests
{
    [Theory]
    [InlineData(TagType.Lut16, false, 2)]
    [InlineData(TagType.Lut16, true, 17)]
    [InlineData(TagType.Lut8, true, 17)]
    [InlineData(TagType.AToB, true, 9)]
    public async Task We_convert_as_LittleCMS_does(TagType type, bool lab, int points)
    {
        string magick = Oracle.Require("magick");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        using Image photo = JpegEncoderTests.Photo(96, 64, seed: 3);
        photo.ColorSpace = ColorSpace.FromIccProfile(SrgbLut(type, lab, points));
        using (FileStream file = File.Create(folder.File("in.png")))
        {
            await photo.SaveAsPngAsync(file, new PngEncoderOptions { ColorSpaceHandling = ColorSpaceHandling.Keep, ReduceColorType = false }, TestContext.Current.CancellationToken);
        }

        byte[] srgb = IccProfileWriter.Write("sRGB", [0.3127, 0.3290, 0.64, 0.33, 0.30, 0.60, 0.15, 0.06], IccProfileWriter.Curve.Srgb);
        await File.WriteAllBytesAsync(folder.File("srgb.icc"), srgb, TestContext.Current.CancellationToken);
        (int code, byte[] output, string errors) = await Oracle.RunAsync(magick, folder.File("in.png"), "-intent", "Perceptual", "-profile", folder.File("srgb.icc"), "-depth", "8", "rgb:-");
        Assert.True(code == 0, errors);

        using Image ours = photo.ToSrgb();
        int max = 0;
        long total = 0;
        ReadOnlySpan<byte> mine = ours.GetPixelBytes();
        Assert.Equal(mine.Length, output.Length);
        for (int i = 0; i < mine.Length; i++)
        {
            int d = Math.Abs(mine[i] - output[i]);
            max = Math.Max(max, d);
            total += d;
        }

        double mean = (double)total / mine.Length;
        Assert.True(max <= 3 && mean < 0.6, $"largest {max}, mean {mean:0.###}");
    }
}
