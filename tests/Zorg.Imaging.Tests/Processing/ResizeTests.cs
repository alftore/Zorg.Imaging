using System;
using System.Runtime.InteropServices;
using Xunit;
using Zorg.Imaging.Tests.Codecs.Png;

namespace Zorg.Imaging.Tests.Processing;

public class ResizeTests
{
    public static TheoryData<PixelFormat> Formats() =>
    [
        PixelFormat.Rgba32, PixelFormat.Bgra32, PixelFormat.Rgb24, PixelFormat.L8,
        PixelFormat.Rgba64, PixelFormat.Rgb48, PixelFormat.L16,
    ];

    public static TheoryData<string> Filters() => ["Nearest", "Bilinear", "Catmull-Rom", "Mitchell", "Lanczos3"];

    [Fact]
    public void A_zero_side_keeps_the_aspect_ratio_rounded_to_the_nearest_pixel()
    {
        using var image = new Image(1000, 667, PixelFormat.Rgb24);
        using Image wide = image.Resize(300, 0);
        using Image tall = image.Resize(0, 100);
        using Image tiny = image.Resize(1, 0);
        Assert.Equal((300, 200), (wide.Width, wide.Height));
        Assert.Equal((150, 100), (tall.Width, tall.Height));
        Assert.Equal((1, 1), (tiny.Width, tiny.Height));
    }

