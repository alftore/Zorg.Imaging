using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Tests.Codecs.Jpeg;
using Zorg.Imaging.Tests.Codecs.Png;

namespace Zorg.Imaging.Tests;

public class IdentifyColourTests
{
    private static readonly IdentifyOptions WithColour = new() { ReadColorSpace = true };

    public static TheoryData<string> Formats => new() { "png", "jpeg", "webp", "gif" };

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Identify_reports_the_colour_space_decoding_gives(string format)
    {
        foreach (ColorSpace space in (ColorSpace[])[ColorSpace.DisplayP3, ColorSpace.Srgb])
        {
            using Image photo = JpegEncoderTests.Photo(24, 16);
            photo.ColorSpace = space;
            byte[] file = Save(photo, format);
            using Image decoded = Image.Decode(file);

            IdentifyResult result = Image.Identify(file, WithColour);
            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(decoded.ColorSpace, result.Info!.ColorSpace);

            // The same through a stream that can't seek.
            IdentifyResult streamed = await Image.IdentifyAsync(new ForwardOnlyStream(file), WithColour, TestContext.Current.CancellationToken);
            Assert.Equal(decoded.ColorSpace, streamed.Info!.ColorSpace);

            // Not asked for, it isn't read.
            Assert.Null(Image.Identify(file).Info!.ColorSpace);
        }
    }

    [Fact]
    public void A_cmyk_jpeg_reports_srgb_as_decoding_converts_it()
    {
        byte[] ycck = JpegTestFiles.Sample("ycck");
        Assert.Equal(ColorSpace.Srgb, Image.Identify(ycck, WithColour).Info!.ColorSpace);
    }

    [Fact]
    public void A_damaged_profile_is_reported_as_corrupt_only_when_it_is_read()
    {
        using Image photo = JpegEncoderTests.Photo(16, 16);
        photo.ColorSpace = ColorSpace.DisplayP3;
        byte[] jpeg = Save(photo, "jpeg");

        // Break the "acsp" signature of the profile inside its APP2 segment.
        int at = jpeg.AsSpan().IndexOf("acsp"u8);
        Assert.True(at > 0);
        jpeg[at] = (byte)'x';
        Assert.True(Image.Identify(jpeg).Succeeded);
        IdentifyResult result = Image.Identify(jpeg, WithColour);
        Assert.False(result.Succeeded);
        Assert.Equal(IdentifyStatus.Corrupt, result.Status);
    }

    private static byte[] Save(Image image, string format)
    {
        using var output = new MemoryStream();
        switch (format)
        {
            case "png":
                image.SaveAsPng(output, new PngEncoderOptions { ColorSpaceHandling = ColorSpaceHandling.Keep });
                break;
            case "jpeg":
                image.SaveAsJpeg(output, new JpegEncoderOptions { ColorSpaceHandling = ColorSpaceHandling.Keep });
                break;
            case "webp":
                image.SaveAsWebp(output, new WebpEncoderOptions { ColorSpaceHandling = ColorSpaceHandling.Keep });
                break;
            default:
                image.SaveAsGif(output, new GifEncoderOptions { ColorSpaceHandling = ColorSpaceHandling.Keep });
                break;
        }

        return output.ToArray();
    }
}
