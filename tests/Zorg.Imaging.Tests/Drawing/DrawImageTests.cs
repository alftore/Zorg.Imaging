using System;
using System.Numerics;
using Xunit;
using Zorg.Imaging.Drawing;
using Zorg.Imaging.Tests.Codecs.Png;

namespace Zorg.Imaging.Tests.Drawing;

public class DrawImageTests
{
    private static Image Background(int width, int height)
    {
        var image = new Image(width, height, PixelFormat.Rgba32);
        using var canvas = new Canvas(image);
        canvas.Clear(new Rgba32(240, 230, 200));
        return image;
    }

    private static void Near(Image expected, Image actual, int tolerance)
    {
        Assert.Equal((expected.Width, expected.Height, expected.PixelFormat), (actual.Width, actual.Height, actual.PixelFormat));
        byte[] a = expected.GetPixelBytes().ToArray(), b = actual.GetPixelBytes().ToArray();
        for (int i = 0; i < a.Length; i++)
        {
            Assert.True(Math.Abs(a[i] - b[i]) <= tolerance, $"Byte {i} (pixel {i / 4 % expected.Width}, {i / 4 / expected.Width}): {a[i]} against {b[i]}.");
        }
    }

    [Fact]
    public void An_image_at_whole_pixels_and_its_own_size_is_what_Image_DrawImage_gives()
    {
        using Image overlay = PngEncoderTests.Smooth(20, 14, PixelFormat.Rgba32, seed: 3);
        using Image background = Background(40, 30);
        using Image expected = background.DrawImage(overlay, 7, 9, opacity: 0.8);
        using var canvas = new Canvas(background);
        canvas.DrawImage(overlay, 7, 9, 20, 14, opacity: 0.8);
        Near(expected, background, 1);
    }

    [Fact]
    public void Shrinking_resizes_first()
    {
        using Image photo = JpegPhoto(64, 48);
        using Image smaller = photo.Resize(16, 12);
        using Image background = Background(30, 20);
        using Image expected = background.DrawImage(smaller, 5, 4);
        using var canvas = new Canvas(background);
        canvas.DrawImage(photo, 5, 4, 16, 12);
        Near(expected, background, 1);
    }

    [Fact]
    public void Nearest_enlarges_pixels_into_blocks()
    {
        using Image photo = JpegPhoto(6, 4);
        using var target = new Image(18, 12, PixelFormat.Rgba32);
        using var canvas = new Canvas(target);
        canvas.DrawImage(photo, 0, 0, 18, 12, Resampler.Nearest);
        for (int y = 0; y < 12; y++)
        {
            for (int x = 0; x < 18; x++)
            {
                Rgb24 p = photo.GetRow<Rgb24>(y / 3)[x / 3];
                Assert.Equal(new Rgba32(p.R, p.G, p.B), target.GetRow<Rgba32>(y)[x]);
            }
        }
    }

    [Fact]
    public void A_quarter_turn_is_what_Rotate_gives()
    {
        using Image photo = JpegPhoto(12, 8);
        using Image turned = photo.Rotate(90);
        using Image background = Background(20, 20);
        using Image expected = background.DrawImage(turned, 3, 5);
        using var canvas = new Canvas(background);

        // Clockwise on screen (y down): (x, y) goes to (height - y, x), then moved to (3, 5).
        canvas.DrawImage(photo, new Matrix3x2(0, 1, -1, 0, 8, 0) * Matrix3x2.CreateTranslation(3, 5));
        Near(expected, background, 1);
    }

    [Fact]
    public void Edges_are_anti_aliased()
    {
        using var white = new Image(4, 4, PixelFormat.Rgb24);
        white.GetPixelBytes().Fill(255);
        using var target = new Image(10, 6, PixelFormat.Rgba32);
        using var canvas = new Canvas(target);
        canvas.DrawImage(white, 2.5f, 1, 4, 4);
        Assert.Equal(0, target.GetRow<Rgba32>(2)[1].A);
        Assert.InRange(target.GetRow<Rgba32>(2)[2].A, 127, 128); // half covered
        Assert.Equal(255, target.GetRow<Rgba32>(2)[3].A);
        Assert.InRange(target.GetRow<Rgba32>(2)[6].A, 127, 128);
    }

