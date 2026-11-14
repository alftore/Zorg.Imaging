using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Xunit;
using Zorg.Imaging.Drawing;

namespace Zorg.Imaging.Tests.Drawing;

public class CanvasTests
{
    private static readonly Rgba32 White = new(255, 255, 255);
    private static readonly Rgba32 Clear = new(0, 0, 0, 0);

    /// <summary>Draws white on a transparent canvas, so each pixel's alpha is its coverage.</summary>
    internal static Image Coverage(int width, int height, Action<Canvas> draw)
    {
        var image = new Image(width, height, PixelFormat.Rgba32);
        using var canvas = new Canvas(image);
        canvas.Clear(Clear);
        draw(canvas);
        return image;
    }

    internal static float[] Alphas(Image image) => image.GetPixelBytes().ToArray().Where((_, i) => i % 4 == 3).Select(a => a / 255f).ToArray();

    private static double Overlap(double a0, double a1, double b0, double b1) => Math.Max(0, Math.Min(a1, b1) - Math.Max(a0, b0));

    [Fact]
    public void A_rectangle_covers_each_pixel_by_its_exact_area()
    {
        (float x0, float y0, float x1, float y1) = (3.25f, 2.6f, 11.7f, 7.1f);
        using Image image = Coverage(16, 10, c => c.Fill(Shape.Rectangle(x0, y0, x1 - x0, y1 - y0), White));
        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                double area = Overlap(x, x + 1, x0, x1) * Overlap(y, y + 1, y0, y1);
                Assert.InRange(image.GetRow<Rgba32>(y)[x].A, (area * 255) - 1, (area * 255) + 1);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_triangle_s_total_coverage_is_its_area(bool reversed)
    {
        Shape triangle = reversed
            ? new Shape().MoveTo(4.3f, 3.1f).LineTo(18.6f, 27.4f).LineTo(29.2f, 6.8f).Close()
            : new Shape().MoveTo(4.3f, 3.1f).LineTo(29.2f, 6.8f).LineTo(18.6f, 27.4f).Close();
        double area = Math.Abs(((29.2 - 4.3) * (27.4 - 3.1)) - ((18.6 - 4.3) * (6.8 - 3.1))) / 2;
        using Image image = Coverage(32, 32, c => c.Fill(triangle, White));
        Assert.InRange(Alphas(image).Sum(), area - 0.5, area + 0.5);
    }

    [Fact]
    public void Winding_direction_does_not_change_a_fill()
    {
        Shape clockwise = new Shape().MoveTo(2.5f, 2.5f).QuadTo(20, -3, 27.5f, 12).LineTo(14, 27.2f).CubicTo(5, 20, 0, 12, 2.5f, 2.5f).Close();
        Shape anticlockwise = new Shape().MoveTo(2.5f, 2.5f).CubicTo(0, 12, 5, 20, 14, 27.2f).LineTo(27.5f, 12).QuadTo(20, -3, 2.5f, 2.5f).Close();
        using Image a = Coverage(30, 30, c => c.Fill(clockwise, White));
        using Image b = Coverage(30, 30, c => c.Fill(anticlockwise, White));
        byte[] left = a.GetPixelBytes().ToArray(), right = b.GetPixelBytes().ToArray();
        for (int i = 0; i < left.Length; i++)
        {
            Assert.InRange(left[i] - right[i], -1, 1); // the curves are cut at the same points either way; float order differs
        }
    }

    [Fact]
    public void A_circle_covers_pi_r_squared()
    {
        using Image image = Coverage(64, 64, c => c.Fill(Shape.Ellipse(32.3f, 31.6f, 25, 25), White));
        // The chords lie inside the arc, each at most the 0.2-pixel tolerance from it: a segment of a circle loses at
        // most two thirds of its chord times that, so the area falls short by at most 2/3 x 0.2 x the circumference.
        double area = Math.PI * 25 * 25, loss = 2.0 / 3 * 0.2 * 2 * Math.PI * 25;
        Assert.InRange(Alphas(image).Sum(), area - loss - 1, area + 1);
        Assert.Equal(255, image.GetRow<Rgba32>(32)[32].A);
        Assert.Equal(0, image.GetRow<Rgba32>(1)[1].A);
    }

    [Fact]
    public void Even_odd_leaves_the_hole_that_non_zero_fills()
    {
        // Both squares drawn the same way round: non-zero fills the middle, even-odd leaves it empty.
        Shape ring = Shape.Rectangle(2, 2, 20, 20);
        ring.MoveTo(8, 8).LineTo(16, 8).LineTo(16, 16).LineTo(8, 16).Close();
        using Image nonZero = Coverage(24, 24, c => c.Fill(ring, White));
        using Image evenOdd = Coverage(24, 24, c => c.Fill(ring, White, FillRule.EvenOdd));
        Assert.Equal(255, nonZero.GetRow<Rgba32>(12)[12].A);
        Assert.Equal(0, evenOdd.GetRow<Rgba32>(12)[12].A);
        Assert.Equal(255, evenOdd.GetRow<Rgba32>(4)[4].A);
        Assert.InRange(Alphas(evenOdd).Sum(), 400 - 64 - 0.5, 400 - 64 + 0.5);

        // Drawn the other way round, the inner square is a hole under non-zero too.
        Shape holed = Shape.Rectangle(2, 2, 20, 20).MoveTo(8, 8).LineTo(8, 16).LineTo(16, 16).LineTo(16, 8).Close();
        using Image cut = Coverage(24, 24, c => c.Fill(holed, White));
        Assert.Equal(evenOdd.GetPixelBytes().ToArray(), cut.GetPixelBytes().ToArray());
    }

    [Fact]
    public void A_clip_changes_only_the_pixels_inside_it()
    {
        Shape star = Star(20, 20, 18, 7);
        using Image whole = Coverage(40, 40, c => c.Fill(star, White));
        using Image clipped = Coverage(40, 40, c =>
        {
            c.SetClip(7, 11, 20, 15);
            c.Fill(star, White);
        });

        for (int y = 0; y < 40; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                bool inside = x >= 7 && x < 27 && y >= 11 && y < 26;
                Assert.Equal(inside ? whole.GetRow<Rgba32>(y)[x].A : 0, clipped.GetRow<Rgba32>(y)[x].A);
            }
        }
    }

