using System;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Tests.Codecs.Jpeg;
using Zorg.Imaging.Tests.Codecs.Png;
using static Zorg.Imaging.Tests.Codecs.Jpeg.JpegTestFiles;

namespace Zorg.Imaging.Tests;

/// <summary><see cref="DecodeOptions.ShrinkTo"/>: shrink-on-load without the pipeline.</summary>
public class DecodeShrinkToTests
{
    private static byte[] Jpeg160x120()
    {
        using Image photo = JpegEncoderTests.Photo(160, 120, seed: 3);
        return JpegEncoderTests.Save(photo);
    }

    [Fact]
    public void Sides_below_one_are_refused_and_the_default_is_full_size()
    {
        Assert.Null(DecodeOptions.Default.ShrinkTo);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodeOptions { ShrinkTo = (0, 5) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodeOptions { ShrinkTo = (5, 0) });
    }

    [Theory]
    [InlineData(20, 15, 40, 30)] // 1/4 is twice the fitted 20x15
    [InlineData(40, 30, 80, 60)] // 1/2 is twice 40x30
    [InlineData(50, 30, 80, 60)]
    [InlineData(100, 100, 160, 120)]
    [InlineData(1000, 1000, 160, 120)]
    public void A_jpeg_decodes_at_the_scale_the_pipeline_would_choose(int boxWidth, int boxHeight, int width, int height)
    {
        byte[] jpeg = Jpeg160x120();
        using Image shrunk = Image.Decode(jpeg, new DecodeOptions { ShrinkTo = (boxWidth, boxHeight) });
        Assert.Equal((width, height), (shrunk.Width, shrunk.Height));

        // The same pixels as the pipeline's own target for a Fit into that box.
        DecodeOptions pipelineStyle = DecodeOptions.Default.WithShrinkTarget((w, h) => Image.Inside(w, h, boxWidth, boxHeight));
        using Image expected = Image.Decode(jpeg, pipelineStyle);
        Assert.Equal(expected.GetPixelBytes().ToArray(), shrunk.GetPixelBytes().ToArray());
    }

    [Fact]
    public async Task Decode_then_fit_gives_the_pipeline_s_pixels()
    {
        byte[] jpeg = Jpeg160x120();
        using Image large = Image.Decode(jpeg, new DecodeOptions { ShrinkTo = (30, 30) });
        using Image direct = large.Fit(30, 30);
        using Image piped = await ImagePipeline.Load(jpeg).Fit(30, 30).ToImageAsync(TestContext.Current.CancellationToken);
        Assert.Equal((piped.Width, piped.Height), (direct.Width, direct.Height));
        Assert.Equal(piped.GetPixelBytes().ToArray(), direct.GetPixelBytes().ToArray());
    }

    [Fact]
    public void The_box_is_upright()
    {
        // Stored 67x45 with orientation 6: upright 45x67. The 11x17 box fits it to 11x16; 1/2 (23x34 upright) is the
        // smallest scale at least twice that, and the result comes out turned.
        byte[] jpeg = WithSegments(Sample("s420"), (0xE1, [.. "Exif\0\0"u8, .. PngTestFiles.ExifWithOrientation(6)]));
        using Image upright = Image.Decode(jpeg, new DecodeOptions { ShrinkTo = (11, 17) });
        Assert.Equal((23, 34), (upright.Width, upright.Height));
    }

    [Fact]
    public void A_png_decodes_byte_for_byte_as_without()
    {
        using Image source = PngEncoderTests.Smooth(64, 48, PixelFormat.Rgba32, seed: 2);
        byte[] png = PngEncoderTests.Save(source);
        using Image plain = Image.Decode(png);
        using Image boxed = Image.Decode(png, new DecodeOptions { ShrinkTo = (8, 8) });
        Assert.Equal(plain.ToString(), boxed.ToString());
        Assert.Equal(plain.GetPixelBytes().ToArray(), boxed.GetPixelBytes().ToArray());
    }

    [Fact]
    public void A_codec_from_another_package_is_offered_the_box()
    {
        var codec = new FakeAvif();
        using Image image = Image.Decode(FakeAvif.File(400, 200), new DecodeOptions { Codecs = ImageCodecs.Empty.Add(codec), ShrinkTo = (100, 100) });
        Assert.NotNull(codec.LastShrink);
        Assert.True(image.Width < 400);
    }

    [Fact]
    public async Task The_pipeline_s_own_steps_win_over_the_box()
    {
        byte[] jpeg = Jpeg160x120();
        using Image piped = await ImagePipeline.Load(jpeg)
            .WithDecodeOptions(new DecodeOptions { ShrinkTo = (1, 1) })
            .Fit(80, 60)
            .ToImageAsync(TestContext.Current.CancellationToken);
        using Image expected = await ImagePipeline.Load(jpeg).Fit(80, 60).ToImageAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected.GetPixelBytes().ToArray(), piped.GetPixelBytes().ToArray());
    }

    [Fact]
    public async Task A_pipeline_that_crops_first_ignores_the_box()
    {
        // The crop needs the full-size pixels: a box from the options must not shrink the decode under it.
        byte[] jpeg = Jpeg160x120();
        using Image full = Image.Decode(jpeg);
        using Image expected = full.Crop(100, 70, 60, 50);
        using Image piped = await ImagePipeline.Load(jpeg)
            .WithDecodeOptions(new DecodeOptions { ShrinkTo = (10, 10) })
            .Crop(100, 70, 60, 50)
            .ToImageAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected.GetPixelBytes().ToArray(), piped.GetPixelBytes().ToArray());

        using Image unchanged = await ImagePipeline.Load(jpeg)
            .WithDecodeOptions(new DecodeOptions { ShrinkTo = (10, 10) })
            .ToImageAsync(TestContext.Current.CancellationToken);
        Assert.Equal((160, 120), (unchanged.Width, unchanged.Height));
    }
}
