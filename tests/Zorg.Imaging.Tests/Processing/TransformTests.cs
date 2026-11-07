using System;
using System.Runtime.InteropServices;
using Xunit;
using Zorg.Imaging.Tests.Codecs.Png;

namespace Zorg.Imaging.Tests.Processing;

public class TransformTests
{
    [Fact]
    public void Crop_copies_the_rectangle()
    {
        using Image image = Numbered(5, 4);
        image.ColorSpace = ColorSpace.DisplayP3;
        using Image part = image.Crop(1, 2, 3, 2);
        Assert.Equal((3, 2), (part.Width, part.Height));
        Assert.Equal([11, 12, 13, 16, 17, 18], part.GetPixelBytes().ToArray());
        Assert.Equal(ColorSpace.DisplayP3, part.ColorSpace);
        using Image whole = image.Crop(0, 0, 5, 4);
        Assert.Equal(image.GetPixelBytes().ToArray(), whole.GetPixelBytes().ToArray());
    }

    [Fact]
    public void Crop_outside_the_image_is_refused()
    {
        using Image image = Numbered(5, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Crop(-1, 0, 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Crop(0, 0, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Crop(4, 0, 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Crop(0, 3, 1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Crop(int.MaxValue, 0, int.MaxValue, 1));
    }

    [Fact]
    public void Crop_works_in_every_format()
    {
        foreach (PixelFormat format in ResizeTests.Formats())
        {
            using Image image = PngEncoderTests.Smooth(7, 6, format, seed: 2);
            using Image part = image.Crop(2, 1, 4, 3);
            for (int y = 0; y < 3; y++)
            {
                Assert.Equal(image.GetRowBytes(y + 1).Slice(2 * image.BytesPerPixel, 4 * image.BytesPerPixel).ToArray(), part.GetRowBytes(y).ToArray());
            }
        }
    }

    [Theory]
    [InlineData(4000, 3000, 1600, 1600, 1600, 1200)]
    [InlineData(3000, 4000, 1600, 1600, 1200, 1600)]
    [InlineData(1000, 667, 300, 300, 300, 200)]
    [InlineData(1200, 900, 1600, 1600, 1200, 900)]
    [InlineData(5000, 10, 100, 100, 100, 1)]
    [InlineData(800, 600, 800, 100, 133, 100)]
    public void Fit_shrinks_inside_the_box_and_never_enlarges(int width, int height, int boxWidth, int boxHeight, int expectedWidth, int expectedHeight)
    {
        using var image = new Image(width, height, PixelFormat.L8);
        using Image fitted = image.Fit(boxWidth, boxHeight);
        Assert.Equal((expectedWidth, expectedHeight), (fitted.Width, fitted.Height));
    }

    [Fact]
    public void Fill_covers_the_box_and_crops_the_centre()
    {
        // A wide image: left third red, middle green, right third blue. Filled to a square, only the middle shows.
        using var image = new Image(300, 100, PixelFormat.Rgb24);
        for (int y = 0; y < 100; y++)
        {
            Span<Rgb24> row = image.GetRow<Rgb24>(y);
            for (int x = 0; x < 300; x++)
            {
                row[x] = x < 100 ? new Rgb24(255, 0, 0) : x < 200 ? new Rgb24(0, 255, 0) : new Rgb24(0, 0, 255);
            }
        }

        using Image square = image.Fill(50, 50);
        Assert.Equal((50, 50), (square.Width, square.Height));
        foreach (Rgb24 pixel in MemoryMarshal.Cast<byte, Rgb24>(square.GetPixelBytes()))
        {
            Assert.Equal(new Rgb24(0, 255, 0), pixel);
        }

        using Image larger = image.Fill(400, 1000);
        Assert.Equal((400, 1000), (larger.Width, larger.Height));
        using Image same = image.Fill(30, 10);
        Assert.Equal((30, 10), (same.Width, same.Height));
    }

    [Fact]
    public void Pad_centres_the_fitted_image_on_the_background()
    {
        using var image = new Image(40, 20, PixelFormat.Rgba32);
        image.GetPixelBytes().Fill(200);
        using Image tile = image.Pad(10, 10, new Rgba32(1, 2, 3, 4));
        Assert.Equal((10, 10), (tile.Width, tile.Height));
        Assert.Equal(new Rgba32(1, 2, 3, 4), tile.GetRow<Rgba32>(0)[5]);
        // Fitted to 10 x 5, so rows 2 to 6 are the image.
        Assert.Equal(new Rgba32(1, 2, 3, 4), tile.GetRow<Rgba32>(1)[5]);
        Assert.Equal(new Rgba32(200, 200, 200, 200), tile.GetRow<Rgba32>(2)[0]);
        Assert.Equal(new Rgba32(200, 200, 200, 200), tile.GetRow<Rgba32>(6)[9]);
        Assert.Equal(new Rgba32(1, 2, 3, 4), tile.GetRow<Rgba32>(7)[5]);

        // Smaller than the box: not enlarged, just centred.
        using var small = new Image(2, 2, PixelFormat.L8);
        small.GetPixelBytes().Fill(9);
        using Image padded = small.Pad(4, 4, new Rgba32(255, 255, 255));
        Assert.Equal([255, 255, 255, 255, 255, 9, 9, 255, 255, 9, 9, 255, 255, 255, 255, 255], padded.GetPixelBytes().ToArray());
    }

    [Fact]
    public void Pad_converts_the_background_to_each_format()
    {
        var colour = new Rgba32(10, 200, 30, 40);
        (PixelFormat Format, byte[] Pixel)[] cases =
        [
            (PixelFormat.Rgba32, [10, 200, 30, 40]),
            (PixelFormat.Bgra32, [30, 200, 10, 40]),
            (PixelFormat.Rgb24, [10, 200, 30]),
            (PixelFormat.L8, [(byte)(((2126 * 10) + (7152 * 200) + (722 * 30) + 5000) / 10000)]),
        ];
        foreach ((PixelFormat format, byte[] pixel) in cases)
        {
            using var image = new Image(1, 1, format);
            using Image padded = image.Pad(3, 1, colour);
            Assert.Equal(pixel, padded.GetRowBytes(0)[..pixel.Length].ToArray());
        }

        using var wide = new Image(1, 1, PixelFormat.Rgba64);
        using Image wideTile = wide.Pad(3, 1, colour);
        Assert.Equal(new Rgba64(10 * 257, 200 * 257, 30 * 257, 40 * 257), wideTile.GetRow<Rgba64>(0)[0]);
        using var rgb48 = new Image(1, 1, PixelFormat.Rgb48);
        using Image rgb48Tile = rgb48.Pad(3, 1, colour);
        Assert.Equal(new Rgb48(10 * 257, 200 * 257, 30 * 257), rgb48Tile.GetRow<Rgb48>(0)[2]);
        using var grey = new Image(1, 1, PixelFormat.L16);
        using Image greyTile = grey.Pad(1, 3, colour);
        Assert.Equal(cases[3].Pixel[0] * 257, greyTile.GetRow<L16>(2)[0].L);
    }

    [Fact]
    public void Rotate_turns_clockwise_by_quarter_turns()
    {
        using Image image = Numbered(3, 2); // 0 1 2 / 3 4 5
        using Image quarter = image.Rotate(90);
        using Image half = image.Rotate(180);
        using Image back = image.Rotate(-90);
        using Image full = image.Rotate(360);
        Assert.Equal((2, 3), (quarter.Width, quarter.Height));
        Assert.Equal([3, 0, 4, 1, 5, 2], quarter.GetPixelBytes().ToArray());
        Assert.Equal([5, 4, 3, 2, 1, 0], half.GetPixelBytes().ToArray());
        Assert.Equal([2, 5, 1, 4, 0, 3], back.GetPixelBytes().ToArray());
        Assert.Equal(image.GetPixelBytes().ToArray(), full.GetPixelBytes().ToArray());
        using Image twice = quarter.Rotate(270);
        Assert.Equal(image.GetPixelBytes().ToArray(), twice.GetPixelBytes().ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Rotate(45));
    }

    [Fact]
    public void Flip_mirrors()
    {
        using Image image = Numbered(3, 2);
        image.ColorSpace = ColorSpace.Rec2020Pq;
        using Image across = image.FlipHorizontal();
        using Image down = image.FlipVertical();
        Assert.Equal([2, 1, 0, 5, 4, 3], across.GetPixelBytes().ToArray());
        Assert.Equal([3, 4, 5, 0, 1, 2], down.GetPixelBytes().ToArray());
        Assert.Equal(ColorSpace.Rec2020Pq, down.ColorSpace);
    }

    [Fact]
    public void Flatten_draws_the_image_over_the_background()
    {
        using var image = new Image(3, 1, PixelFormat.Rgba32) { ColorSpace = ColorSpace.DisplayP3 };
        Span<Rgba32> row = image.GetRow<Rgba32>(0);
        row[0] = new Rgba32(100, 150, 200, 255);
        row[1] = new Rgba32(100, 150, 200, 0);
        row[2] = new Rgba32(0, 0, 0, 128);
        using Image flat = image.Flatten(new Rgba32(255, 255, 255, 0));
        Assert.Equal(PixelFormat.Rgb24, flat.PixelFormat);
        Assert.Equal(ColorSpace.DisplayP3, flat.ColorSpace);
        Assert.Equal([100, 150, 200, 255, 255, 255, 127, 127, 127], flat.GetPixelBytes().ToArray());

        using var bgra = new Image(1, 1, PixelFormat.Bgra32);
        bgra.GetRow<Bgra32>(0)[0] = new Bgra32(30, 20, 10, 255);
        using Image flatBgra = bgra.Flatten(default);
        Assert.Equal(new Rgb24(10, 20, 30), flatBgra.GetRow<Rgb24>(0)[0]);

        using var wide = new Image(1, 1, PixelFormat.Rgba64);
        wide.GetRow<Rgba64>(0)[0] = new Rgba64(65535, 0, 1000, 32768);
        using Image flatWide = wide.Flatten(new Rgba32(0, 255, 0));
        Assert.Equal(PixelFormat.Rgb48, flatWide.PixelFormat);
        Assert.Equal(new Rgb48(32768, 32767, 500), flatWide.GetRow<Rgb48>(0)[0]);

        using var opaque = new Image(2, 1, PixelFormat.Rgb24);
        opaque.GetPixelBytes().Fill(7);
        using Image copy = opaque.Flatten(default);
        Assert.Equal(opaque.GetPixelBytes().ToArray(), copy.GetPixelBytes().ToArray());
    }

    [Fact]
    public void Disposed_images_are_refused()
    {
        Image image = Numbered(3, 2);
        image.Dispose();
        Assert.Throws<ObjectDisposedException>(() => image.Crop(0, 0, 1, 1));
        Assert.Throws<ObjectDisposedException>(() => image.Fit(1, 1));
        Assert.Throws<ObjectDisposedException>(() => image.Fill(1, 1));
        Assert.Throws<ObjectDisposedException>(() => image.Pad(1, 1, default));
        Assert.Throws<ObjectDisposedException>(() => image.Rotate(90));
        Assert.Throws<ObjectDisposedException>(() => image.FlipHorizontal());
        Assert.Throws<ObjectDisposedException>(() => image.FlipVertical());
        Assert.Throws<ObjectDisposedException>(() => image.Flatten(default));
    }

    [Fact]
    public void Boxes_smaller_than_a_pixel_are_refused()
    {
        using Image image = Numbered(3, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Fit(0, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Fill(5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Pad(0, 0, default));
    }

    // An L8 image whose pixels are 0, 1, 2... row after row.
    private static Image Numbered(int width, int height)
    {
        var image = new Image(width, height, PixelFormat.L8);
        Span<byte> all = image.GetPixelBytes();
        for (int i = 0; i < all.Length; i++)
        {
            all[i] = (byte)i;
        }

        return image;
    }
}