    [Fact]
    public void A_shape_partly_off_the_canvas_draws_the_part_on_it()
    {
        // A triangle sticking out on every side, against the same triangle drawn on a larger canvas and cropped.
        Shape big = new Shape().MoveTo(-30.5f, -10.25f).LineTo(55.5f, 4.75f).LineTo(10.5f, 60.5f).Close();
        using Image small = Coverage(30, 30, c => c.Fill(big, White));
        using Image large = Coverage(130, 130, c => c.Fill(big, White, Matrix3x2.CreateTranslation(50, 50)));
        using Image cropped = large.Crop(50, 50, 30, 30);
        byte[] a = small.GetPixelBytes().ToArray(), b = cropped.GetPixelBytes().ToArray();
        for (int i = 0; i < a.Length; i++)
        {
            Assert.InRange(a[i] - b[i], -1, 1);
        }
    }

    [Fact]
    public void A_transform_moves_the_shape_as_moving_its_points_does()
    {
        Matrix3x2 m = Matrix3x2.CreateRotation(0.4f) * Matrix3x2.CreateScale(1.5f, 0.8f) * Matrix3x2.CreateTranslation(9.3f, 4.1f);
        (float X, float Y)[] points = [(1, 1), (14, 2), (12, 13), (3, 9)];
        Shape plain = new Shape().MoveTo(points[0].X, points[0].Y);
        Shape moved = new();
        for (int i = 0; i < points.Length; i++)
        {
            Vector2 p = Vector2.Transform(new Vector2(points[i].X, points[i].Y), m);
            if (i == 0)
            {
                moved.MoveTo(p.X, p.Y);
            }
            else
            {
                plain.LineTo(points[i].X, points[i].Y);
                moved.LineTo(p.X, p.Y);
            }
        }

        using Image viaCall = Coverage(32, 32, c => c.Fill(plain.Close(), White, m));
        using Image viaCanvas = Coverage(32, 32, c =>
        {
            c.Transform = m;
            c.Fill(plain, White);
        });
        using Image viaPoints = Coverage(32, 32, c => c.Fill(moved.Close(), White));
        Assert.Equal(viaPoints.GetPixelBytes().ToArray(), viaCall.GetPixelBytes().ToArray());
        Assert.Equal(viaPoints.GetPixelBytes().ToArray(), viaCanvas.GetPixelBytes().ToArray());
    }

