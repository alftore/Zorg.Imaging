using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Tests.Codecs.Png;

namespace Zorg.Imaging.Tests;

public class ImagePipelineTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Steps_give_the_same_pixels_as_calling_them_directly()
    {
        using Image source = PngEncoderTests.Smooth(120, 90, PixelFormat.Rgba32, seed: 4);
        byte[] png = PngEncoderTests.Save(source);

        using Image piped = await ImagePipeline.Load(png)
            .Crop(10, 5, 100, 80)
            .Rotate(90)
            .Fit(40, 40)
            .FlipHorizontal()
            .Flatten(new Rgba32(255, 255, 255))
            .ToImageAsync(Token);

        using Image decoded = Image.Decode(png);
        using Image cropped = decoded.Crop(10, 5, 100, 80);
        using Image turned = cropped.Rotate(90);
        using Image fitted = turned.Fit(40, 40);
        using Image mirrored = fitted.FlipHorizontal();
        using Image direct = mirrored.Flatten(new Rgba32(255, 255, 255));

        Assert.Equal((PixelFormat.Rgb24, 32, 40), (piped.PixelFormat, piped.Width, piped.Height));
        Assert.Equal(direct.GetPixelBytes().ToArray(), piped.GetPixelBytes().ToArray());
    }

    [Fact]
    public async Task Every_step_matches_its_image_method()
    {
        using Image source = PngEncoderTests.Smooth(30, 20, PixelFormat.Rgba32, seed: 5);
        (Func<ImagePipeline, ImagePipeline> Step, Func<Image, Image> Direct)[] cases =
        [
            (p => p.Resize(13, 0, Resampler.Mitchell), i => i.Resize(13, 0, Resampler.Mitchell)),
            (p => p.Fit(10, 10), i => i.Fit(10, 10)),
            (p => p.Fill(12, 12), i => i.Fill(12, 12)),
            (p => p.Pad(40, 40, new Rgba32(1, 2, 3)), i => i.Pad(40, 40, new Rgba32(1, 2, 3))),
            (p => p.Crop(1, 2, 3, 4), i => i.Crop(1, 2, 3, 4)),
            (p => p.Rotate(-90), i => i.Rotate(-90)),
            (p => p.FlipVertical(), i => i.FlipVertical()),
            (p => p.Flatten(default), i => i.Flatten(default)),
        ];
        foreach ((Func<ImagePipeline, ImagePipeline> step, Func<Image, Image> direct) in cases)
        {
            using Image piped = await step(ImagePipeline.From(source)).ToImageAsync(Token);
            using Image expected = direct(source);
            Assert.Equal(expected.ToString(), piped.ToString());
            Assert.Equal(expected.GetPixelBytes().ToArray(), piped.GetPixelBytes().ToArray());
        }
    }

    [Fact]
    public async Task A_pipeline_is_immutable_and_reusable()
    {
        byte[] png = PngEncoderTests.Save(PngEncoderTests.Smooth(64, 48, PixelFormat.Rgb24, seed: 1));
        ImagePipeline loaded = ImagePipeline.Load(png);
        ImagePipeline small = loaded.Fit(16, 16);
        ImagePipeline large = loaded.Fit(32, 32);

        using Image first = await small.ToImageAsync(Token);
        using Image again = await small.ToImageAsync(Token);
        using Image other = await large.ToImageAsync(Token);
        using Image original = await loaded.ToImageAsync(Token);
        Assert.Equal((16, 12), (first.Width, first.Height));
        Assert.Equal(first.GetPixelBytes().ToArray(), again.GetPixelBytes().ToArray());
        Assert.Equal((32, 24), (other.Width, other.Height));
        Assert.Equal((64, 48), (original.Width, original.Height));
        Assert.Equal("bytes → Fit(16, 16)", small.ToString());
        Assert.Equal("bytes", loaded.ToString());
    }

    [Fact]
    public async Task A_stream_that_cannot_seek_is_read_once_when_the_pipeline_runs()
    {
        byte[] png = PngEncoderTests.Save(PngEncoderTests.Smooth(20, 10, PixelFormat.Rgba32, seed: 2));
        var stream = new ForwardOnlyStream(png);
        ImagePipeline pipeline = ImagePipeline.Load(stream).Resize(10, 0);

        using Image image = await pipeline.ToImageAsync(Token);
        Assert.Equal((10, 5), (image.Width, image.Height));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => (await pipeline.ToImageAsync(Token)).Dispose());
    }

    [Fact]
    public async Task A_peeked_stream_works_after_identify()
    {
        byte[] png = PngEncoderTests.Save(PngEncoderTests.Smooth(20, 10, PixelFormat.Rgba32, seed: 3));
        FormatPeek peek = await ImageFormats.PeekAsync(new ForwardOnlyStream(png), Token);
        IdentifyResult identified = await Image.IdentifyAsync(peek.Stream, cancellationToken: Token);
        Assert.True(identified.Succeeded);

        using Image image = await ImagePipeline.Load(peek.Stream).Fit(5, 5).ToImageAsync(Token);
        Assert.Equal((5, 3), (image.Width, image.Height));
    }

    [Fact]
    public async Task A_file_is_opened_only_when_the_pipeline_runs()
    {
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        string path = folder.File("photo.png");
        ImagePipeline pipeline = ImagePipeline.Load(path).Fit(8, 8);
        await Assert.ThrowsAsync<FileNotFoundException>(async () => (await pipeline.ToImageAsync(Token)).Dispose());

        await File.WriteAllBytesAsync(path, PngEncoderTests.Save(PngEncoderTests.Smooth(16, 16, PixelFormat.L8, seed: 1)), Token);
        using Image image = await pipeline.ToImageAsync(Token);
        Assert.Equal((8, 8), (image.Width, image.Height));
        using Image twice = await pipeline.ToImageAsync(Token);
        Assert.Equal(image.GetPixelBytes().ToArray(), twice.GetPixelBytes().ToArray());
    }

    [Fact]
    public async Task An_image_source_is_never_changed_or_disposed()
    {
        using Image source = PngEncoderTests.Smooth(10, 10, PixelFormat.Rgba32, seed: 6);
        byte[] before = source.GetPixelBytes().ToArray();

        using Image copy = await ImagePipeline.From(source).ToImageAsync(Token);
        Assert.NotSame(source, copy);
        Assert.Equal(before, copy.GetPixelBytes().ToArray());

        using Image turned = await ImagePipeline.From(source).Rotate(90).ToImageAsync(Token);
        Assert.Equal(before, source.GetPixelBytes().ToArray());

        using var direct = new MemoryStream();
        await source.SaveAsPngAsync(direct, cancellationToken: Token);
        using var piped = new MemoryStream();
        await ImagePipeline.From(source).SaveAsPngAsync(piped, cancellationToken: Token);
        Assert.Equal(direct.ToArray(), piped.ToArray());
    }

    [Fact]
    public async Task Saving_writes_the_result_as_png_with_its_colour_space()
    {
        using var wide = new Image(40, 30, PixelFormat.Rgba64) { ColorSpace = ColorSpace.DisplayP3 };
        wide.GetPixelBytes().Fill(0x90);
        using var output = new MemoryStream();
        await ImagePipeline.From(wide).Fit(20, 20).SaveAsPngAsync(output, new PngEncoderOptions { CompressionLevel = 9 }, Token);

        // One colour, so the file is a palette; decoded at 16 bits it is the same.
        using Image decoded = Image.Decode(output.ToArray(), new DecodeOptions { PixelFormat = PixelFormat.Rgba64 });
        Assert.Equal((20, 15), (decoded.Width, decoded.Height));
        Assert.Equal(ColorSpace.DisplayP3, decoded.ColorSpace);
        Assert.Equal(new Rgba64(0x9090, 0x9090, 0x9090, 0x9090), decoded.GetRow<Rgba64>(14)[19]);
    }

    [Fact]
    public async Task Decode_options_and_limits_apply()
    {
        byte[] png = PngEncoderTests.Save(PngEncoderTests.Smooth(20, 20, PixelFormat.Rgba32, seed: 7));
        var tight = new DecodeOptions { Limits = new ImageLimits { MaxPixels = 100 } };
        await Assert.ThrowsAsync<ImageLimitException>(async () => (await ImagePipeline.Load(png).WithDecodeOptions(tight).Fit(5, 5).ToImageAsync(Token)).Dispose());

        var bgra = new DecodeOptions { PixelFormat = PixelFormat.Bgra32 };
        using Image image = await ImagePipeline.Load(png).WithDecodeOptions(bgra).ToImageAsync(Token);
        Assert.Equal(PixelFormat.Bgra32, image.PixelFormat);
    }

    [Fact]
    public async Task Bad_input_and_bad_steps_fail_when_the_pipeline_runs()
    {
        await Assert.ThrowsAsync<UnsupportedImageFormatException>(async () => (await ImagePipeline.Load("not an image"u8.ToArray()).ToImageAsync(Token)).Dispose());

        byte[] png = PngEncoderTests.Save(PngEncoderTests.Smooth(10, 10, PixelFormat.L8, seed: 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => (await ImagePipeline.Load(png).Crop(5, 5, 10, 10).ToImageAsync(Token)).Dispose());

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => (await ImagePipeline.Load(png).Fit(5, 5).ToImageAsync(cancelled.Token)).Dispose());
    }

    [Fact]
    public void Arguments_are_checked_when_a_step_is_added()
    {
        ImagePipeline pipeline = ImagePipeline.Load([1]);
        Assert.Throws<ArgumentNullException>(() => ImagePipeline.Load((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => ImagePipeline.Load((Stream)null!));
        Assert.Throws<ArgumentException>(() => ImagePipeline.Load(""));
        Assert.Throws<ArgumentNullException>(() => ImagePipeline.From(null!));
        Assert.Throws<NotSupportedException>(() => ImagePipeline.Load(new WriteOnlyStream()));
        Assert.Throws<ArgumentNullException>(() => pipeline.WithDecodeOptions(null!));
        Assert.Throws<ArgumentException>(() => pipeline.Resize(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => pipeline.Resize(-1, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => pipeline.Fit(0, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => pipeline.Fill(5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => pipeline.Pad(0, 5, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => pipeline.Crop(-1, 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => pipeline.Crop(0, 0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => pipeline.Rotate(45));
        Assert.Equal("bytes → Fit(5, 6, Lanczos3) → Rotate(-90) → Flatten(Rgba32(1, 2, 3, 255))", pipeline.Fit(5, 6, Resampler.Lanczos3).Rotate(-90).Flatten(new Rgba32(1, 2, 3)).ToString());
    }

    [Fact]
    public async Task Saving_to_a_stream_that_cannot_write_is_refused_before_any_work()
    {
        ImagePipeline pipeline = ImagePipeline.Load("not an image"u8.ToArray());
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await pipeline.SaveAsPngAsync(null!, cancellationToken: Token));
        using var readOnly = new MemoryStream([], writable: false);
        await Assert.ThrowsAsync<NotSupportedException>(async () => await pipeline.SaveAsPngAsync(readOnly, cancellationToken: Token));
    }

    private sealed class WriteOnlyStream : MemoryStream
    {
        public override bool CanRead => false;
    }
}
