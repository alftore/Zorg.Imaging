using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Tests.Codecs.Png;
using static Zorg.Imaging.Tests.Codecs.Jpeg.JpegTestFiles;

namespace Zorg.Imaging.Tests.Codecs.Jpeg;

public class JpegDecoderTests
{
    public static TheoryData<string> Files() =>
    [
        "s420", "s444", "s422", "s440", "s411", "grey", "rgb", "progressive", "progressive-grey", "restart", "progressive-restart",
    ];

    public static TheoryData<string, int> Scaled()
    {
        var data = new TheoryData<string, int>();
        foreach (string name in new[] { "s420", "progressive", "grey" })
        {
            data.Add(name, 2);
            data.Add(name, 4);
            data.Add(name, 8);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Files_decode_as_libjpeg_turbo_decodes_them(string name)
    {
        (int width, int height, int channels, byte[] expected) = Pnm(name);
        using Image image = Image.Decode(Sample(name));
        Assert.Equal((width, height), (image.Width, image.Height));
        Assert.Equal(channels == 1 ? PixelFormat.L8 : PixelFormat.Rgb24, image.PixelFormat);
        Assert.Equal(ColorSpace.Srgb, image.ColorSpace);

        // Our float inverse DCT against libjpeg's integer one: a level or two apart at most.
        (int max, double mean) = Compare(image.GetPixelBytes(), expected);
        Assert.True(max <= 4 && mean < 0.6, $"{name}: largest difference {max}, mean {mean:0.###}");
    }

    [Theory]
    [MemberData(nameof(Scaled))]
    public void Scaled_decoding_matches_libjpeg_turbo(string name, int denominator)
    {
        (int width, int height, _, byte[] expected) = Pnm($"{name}-scale1-{denominator}");
        byte[] jpeg = Sample(name);

        // A target half the scaled size makes the decoder pick exactly that scale.
        var options = DecodeOptions.Default.WithShrinkTarget((w, h) => (width / 2, height / 2));
        using Image image = Image.Decode(jpeg, options);
        Assert.Equal((width, height), (image.Width, image.Height));

        (int max, double mean) = Compare(image.GetPixelBytes(), expected);
        Assert.True(max <= 8 && mean < 1, $"{name} 1/{denominator}: largest difference {max}, mean {mean:0.###}");
    }

    [Theory]
    [InlineData("grey", 0.6)]
    [InlineData("s444", 1.2)]
    [InlineData("s420", 1.3)]
    [InlineData("s422", 1.3)]
    [InlineData("s440", 1.3)]
    [InlineData("s411", 3)]
    public void Scaled_decoding_is_the_area_average_of_the_full_decode(string name, double meanLimit)
    {
        // The reduced transform averages each cell exactly; what's left is rounding, and for colour, that the full decode
        // upsamples chroma with a triangle filter before the average is taken (4:1:1 still needs a 2x step at 1/2).
        byte[] jpeg = Sample(name);
        using Image full = Image.Decode(jpeg);
        foreach (int cell in new[] { 2, 4, 8 })
        {
            int w = (full.Width + cell - 1) / cell;
            int h = (full.Height + cell - 1) / cell;
            using Image scaled = Image.Decode(jpeg, DecodeOptions.Default.WithShrinkTarget((_, _) => (w / 2, h / 2)));
            Assert.Equal((w, h), (scaled.Width, scaled.Height));

            byte[] box = AreaAverage(full, cell);
            (int max, double mean) = Compare(scaled.GetPixelBytes(), box);
            Assert.True(max <= 16 && mean < meanLimit, $"{name} 1/{cell}: largest difference {max}, mean {mean:0.###}");
        }
    }

    [Fact]
    public void Requested_formats_hold_the_same_colours()
    {
        byte[] jpeg = Sample("s420");
        using Image rgb = Image.Decode(jpeg);
        using Image rgba = Image.Decode(jpeg, new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        using Image bgra = Image.Decode(jpeg, new DecodeOptions { PixelFormat = PixelFormat.Bgra32 });
        using Image wide = Image.Decode(jpeg, new DecodeOptions { PixelFormat = PixelFormat.Rgba64 });
        for (int y = 0; y < rgb.Height; y += 7)
        {
            for (int x = 0; x < rgb.Width; x += 5)
            {
                Rgb24 p = rgb.GetRow<Rgb24>(y)[x];
                Assert.Equal(new Rgba32(p.R, p.G, p.B, 255), rgba.GetRow<Rgba32>(y)[x]);
                Assert.Equal(new Bgra32(p.B, p.G, p.R, 255), bgra.GetRow<Bgra32>(y)[x]);
                Assert.Equal(new Rgba64((ushort)(p.R * 257), (ushort)(p.G * 257), (ushort)(p.B * 257), 65535), wide.GetRow<Rgba64>(y)[x]);
            }
        }

        using Image grey = Image.Decode(Sample("grey"), new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        Rgba32 g = grey.GetRow<Rgba32>(3)[4];
        Assert.True(g.R == g.G && g.G == g.B && g.A == 255);
    }

    [Fact]
    public async Task Decoding_from_a_stream_that_cannot_seek_gives_the_same_pixels()
    {
        foreach (string name in Files())
        {
            byte[] jpeg = Sample(name);
            using Image fromBytes = Image.Decode(jpeg);
            using Image fromStream = await Image.DecodeAsync(new ForwardOnlyStream(jpeg), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(fromBytes.GetPixelBytes().ToArray(), fromStream.GetPixelBytes().ToArray());
        }
    }

    [Fact]
    public void Exif_orientation_is_applied()
    {
        byte[] jpeg = WithSegments(Sample("s420"), (0xE1, [.. "Exif\0\0"u8, .. PngTestFiles.ExifWithOrientation(6)]));
        using Image upright = Image.Decode(jpeg);
        using Image stored = Image.Decode(jpeg, new DecodeOptions { ApplyOrientation = false });
        Assert.Equal((45, 67), (upright.Width, upright.Height));
        Assert.Equal((67, 45), (stored.Width, stored.Height));
        Assert.Equal(stored.GetRow<Rgb24>(44)[0], upright.GetRow<Rgb24>(0)[0]);
        Assert.Equal(1, Image.Identify(Sample("s420")).Info!.Orientation);
        Assert.Equal(6, Image.Identify(jpeg).Info!.Orientation);
    }

    [Fact]
    public void An_icc_profile_in_several_chunks_is_reassembled()
    {
        byte[] profile = PngColourTests.FakeProfile(1500, seed: 4);
        byte[] first = [.. "ICC_PROFILE\0"u8, 1, 2, .. profile[..1000]];
        byte[] second = [.. "ICC_PROFILE\0"u8, 2, 2, .. profile[1000..]];

        // Out of order on purpose: chunks are put together by their numbers.
        using Image image = Image.Decode(WithSegments(Sample("s444"), (0xE2, second), (0xE2, first)));
        Assert.Equal(profile, image.ColorSpace.IccProfile.ToArray());

        Assert.Throws<ImageFormatException>(() => Image.Decode(WithSegments(Sample("s444"), (0xE2, first))));
        Assert.Throws<ImageFormatException>(() => Image.Decode(WithSegments(Sample("s444"), (0xE2, first), (0xE2, first))));
        byte[] notAProfile = [.. "ICC_PROFILE\0"u8, 1, 1, .. new byte[200]];
        Assert.Throws<ImageFormatException>(() => Image.Decode(WithSegments(Sample("s444"), (0xE2, notAProfile))));
    }

    [Fact]
    public void An_oversized_icc_profile_is_refused()
    {
        var segments = new List<(int, byte[])>();
        for (int i = 0; i < 70; i++)
        {
            segments.Add((0xE2, [.. "ICC_PROFILE\0"u8, (byte)(i + 1), 70, .. new byte[65000]]));
        }

        Assert.Throws<ImageLimitException>(() => Image.Decode(WithSegments(Sample("grey"), [.. segments])));
    }

    [Fact]
    public void Every_truncation_throws_an_image_format_exception()
    {
        byte[] jpeg = Sample("progressive");
        for (int length = 0; length < jpeg.Length - 2; length++)
        {
            Exception? error = Record.Exception(() => Image.Decode(jpeg[..length]).Dispose());
            Assert.True(error is ImageFormatException or UnsupportedImageFormatException, $"cut to {length}: {error}");
        }
    }

    [Fact]
    public void Formats_this_decoder_does_not_handle_say_so()
    {
        byte[] jpeg = Sample("grey");
        (int sof, _) = Segments(jpeg).First(s => s.Marker == 0xC0);
        foreach ((byte marker, string words) in new[] { ((byte)0xC3, "Lossless"), ((byte)0xC9, "Arithmetic"), ((byte)0xC5, "Hierarchical") })
        {
            byte[] copy = (byte[])jpeg.Clone();
            copy[sof + 1] = marker;
            UnsupportedImageFormatException error = Assert.Throws<UnsupportedImageFormatException>(() => Image.Decode(copy));
            Assert.Contains(words, error.Message, StringComparison.Ordinal);
            Assert.Same(ImageFormat.Jpeg, error.Format);
        }

        byte[] twelveBit = (byte[])jpeg.Clone();
        twelveBit[sof + 4] = 12;
        Assert.Throws<UnsupportedImageFormatException>(() => Image.Decode(twelveBit));

        // A CMYK frame header: four components.
        byte[] cmyk = [0xFF, 0xD8, 0xFF, 0xC0, 0, 20, 8, 0, 8, 0, 8, 4, 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0, 4, 0x11, 0, 0xFF, 0xD9];
        Assert.Contains("CMYK", Assert.Throws<UnsupportedImageFormatException>(() => Image.Decode(cmyk)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_frame_over_MaxPixels_is_refused_before_anything_is_allocated()
    {
        byte[] jpeg = Sample("grey");
        (int sof, _) = Segments(jpeg).First(s => s.Marker == 0xC0);
        jpeg[sof + 5] = 0xFF;
        jpeg[sof + 6] = 0xFF;
        jpeg[sof + 7] = 0xFF;
        jpeg[sof + 8] = 0xFF;

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ImageLimitException>(() => Image.Decode(jpeg, new DecodeOptions { Limits = new ImageLimits { MaxPixels = 1_000_000 } }));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_000_000);
    }

    [Fact]
    public void Too_many_scans_are_refused()
    {
        // An 8 x 8 grey progressive image: a DC first scan, then refinement scans of one bit each, far too many.
        var file = new List<byte> { 0xFF, 0xD8 };
        void Segment(byte marker, params byte[] data)
        {
            file.AddRange([0xFF, marker, (byte)((data.Length + 2) >> 8), (byte)(data.Length + 2)]);
            file.AddRange(data);
        }

        Segment(0xDB, [0, .. Enumerable.Repeat((byte)1, 64)]);
        Segment(0xC2, 8, 0, 8, 0, 8, 1, 1, 0x11, 0);
        Segment(0xC4, [0x00, 1, .. new byte[15], 0]);
        Segment(0xDA, 1, 1, 0x00, 0, 0, 0x01);
        file.Add(0x00);
        for (int i = 0; i < 300; i++)
        {
            Segment(0xDA, 1, 1, 0x00, 0, 0, 0x10);
            file.Add(0x00);
        }

        file.AddRange([0xFF, 0xD9]);
        Assert.Throws<ImageLimitException>(() => Image.Decode([.. file]));

        // The same with a few refinement scans decodes.
        int keep = file.Count - (290 * 11) - 2;
        using Image image = Image.Decode([.. file.Take(keep), 0xFF, 0xD9]);
        Assert.Equal((8, 8), (image.Width, image.Height));
    }

    [Fact]
    public void Random_damage_throws_only_our_exceptions_and_never_hangs()
    {
        var small = new DecodeOptions { Limits = new ImageLimits { MaxPixels = 1_000_000 } };
        foreach (string name in Files())
        {
            byte[] jpeg = Sample(name);
            var random = new Random(name.Length);
            for (int round = 0; round < 600; round++)
            {
                byte[] copy = (byte[])jpeg.Clone();
                int flips = 1 + random.Next(4);
                for (int f = 0; f < flips; f++)
                {
                    int at = random.Next(copy.Length);
                    copy[at] = random.Next(3) == 0 ? (byte)random.Next(256) : (byte)(copy[at] ^ (1 << random.Next(8)));
                }

                long started = Stopwatch.GetTimestamp();
                Exception? error = Record.Exception(() => Image.Decode(copy, small).Dispose());
                TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
                Assert.True(error is null or ImageException, $"{name} round {round}: {error}");
                Assert.True(elapsed < TimeSpan.FromSeconds(2), $"{name} round {round} took {elapsed}");
            }
        }
    }

    [Fact]
    public async Task The_pipeline_shrinks_on_load_and_still_fits_exactly()
    {
        byte[] jpeg = Sample("s420");
        using Image piped = await ImagePipeline.Load(jpeg).Fit(16, 16).ToImageAsync(TestContext.Current.CancellationToken);

        // 67 x 45 fitted to 16 x 11: 1/2 scale (34 x 23) is still twice that, 1/4 (17 x 12) isn't.
        using Image half = Image.Decode(jpeg, DecodeOptions.Default.WithShrinkTarget((w, h) => (16, 11)));
        Assert.Equal((34, 23), (half.Width, half.Height));
        using Image expected = half.Fit(16, 16);
        Assert.Equal((16, 11), (piped.Width, piped.Height));
        Assert.Equal(expected.GetPixelBytes().ToArray(), piped.GetPixelBytes().ToArray());

        // With no resize, or a crop before it, the decode is at full size.
        using Image turned = await ImagePipeline.Load(jpeg).Rotate(90).ToImageAsync(TestContext.Current.CancellationToken);
        Assert.Equal((45, 67), (turned.Width, turned.Height));
        using Image full = Image.Decode(jpeg);
        using Image cropped = full.Crop(0, 0, 60, 40);
        using Image cropFit = cropped.Fit(16, 16);
        using Image pipedCrop = await ImagePipeline.Load(jpeg).Crop(0, 0, 60, 40).Fit(16, 16).ToImageAsync(TestContext.Current.CancellationToken);
        Assert.Equal(cropFit.GetPixelBytes().ToArray(), pipedCrop.GetPixelBytes().ToArray());
    }

    [Fact]
    public async Task The_pipeline_shrinks_on_load_through_turns_flips_and_flattening()
    {
        byte[] jpeg = Sample("s420");
        using Image piped = await ImagePipeline.Load(jpeg)
            .Rotate(90)
            .FlipHorizontal()
            .Flatten(new Rgba32(255, 255, 255))
            .Fit(16, 16)
            .ToImageAsync(TestContext.Current.CancellationToken);

        // Turned, 45 x 67 fits to 11 x 16, which is 16 x 11 in the file's own orientation: 1/2 scale again.
        using Image half = Image.Decode(jpeg, DecodeOptions.Default.WithShrinkTarget((w, h) => (16, 11)));
        using Image turned = half.Rotate(90);
        using Image mirrored = turned.FlipHorizontal();
        using Image flat = mirrored.Flatten(new Rgba32(255, 255, 255));
        using Image expected = flat.Fit(16, 16);
        Assert.Equal((11, 16), (piped.Width, piped.Height));
        Assert.Equal(expected.GetPixelBytes().ToArray(), piped.GetPixelBytes().ToArray());
    }
}