    [Fact]
    public void Colours_blend_in_the_stored_values()
    {
        using var image = new Image(4, 1, PixelFormat.Rgb24);
        using var canvas = new Canvas(image);
        canvas.Clear(White);
        canvas.FillRectangle(0, 0, 1, 1, new Rgba32(255, 0, 0, 128)); // half-transparent red, whole pixel
        canvas.FillRectangle(1, 0, 0.5f, 1, new Rgba32(0, 0, 0)); // black, half the pixel
        Assert.Equal(new Rgb24(255, 127, 127), image.GetRow<Rgb24>(0)[0]);
        Assert.Equal(new Rgb24(128, 128, 128), image.GetRow<Rgb24>(0)[1]); // 50% coverage is grey 128, as in a PDF viewer
        Assert.Equal(White.R, image.GetRow<Rgb24>(0)[2].R);
    }

    [Fact]
    public void Every_layout_draws_the_same_picture()
    {
        Shape star = Star(16, 16, 14, 6);
        using Image reference = Draw(PixelFormat.Rgba32);
        foreach (PixelFormat format in new[] { PixelFormat.Bgra32, PixelFormat.Rgb24, PixelFormat.Rgba64, PixelFormat.Rgb48 })
        {
            using Image drawn = Draw(format);
            using Image back = drawn.Convert(PixelFormat.Rgba32);
            byte[] a = reference.GetPixelBytes().ToArray(), b = back.GetPixelBytes().ToArray();
            for (int i = 0; i < a.Length; i++)
            {
                Assert.InRange(a[i] - b[i], -1, 1);
            }
        }

        // Grey takes the colour's luma.
        using Image grey = Draw(PixelFormat.L8);
        using Image greyReference = reference.Convert(PixelFormat.L8);
        byte[] g = grey.GetPixelBytes().ToArray(), r = greyReference.GetPixelBytes().ToArray();
        for (int i = 0; i < g.Length; i++)
        {
            Assert.InRange(g[i] - r[i], -2, 2);
        }

        Image Draw(PixelFormat format)
        {
            var image = new Image(32, 32, format);
            using var canvas = new Canvas(image);
            canvas.Clear(new Rgba32(250, 240, 200));
            canvas.Fill(star, new Rgba32(20, 60, 160));
            canvas.FillRectangle(2.5f, 20.25f, 27, 3.5f, new Rgba32(200, 30, 30, 160));
            return image;
        }
    }

    [Fact]
    public void Translucent_layers_blend_alike_in_every_alpha_layout()
    {
        // The 8-bit layouts have their own blending; over a see-through background it must match the 16-bit one.
        Shape star = Star(16, 16, 14, 6);
        using Image reference = Draw(PixelFormat.Rgba64);
        using Image wide = reference.Convert(PixelFormat.Rgba32);
        foreach (PixelFormat format in new[] { PixelFormat.Rgba32, PixelFormat.Bgra32 })
        {
            using Image drawn = Draw(format);
            using Image back = drawn.Convert(PixelFormat.Rgba32);
            byte[] a = wide.GetPixelBytes().ToArray(), b = back.GetPixelBytes().ToArray();
            for (int i = 0; i < a.Length; i++)
            {
                Assert.InRange(a[i] - b[i], -1, 1);
            }
        }

        Image Draw(PixelFormat format)
        {
            var image = new Image(32, 32, format);
            using var canvas = new Canvas(image);
            canvas.Clear(new Rgba32(10, 200, 100, 90));
            canvas.FillRectangle(0, 0, 9.5f, 32, new Rgba32(0, 0, 0, 0)); // and a strip fully see-through
            canvas.Fill(star, new Rgba32(20, 60, 160, 200));
            canvas.FillRectangle(2.5f, 20.25f, 27, 3.5f, new Rgba32(200, 30, 30, 255));
            return image;
        }
    }