    [Fact]
    public void Bad_sizes_are_refused()
    {
        using var image = new Image(4, 4, PixelFormat.Rgba32);
        Assert.Throws<ArgumentException>(() => image.Resize(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Resize(-1, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Resize(4, -1));
        using var thin = new Image(1, 2, PixelFormat.L8);
        Assert.Throws<ArgumentOutOfRangeException>(() => thin.Resize(int.MaxValue, 0));
    }

    [Fact]
    public void A_disposed_image_is_refused()
    {
        var image = new Image(4, 4, PixelFormat.Rgba32);
        image.Dispose();
        Assert.Throws<ObjectDisposedException>(() => image.Resize(2, 2));
    }

    [Fact]
    public void The_same_size_is_an_exact_copy()
    {
        using Image image = PngEncoderTests.Smooth(9, 7, PixelFormat.Rgba32, seed: 1);
        image.ColorSpace = ColorSpace.DisplayP3;
        using Image copy = image.Resize(9, 7, Resampler.Lanczos3);
        Assert.Equal(image.GetPixelBytes().ToArray(), copy.GetPixelBytes().ToArray());
        Assert.Equal(ColorSpace.DisplayP3, copy.ColorSpace);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Format_and_colour_space_are_kept(PixelFormat format)
    {
        using var image = new Image(20, 10, format) { ColorSpace = ColorSpace.Rec2020Pq };
        using Image smaller = image.Resize(7, 3);
        using Image larger = image.Resize(33, 21);
        Assert.Equal((format, 7, 3), (smaller.PixelFormat, smaller.Width, smaller.Height));
        Assert.Equal((format, 33, 21), (larger.PixelFormat, larger.Width, larger.Height));
        Assert.Equal(ColorSpace.Rec2020Pq, smaller.ColorSpace);
        Assert.Equal(ColorSpace.Rec2020Pq, larger.ColorSpace);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void A_flat_colour_stays_exactly_the_same_with_every_filter(PixelFormat format)
    {
        ColorSpace[] spaces = [ColorSpace.Srgb, ColorSpace.DisplayP3, ColorSpace.Rec2020Pq, ColorSpace.Rec2020Hlg, ColorSpace.LinearSrgb, ColorSpace.FromCicp(1, 1)];
        foreach (ColorSpace space in spaces)
        {
            using var image = new Image(37, 23, format) { ColorSpace = space };
            byte[] pixel = new byte[image.BytesPerPixel];
            new Random(image.BytesPerPixel).NextBytes(pixel);
            if (format is PixelFormat.Rgba32 or PixelFormat.Bgra32)
            {
                pixel[3] = 255;
            }
            else if (format == PixelFormat.Rgba64)
            {
                pixel[6] = pixel[7] = 255;
            }

            Fill(image, pixel);
            foreach (string name in Filters())
            {
                foreach ((int w, int h) in new[] { (5, 4), (36, 23), (80, 61), (12, 50) })
                {
                    using Image resized = image.Resize(w, h, Filter(name));
                    for (int y = 0; y < h; y++)
                    {
                        Span<byte> row = resized.GetRowBytes(y);
                        for (int x = 0; x < w; x++)
                        {
                            Assert.True(row.Slice(x * pixel.Length, pixel.Length).SequenceEqual(pixel), $"{space} {name} {w}x{h} at {x},{y}");
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void Shrinking_averages_in_linear_light()
    {
        // Black and white average to half the light, which is sRGB 188, not 128.
        using var image = new Image(2, 1, PixelFormat.Rgb24);
        image.GetRow<Rgb24>(0)[1] = new Rgb24(255, 255, 255);
        foreach (string name in new[] { "Bilinear", "Catmull-Rom", "Mitchell", "Lanczos3" })
        {
            using Image half = image.Resize(1, 1, Filter(name));
            Assert.Equal(new Rgb24(188, 188, 188), half.GetRow<Rgb24>(0)[0]);
        }

        image.ColorSpace = ColorSpace.LinearSrgb;
        using Image linear = image.Resize(1, 1);
        Assert.Equal(new Rgb24(128, 128, 128), linear.GetRow<Rgb24>(0)[0]);
    }

    [Fact]
    public void Shrinking_uses_the_images_own_transfer_curve()
    {
        // PQ: half of 10,000 cd/m² is code 0.92655 (ST 2084), 60721 of 65535.
        using var image = new Image(2, 1, PixelFormat.L16) { ColorSpace = ColorSpace.Rec2020Pq };
        image.GetRow<L16>(0)[1] = new L16(65535);
        using Image half = image.Resize(1, 1);
        Assert.InRange(half.GetRow<L16>(0)[0].L, 60720, 60722);

        // A PNG gamma of 1/1.8.
        using var gamma = new Image(2, 1, PixelFormat.L8) { ColorSpace = ColorSpace.FromPngGamma(55556, null) };
        gamma.GetRow<L8>(0)[1] = new L8(255);
        using Image halfGamma = gamma.Resize(1, 1);
        Assert.Equal(Math.Round(Math.Pow(0.5, 1 / 1.8) * 255), halfGamma.GetRow<L8>(0)[0].L);
    }

    [Fact]
    public void Enlarging_blends_the_stored_values()
    {
        using var image = new Image(2, 1, PixelFormat.L8);
        image.GetRow<L8>(0)[1] = new L8(255);
        using Image wide = image.Resize(4, 1, Resampler.Bilinear);
        Assert.Equal([0, 64, 191, 255], wide.GetPixelBytes().ToArray());
    }

    [Fact]
    public void Transparent_pixels_lend_no_colour()
    {
        // Opaque blue beside transparent green: no green may reach a pixel that shows.
        using var image = new Image(16, 4, PixelFormat.Rgba32);
        for (int y = 0; y < 4; y++)
        {
            Span<Rgba32> row = image.GetRow<Rgba32>(y);
            for (int x = 0; x < 16; x++)
            {
                row[x] = x < 8 ? new Rgba32(0, 0, 255, 255) : new Rgba32(0, 255, 0, 0);
            }
        }

        foreach (string name in new[] { "Bilinear", "Catmull-Rom", "Mitchell", "Lanczos3" })
        {
            foreach ((int w, int h) in new[] { (5, 2), (40, 9) })
            {
                using Image resized = image.Resize(w, h, Filter(name));
                foreach (Rgba32 pixel in MemoryMarshal.Cast<byte, Rgba32>(resized.GetPixelBytes()))
                {
                    Assert.True(pixel.A == 0 ? pixel == default : pixel.G == 0 && pixel.B == 255, $"{name} {w}x{h}: {pixel}");
                }
            }
        }
    }

    [Fact]
    public void Nearest_picks_source_pixels_by_their_centres()
    {
        using var image = new Image(4, 1, PixelFormat.L8);
        "\u000a\u0014\u001e("u8.ToArray().CopyTo(image.GetPixelBytes());
        using Image half = image.Resize(2, 1, Resampler.Nearest);
        using Image twice = image.Resize(8, 1, Resampler.Nearest);
        Assert.Equal([20, 40], half.GetPixelBytes().ToArray());
        Assert.Equal([10, 10, 20, 20, 30, 30, 40, 40], twice.GetPixelBytes().ToArray());
    }

    [Fact]
    public void Nearest_makes_no_new_colours_in_any_format()
    {
        foreach (PixelFormat format in Formats())
        {
            using Image image = PngEncoderTests.Smooth(13, 11, format, seed: 3);
            using Image resized = image.Resize(29, 5, Resampler.Nearest);
            int size = image.BytesPerPixel;
            var colours = new System.Collections.Generic.HashSet<string>();
            byte[] all = image.GetPixelBytes().ToArray();
            for (int i = 0; i < all.Length; i += size)
            {
                colours.Add(Convert.ToHexString(all, i, size));
            }

            byte[] result = resized.GetPixelBytes().ToArray();
            for (int i = 0; i < result.Length; i += size)
            {
                Assert.Contains(Convert.ToHexString(result, i, size), colours);
            }
        }
    }

    [Fact]
    public void A_huge_shrink_needs_only_a_few_rows_of_memory()
    {
        using var image = new Image(3000, 3000, PixelFormat.Rgba32);
        Fill(image, [10, 20, 30, 255]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        using Image strip = image.Resize(3000, 1);
        using Image dot = image.Resize(1, 1);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 2_000_000, $"{allocated} bytes allocated");
        Assert.Equal(new Rgba32(10, 20, 30, 255), dot.GetRow<Rgba32>(0)[0]);
        Assert.Equal(new Rgba32(10, 20, 30, 255), strip.GetRow<Rgba32>(0)[2999]);
    }

    [Fact]
    public void Ringing_is_clamped_to_the_range()
    {
        using var image = new Image(9, 1, PixelFormat.L16);
        Span<L16> row = image.GetRow<L16>(0);
        for (int x = 4; x < 9; x++)
        {
            row[x] = new L16(65535);
        }

        using Image larger = image.Resize(40, 1, Resampler.Lanczos3);
        ReadOnlySpan<L16> values = larger.GetRow<L16>(0);
        Assert.Equal(0, values[0].L);
        Assert.Equal(65535, values[39].L);
    }

    [Fact]
    public void Resamplers_describe_themselves()
    {
        Assert.Equal("Lanczos3", Resampler.Lanczos3.ToString());
        Assert.Equal(3, Resampler.Lanczos3.Radius);
        Assert.Equal(2, Resampler.Mitchell.Radius);
        Assert.Equal("Catmull-Rom", Resampler.CatmullRom.Name);
        Assert.Equal(1, Resampler.Bilinear.Radius);
        Assert.Equal(0.5, Resampler.Nearest.Radius);
    }

    internal static Resampler Filter(string name) => name switch
    {
        "Nearest" => Resampler.Nearest,
        "Bilinear" => Resampler.Bilinear,
        "Catmull-Rom" => Resampler.CatmullRom,
        "Mitchell" => Resampler.Mitchell,
        _ => Resampler.Lanczos3,
    };

    private static void Fill(Image image, ReadOnlySpan<byte> pixel)
    {
        Span<byte> all = image.GetPixelBytes();
        for (int i = 0; i < all.Length; i += pixel.Length)
        {
            pixel.CopyTo(all[i..]);
        }
    }
}