    [Fact]
    public void Transparent_pixels_lend_no_colour()
    {
        // Blue beside transparent red: enlarged, the blend between them fades blue out without turning purple.
        using var image = new Image(2, 1, PixelFormat.Rgba32);
        image.GetRow<Rgba32>(0)[0] = new Rgba32(0, 0, 255, 255);
        image.GetRow<Rgba32>(0)[1] = new Rgba32(255, 0, 0, 0);
        using var target = new Image(20, 1, PixelFormat.Rgba32);
        using var canvas = new Canvas(target);
        canvas.DrawImage(image, 0, 0, 20, 1);
        foreach (Rgba32 p in target.GetRow<Rgba32>(0))
        {
            Assert.True(p.A == 0 || (p.R == 0 && p.B == 255), $"{p}");
        }
    }

    [Fact]
    public void A_grey_canvas_takes_the_image_s_luma()
    {
        using Image photo = JpegPhoto(8, 6);
        using var grey = new Image(8, 6, PixelFormat.L8);
        using var canvas = new Canvas(grey);
        canvas.DrawImage(photo, 0, 0, 8, 6);
        using Image expected = photo.Convert(PixelFormat.L8);
        Near(expected, grey, 1);
    }

    [Fact]
    public void Drawing_images_checks_its_input()
    {
        using Image photo = JpegPhoto(4, 4);
        using var target = new Image(8, 8, PixelFormat.Rgb24);
        using var canvas = new Canvas(target);
        Assert.Throws<ArgumentNullException>(() => canvas.DrawImage(null!, 0, 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.DrawImage(photo, 0, 0, 1, 1, opacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.DrawImage(photo, 0, 0, 1, 1, opacity: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.DrawImage(photo, float.NaN, 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.DrawImage(photo, new Matrix3x2(float.PositiveInfinity, 0, 0, 1, 0, 0)));

        using var p3 = new Image(2, 2, PixelFormat.Rgb24) { ColorSpace = ColorSpace.DisplayP3 };
        Assert.Throws<ArgumentException>(() => canvas.DrawImage(p3, 0, 0, 2, 2));

        // Squashed flat: nothing drawn, nothing thrown.
        canvas.DrawImage(photo, 0, 0, 0, 4);
        Assert.All(target.GetPixelBytes().ToArray(), b => Assert.Equal(0, b));

        photo.Dispose();
        Assert.Throws<ObjectDisposedException>(() => canvas.DrawImage(photo, 0, 0, 1, 1));
    }

    [Fact]
    public void Extreme_transforms_draw_or_throw_an_argument_exception()
    {
        using Image photo = JpegPhoto(16, 12);
        var random = new Random(5);
        float[] scales = [1e30f, 1e-30f, 0, -1, 1e6f, 1e-6f, 3.7f, -0.2f];
        for (int round = 0; round < 200; round++)
        {
            float S() => scales[random.Next(scales.Length)];
            var m = new Matrix3x2(S(), S() * (float)random.NextDouble(), S() * (float)random.NextDouble(), S(), S(), S());
            using var target = new Image(24, 18, PixelFormat.Rgba32);
            using var canvas = new Canvas(target);
            try
            {
                canvas.DrawImage(photo, m, random.Next(2) == 0 ? Resampler.Nearest : null);
            }
            catch (ArgumentException)
            {
                // The transform overflowed a float: refused, as documented.
            }
        }
    }

    private static Image JpegPhoto(int width, int height) => Codecs.Jpeg.JpegEncoderTests.Photo(width, height, PixelFormat.Rgb24, seed: width + height);
}