    [Fact]
    public void Clear_fills_only_the_clip()
    {
        using var image = new Image(7, 5, PixelFormat.Rgb24);
        using var canvas = new Canvas(image);
        canvas.SetClip(2, 1, 4, 3);
        canvas.Clear(new Rgba32(200, 10, 20));
        for (int y = 0; y < 5; y++)
        {
            for (int x = 0; x < 7; x++)
            {
                bool inside = x is >= 2 and < 6 && y is >= 1 and < 4;
                Assert.Equal(inside ? new Rgb24(200, 10, 20) : default, image.GetRow<Rgb24>(y)[x]);
            }
        }
    }

    [Fact]
    public void Tall_fills_render_in_bands_as_in_one_piece()
    {
        // 1200 wide: a band holds fewer rows than the canvas, so a full-height fill is done in more than one.
        Assert.True(Rasterizer.MaxCells / 1202 < 1000);
        Shape shape = Shape.Ellipse(600, 500, 590, 490).MoveTo(600, 150).LineTo(850, 800).LineTo(350, 800).Close();
        using Image banded = Draw(clipped: false);
        using Image pieces = Draw(clipped: true); // each half within one band
        Assert.Equal(pieces.GetPixelBytes().ToArray(), banded.GetPixelBytes().ToArray());
        double inked = banded.GetPixelBytes().ToArray().Sum(b => b / 255.0);
        Assert.InRange(inked, Math.PI * 590 * 490 * 0.999, Math.PI * 590 * 490 * 1.001); // the triangle is inside, non-zero

        Image Draw(bool clipped)
        {
            var image = new Image(1200, 1000, PixelFormat.L8);
            using var canvas = new Canvas(image);
            if (!clipped)
            {
                canvas.Fill(shape, new Rgba32(255, 255, 255));
                return image;
            }

            canvas.SetClip(0, 0, 1200, 500);
            canvas.Fill(shape, new Rgba32(255, 255, 255));
            canvas.SetClip(0, 500, 1200, 500);
            canvas.Fill(shape, new Rgba32(255, 255, 255));
            return image;
        }
    }

    [Fact]
    public void Shapes_check_their_input()
    {
        Assert.Throws<InvalidOperationException>(() => new Shape().LineTo(1, 1));
        Assert.Throws<InvalidOperationException>(() => new Shape().MoveTo(0, 0).Close().LineTo(1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Shape().MoveTo(float.NaN, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Shape().MoveTo(0, 0).QuadTo(0, float.PositiveInfinity, 1, 1));

        Shape shape = new Shape().MoveTo(0, 0).LineTo(1, 0).QuadTo(1, 1, 0, 1).CubicTo(0, 2, 2, 2, 2, 0).Close();
        Assert.Equal(4, shape.SegmentCount);
        Assert.False(shape.IsEmpty);
        Assert.True(shape.Clear().IsEmpty);
        Assert.Equal(0, shape.SegmentCount);
    }

    [Fact]
    public void Canvases_check_their_input()
    {
        using var p3 = new Image(4, 4, PixelFormat.Rgb24) { ColorSpace = ColorSpace.DisplayP3 };
        Assert.Throws<ArgumentException>(() => new Canvas(p3));
        Assert.Throws<ArgumentNullException>(() => new Canvas(null!));

        using var rgb = new Image(4, 4, PixelFormat.Rgb24);
        using var canvas = new Canvas(rgb);
        Assert.Throws<ArgumentException>(() => canvas.Clear(new Rgba32(1, 2, 3, 100)));
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.Tolerance = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.Tolerance = float.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.Transform = new Matrix3x2(float.NaN, 0, 0, 1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.Fill(Shape.Rectangle(0, 0, 1, 1), White, new Matrix3x2(1, 0, 0, float.PositiveInfinity, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.FillRectangle(0, float.NaN, 1, 1, White));
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.Fill(Shape.Rectangle(0, 0, 1, 1), White, (FillRule)5));
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.SetClip(0, 0, -1, 1));

        rgb.Dispose();
        Assert.Throws<ObjectDisposedException>(() => canvas.Fill(Shape.Rectangle(0, 0, 1, 1), White));
    }

    [Fact]
    public void Hostile_shapes_draw_or_throw_an_argument_exception_quickly()
    {
        var random = new Random(7);
        float[] extremes = [0, -1e30f, 1e30f, float.MaxValue, -float.MaxValue, 1e-30f, 3.5f, -7.25f, 1 << 21, 100];
        var timer = Stopwatch.StartNew();
        for (int round = 0; round < 300; round++)
        {
            var shape = new Shape();
            float Next() => random.Next(3) == 0 ? extremes[random.Next(extremes.Length)] : (float)((random.NextDouble() * 200) - 50);
            shape.MoveTo(Next(), Next());
            for (int i = random.Next(1, 12); i > 0; i--)
            {
                int kind = random.Next(4);
                if (kind == 0)
                {
                    shape.LineTo(Next(), Next());
                }
                else if (kind == 1)
                {
                    shape.QuadTo(Next(), Next(), Next(), Next());
                }
                else if (kind == 2)
                {
                    shape.CubicTo(Next(), Next(), Next(), Next(), Next(), Next());
                }
                else
                {
                    shape.Close().MoveTo(Next(), Next());
                }
            }

            Matrix3x2 m = random.Next(4) == 0 ? Matrix3x2.CreateScale(1e30f) : Matrix3x2.CreateRotation((float)random.NextDouble());
            using var image = new Image(64, 48, PixelFormat.Rgba32);
            using var canvas = new Canvas(image);
            canvas.SetClip(5, 5, 50, 30);
            try
            {
                canvas.Fill(shape, new Rgba32(255, 255, 255, 200), m, random.Next(2) == 0 ? FillRule.NonZero : FillRule.EvenOdd);
            }
            catch (ArgumentException)
            {
                // The transform overflowed a float: refused, as documented.
            }

            for (int y = 0; y < 48; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    if (x < 5 || x >= 55 || y < 5 || y >= 35)
                    {
                        Assert.Equal(default, image.GetRow<Rgba32>(y)[x]);
                    }
                }
            }
        }

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(20), $"Took {timer.Elapsed}.");
    }

