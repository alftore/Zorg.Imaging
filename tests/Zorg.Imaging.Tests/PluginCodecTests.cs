using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Zorg.Imaging.Tests;

/// <summary>Codecs added by other packages: how the library calls them, and the checks it makes around them.</summary>
public sealed class PluginCodecTests
{
    [Fact]
    public void Without_a_codec_the_format_is_unsupported()
    {
        byte[] file = FakeAvif.File(10, 8);
        Assert.Throws<UnsupportedImageFormatException>(() => Image.Decode(file));
        Assert.Equal(IdentifyStatus.UnsupportedFormat, Image.Identify(file).Status);
    }

    [Fact]
    public void A_codec_decodes_and_identifies_its_format()
    {
        var codec = new FakeAvif();
        ImageCodecs codecs = ImageCodecs.Empty.Add(codec);
        byte[] file = FakeAvif.File(10, 8);

        IdentifyResult identified = Image.Identify(file, new IdentifyOptions { Codecs = codecs });
        Assert.Equal(IdentifyStatus.Ok, identified.Status);
        Assert.Same(ImageFormat.Avif, identified.Info!.Format);
        Assert.Equal((10, 8), (identified.Info.Width, identified.Info.Height));

        using Image image = Image.Decode(file, new DecodeOptions { Codecs = codecs });
        Assert.Equal((10, 8), (image.Width, image.Height));
        Assert.Equal(1, codec.Decodes);
        Assert.True(codecs.Supports(ImageFormat.Avif));
        Assert.False(codecs.Supports(ImageFormat.Heif));
    }

    [Fact]
    public async Task A_codec_decodes_from_a_stream()
    {
        ImageCodecs codecs = ImageCodecs.Empty.Add(new FakeAvif());
        using var stream = new MemoryStream(FakeAvif.File(12, 5));
        using Image image = await Image.DecodeAsync(stream, new DecodeOptions { Codecs = codecs }, TestContext.Current.CancellationToken);
        Assert.Equal((12, 5), (image.Width, image.Height));
    }

    [Fact]
    public void The_library_applies_the_orientation()
    {
        ImageCodecs codecs = ImageCodecs.Empty.Add(new FakeAvif());
        byte[] file = FakeAvif.File(10, 8, orientation: 6);

        using Image upright = Image.Decode(file, new DecodeOptions { Codecs = codecs });
        Assert.Equal((8, 10), (upright.Width, upright.Height));

        using Image stored = Image.Decode(file, new DecodeOptions { Codecs = codecs, ApplyOrientation = false });
        Assert.Equal((10, 8), (stored.Width, stored.Height));
    }

    [Fact]
    public void The_size_is_checked_against_the_limits_before_the_codec_decodes()
    {
        var codec = new FakeAvif();
        var limits = new ImageLimits { MaxPixels = 50 };
        byte[] file = FakeAvif.File(10, 8);

        Assert.Throws<ImageLimitException>(() => Image.Decode(file, new DecodeOptions { Codecs = ImageCodecs.Empty.Add(codec), Limits = limits }));
        Assert.Equal(0, codec.Decodes);
        Assert.Equal(IdentifyStatus.ExceedsLimits, Image.Identify(file, new IdentifyOptions { Codecs = ImageCodecs.Empty.Add(codec), Limits = limits }).Status);
    }

    [Fact]
    public async Task A_file_longer_than_MaxFileBytes_is_refused()
    {
        var codec = new FakeAvif();
        var options = new DecodeOptions { Codecs = ImageCodecs.Empty.Add(codec), Limits = new ImageLimits { MaxFileBytes = 100 } };
        byte[] file = FakeAvif.File(10, 8, padding: 200);

        Assert.Throws<ImageLimitException>(() => Image.Decode(file, options));
        using var stream = new MemoryStream(file);
        await Assert.ThrowsAsync<ImageLimitException>(async () => await Image.DecodeAsync(stream, options, TestContext.Current.CancellationToken));
        Assert.Equal(0, codec.Decodes);
    }

    [Fact]
    public void A_codec_failure_becomes_an_ImageCodecException()
    {
        var options = new DecodeOptions { Codecs = ImageCodecs.Empty.Add(new FakeAvif { Fail = FakeAvifFailure.Decode }) };
        ImageCodecException failure = Assert.Throws<ImageCodecException>(() => Image.Decode(FakeAvif.File(10, 8), options));
        Assert.Equal("Fake AVIF", failure.CodecName);
        Assert.IsType<InvalidOperationException>(failure.InnerException);

        var header = new IdentifyOptions { Codecs = ImageCodecs.Empty.Add(new FakeAvif { Fail = FakeAvifFailure.Header }) };
        Assert.Equal(IdentifyStatus.CodecFailed, Image.Identify(FakeAvif.File(10, 8), header).Status);
    }