    [Fact]
    public void Many_glyph_sized_fills_reuse_memory()
    {
        // A page of text: thousands of small fills on one canvas. Mostly checks nothing throws and the time is sane.
        using var page = new Image(620, 877, PixelFormat.Rgb24);
        using var canvas = new Canvas(page);
        canvas.Clear(White);
        Shape glyph = Letter();
        var timer = Stopwatch.StartNew();
        for (int line = 0; line < 50; line++)
        {
            for (int column = 0; column < 60; column++)
            {
                canvas.Fill(glyph, new Rgba32(0, 0, 0), Matrix3x2.CreateScale(0.01f) * Matrix3x2.CreateTranslation(20 + (column * 9.7f), 30 + (line * 16.3f)));
            }
        }

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"Took {timer.Elapsed}.");
        Assert.Contains(page.GetPixelBytes().ToArray(), b => b < 64);
    }

    internal static Shape Star(float cx, float cy, float outer, float inner)
    {
        var star = new Shape();
        for (int i = 0; i < 10; i++)
        {
            double angle = (Math.PI * i / 5) - (Math.PI / 2);
            float r = i % 2 == 0 ? outer : inner;
            float x = cx + (r * (float)Math.Cos(angle)), y = cy + (r * (float)Math.Sin(angle));
            if (i == 0)
            {
                star.MoveTo(x, y);
            }
            else
            {
                star.LineTo(x, y);
            }
        }

        return star.Close();
    }

    /// <summary>A letter-like outline of quadratic curves with a counter, in 1000 units: an "o" with a stem, like a "d".</summary>
    internal static Shape Letter() => new Shape()
        .MoveTo(500, 0).QuadTo(800, 0, 800, 350).QuadTo(800, 700, 500, 700).QuadTo(200, 700, 200, 350).QuadTo(200, 0, 500, 0).Close()
        .MoveTo(500, 120).QuadTo(330, 120, 330, 350).QuadTo(330, 580, 500, 580).QuadTo(670, 580, 670, 350).QuadTo(670, 120, 500, 120).Close()
        .MoveTo(700, -300).LineTo(820, -300).LineTo(820, 700).LineTo(700, 700).Close();
}