    [Fact]
    public void A_damaged_file_stays_a_format_error()
    {
        var options = new DecodeOptions { Codecs = ImageCodecs.Empty.Add(new FakeAvif()) };
        byte[] file = FakeAvif.File(10, 8)[..26];
        Assert.Throws<ImageFormatException>(() => Image.Decode(file, options));
    }

    [Theory]
    [InlineData(FakeAvifFailure.WrongSize)]
    [InlineData(FakeAvifFailure.WrongFormat)]
    [InlineData(FakeAvifFailure.BadHeader)]
    public void A_codec_that_breaks_its_promise_is_caught(FakeAvifFailure failure)
    {
        var options = new DecodeOptions { Codecs = ImageCodecs.Empty.Add(new FakeAvif { Fail = failure }), PixelFormat = PixelFormat.Rgba32 };
        Assert.Throws<ImageCodecException>(() => Image.Decode(FakeAvif.File(10, 8), options));
    }

    [Fact]
    public void The_pixel_format_asked_for_is_passed_on()
    {
        var options = new DecodeOptions { Codecs = ImageCodecs.Empty.Add(new FakeAvif()), PixelFormat = PixelFormat.Rgba64 };
        using Image image = Image.Decode(FakeAvif.File(4, 4), options);
        Assert.Equal(PixelFormat.Rgba64, image.PixelFormat);
    }

    [Fact]
    public void An_unavailable_codec_says_why()
    {
        var codecs = ImageCodecs.Empty.Add(new FakeAvif { Missing = "AVIF decoding needs the fake extension." });
        UnsupportedImageFormatException failure = Assert.Throws<UnsupportedImageFormatException>(
            () => Image.Decode(FakeAvif.File(10, 8), new DecodeOptions { Codecs = codecs }));
        Assert.Equal("AVIF decoding needs the fake extension.", failure.Message);

        IdentifyResult identified = Image.Identify(FakeAvif.File(10, 8), new IdentifyOptions { Codecs = codecs });
        Assert.Equal(IdentifyStatus.UnsupportedFormat, identified.Status);
        Assert.Equal("AVIF decoding needs the fake extension.", identified.Message);
        Assert.False(codecs.Supports(ImageFormat.Avif));
    }

    [Fact]
    public void The_first_available_codec_wins()
    {
        var missing = new FakeAvif { Missing = "Not here." };
        var second = new FakeAvif();
        using Image image = Image.Decode(FakeAvif.File(3, 3), new DecodeOptions { Codecs = ImageCodecs.Empty.Add(missing, second) });
        Assert.Equal(0, missing.Decodes);
        Assert.Equal(1, second.Decodes);
    }

    [Fact]
    public void Codecs_for_built_in_or_unknown_formats_are_refused()
    {
        Assert.Throws<ArgumentException>(() => ImageCodecs.Empty.Add(new FakeAvif { Claims = ImageFormat.Png }));
        Assert.Throws<ArgumentNullException>(() => ImageCodecs.Empty.Add([null!]));
        ImageCodecs one = ImageCodecs.Empty.Add(new FakeAvif());
        Assert.Single(one);
        Assert.Empty(ImageCodecs.Empty);
    }

    [Fact]
    public async Task The_pipeline_offers_the_codec_a_smaller_size()
    {
        var codec = new FakeAvif();
        var options = new DecodeOptions { Codecs = ImageCodecs.Empty.Add(codec) };
        using Image image = await ImagePipeline.Load(FakeAvif.File(400, 200)).WithDecodeOptions(options).Fit(100, 100)
            .ToImageAsync(TestContext.Current.CancellationToken);
        Assert.Equal((100, 50), (image.Width, image.Height));
        Assert.NotNull(codec.LastShrink);
    }

    [Fact]
    public void An_animation_reader_gives_the_still_image()
    {
        using AnimationReader reader = AnimationReader.Open(FakeAvif.File(6, 6), new DecodeOptions { Codecs = ImageCodecs.Empty.Add(new FakeAvif()) });
        using AnimationFrame? frame = reader.ReadFrame();
        Assert.Equal(6, frame!.Image.Width);
        Assert.Null(reader.ReadFrame());
    }

    [Fact]
    public async Task Cancellation_passes_through()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        using var stream = new MemoryStream(FakeAvif.File(4, 4));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await Image.DecodeAsync(stream, new DecodeOptions { Codecs = ImageCodecs.Empty.Add(new FakeAvif()) }, cancel.Token));
    }

    [Fact]
    public void ImageInfo_checks_its_arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageInfo(ImageFormat.Avif, -1, 1));
        Assert.Throws<ArgumentNullException>(() => new ImageInfo(null!, 1, 1));
        Assert.Equal("AVIF 3x2", new ImageInfo(ImageFormat.Avif, 2, 3) { Orientation = 6 }.ToString());
    }

    [Fact]
    public void CodecAvailability_needs_a_reason()
    {
        Assert.Throws<ArgumentException>(() => CodecAvailability.Unavailable(" "));
        Assert.Equal("Available", CodecAvailability.Available.ToString());
    }
}
