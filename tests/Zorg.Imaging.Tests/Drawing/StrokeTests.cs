using System;
using System.Linq;
using System.Numerics;
using Xunit;
using Zorg.Imaging.Drawing;

namespace Zorg.Imaging.Tests.Drawing;

public class StrokeTests
{
    private static readonly Rgba32 White = new(255, 255, 255);

    private static Image Coverage(int width, int height, Action<Canvas> draw) => CanvasTests.Coverage(width, height, draw);

    [Fact]
    public void A_straight_line_is_the_rectangle_it_sweeps()
    {
        using Image line = Coverage(24, 12, c => c.StrokeLine(2.5f, 5.25f, 20.5f, 5.25f, 2.5f, White));
        using Image box = Coverage(24, 12, c => c.FillRectangle(2.5f, 4f, 18, 2.5f, White));
        Assert.Equal(box.GetPixelBytes().ToArray(), line.GetPixelBytes().ToArray());
    }

    [Fact]
    public void An_open_line_has_round_ends()
    {
        // 20 long, 6 wide, with a half disc of radius 3 at each end.
        using Image image = Coverage(40, 20, c => c.Stroke(new Shape().MoveTo(10, 10.3f).LineTo(30, 10.3f), 6, White));
        double area = (20 * 6) + (Math.PI * 9);
        Assert.InRange(CanvasTests.Alphas(image).Sum(), area - 0.6, area + 0.6);
        Assert.Equal(0, image.GetRow<Rgba32>(10)[6].A); // past the cap
        Assert.Equal(255, image.GetRow<Rgba32>(10)[8].A); // inside the cap
    }

    [Fact]
    public void A_closed_square_s_outline_has_round_outer_corners_and_square_inner_ones()
    {
        // A square of side 20 stroked 4 wide: the outer edge is the square grown by 2 with corners of radius 2, the
        // inner edge the square shrunk by 2.
        using Image image = Coverage(40, 40, c => c.Stroke(Shape.Rectangle(10, 10, 20, 20), 4, White));
        double outer = (20 * 20) + (4 * 20 * 2) + (Math.PI * 4), inner = 16 * 16;
        Assert.InRange(CanvasTests.Alphas(image).Sum(), outer - inner - 0.6, outer - inner + 0.6);
        Assert.Equal(255, image.GetRow<Rgba32>(10)[10].A); // the corner itself is covered
        Assert.Equal(0, image.GetRow<Rgba32>(20)[20].A); // the middle isn't
    }

    [Fact]
    public void A_translucent_stroke_has_no_darker_overlaps()
    {
        // The rectangles and discs overlap at every corner; a non-zero fill of them all counts each pixel once.
        using Image image = Coverage(40, 40, c => c.Stroke(CanvasTests.Star(20, 20, 16, 7), 3, new Rgba32(255, 255, 255, 128)));
        Assert.Equal(128, image.GetPixelBytes().ToArray().Where((_, i) => i % 4 == 3).Max());
    }

    [Fact]
    public void Hairlines_are_drawn_one_pixel_wide()
    {
        using Image hairline = Coverage(30, 10, c => c.StrokeLine(2, 5, 27, 5, 0, White));
        Assert.InRange(CanvasTests.Alphas(hairline).Sum(), 25 - 0.1, 25 + 0.1);

        using Image thin = Coverage(30, 10, c =>
        {
            c.MinimumStrokeWidth = 0;
            c.StrokeLine(2, 5, 27, 5, 0.25f, White);
        });
        Assert.InRange(CanvasTests.Alphas(thin).Sum(), 6.25 - 0.1, 6.25 + 0.1);
    }

    [Fact]
    public void The_width_scales_with_the_transform()
    {
        Shape small = new Shape().MoveTo(2, 2).LineTo(12, 3).LineTo(8, 12);
        Shape large = new Shape().MoveTo(4, 4).LineTo(24, 6).LineTo(16, 24);
        using Image scaled = Coverage(32, 32, c => c.Stroke(small, 1.5f, White, Matrix3x2.CreateScale(2)));
        using Image direct = Coverage(32, 32, c => c.Stroke(large, 3, White));
        byte[] a = scaled.GetPixelBytes().ToArray(), b = direct.GetPixelBytes().ToArray();
        for (int i = 0; i < a.Length; i++)
        {
            Assert.InRange(a[i] - b[i], -2, 2); // the discs have as many sides either way; only float order differs
        }
    }

    [Fact]
    public void A_single_point_with_a_round_pen_is_a_dot()
    {
        using Image image = Coverage(20, 20, c => c.Stroke(new Shape().MoveTo(10, 10).LineTo(10, 10), 6, White));
        Assert.InRange(CanvasTests.Alphas(image).Sum(), (Math.PI * 9) - 0.5, (Math.PI * 9) + 0.5);
    }

    [Fact]
    public void A_lone_move_paints_nothing_but_a_closed_point_is_a_dot()
    {
        // As in PDF and SVG: "m S" paints nothing; "m h S" and a zero-length line with a round cap paint a dot.
        using Image lone = Coverage(20, 20, c => c.Stroke(new Shape().MoveTo(10, 10).MoveTo(12, 12), 6, White));
        Assert.Equal(0, CanvasTests.Alphas(lone).Sum());
        using Image closed = Coverage(20, 20, c => c.Stroke(new Shape().MoveTo(10, 10).Close(), 6, White));
        Assert.InRange(CanvasTests.Alphas(closed).Sum(), (Math.PI * 9) - 0.5, (Math.PI * 9) + 0.5);
    }

    [Fact]
    public void Points_too_close_for_a_direction_make_a_dot()
    {
        // 1e-24 apart: the distance squared is 0 in a float, so there's no direction to stroke along.
        using Image image = Coverage(20, 20, c => c.Stroke(new Shape().MoveTo(10, 10).LineTo(10 + 1e-24f, 10), 6, White));
        Assert.InRange(CanvasTests.Alphas(image).Sum(), (Math.PI * 9) - 0.5, (Math.PI * 9) + 0.5);
    }

    [Fact]
    public void A_hairline_is_a_pixel_wide_whichever_way_the_transform_stretches()
    {
        // x is stretched 4 times and y squeezed to a quarter: a width-0 line is still one pixel wide either way.
        foreach (bool across in new[] { true, false })
        {
            using Image image = Coverage(40, 40, c =>
            {
                c.Transform = Matrix3x2.CreateScale(4, 0.25f);
                if (across)
                {
                    c.StrokeLine(1, 80.5f, 9, 80.5f, 0, White); // 32 pixels long, in y = 20.125
                }
                else
                {
                    c.Stroke(new Shape().MoveTo(5.125f, 16).LineTo(5.125f, 144), 0, White); // 32 long, round ends
                }
            });
            double expected = across ? 32 : 32 + (Math.PI / 4);
            Assert.InRange(CanvasTests.Alphas(image).Sum(), expected - 0.2, expected + 0.2);
        }
    }

    [Fact]
    public void Strokes_check_their_input()
    {
        using var image = new Image(8, 8, PixelFormat.Rgb24);
        using var canvas = new Canvas(image);
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.StrokeLine(0, 0, 1, float.NaN, 1, White));
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.StrokeLine(0, 0, 1, 1, -1, White));
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.Stroke(Shape.Rectangle(0, 0, 2, 2), float.PositiveInfinity, White));
        Assert.Throws<ArgumentNullException>(() => canvas.Stroke(null!, 1, White));
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.MinimumStrokeWidth = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => canvas.MinimumStrokeWidth = float.NaN);
    }

    [Fact]
    public void Hostile_strokes_draw_or_throw_an_argument_exception()
    {
        var random = new Random(11);
        float[] extremes = [0, -1e30f, 1e30f, float.MaxValue, 1e-30f, 3.5f, 1 << 21];
        for (int round = 0; round < 200; round++)
        {
            float Next() => random.Next(3) == 0 ? extremes[random.Next(extremes.Length)] : (float)((random.NextDouble() * 100) - 20);
            var shape = new Shape().MoveTo(Next(), Next());
            for (int i = random.Next(1, 6); i > 0; i--)
            {
                if (random.Next(2) == 0)
                {
                    shape.LineTo(Next(), Next());
                }
                else
                {
                    shape.CubicTo(Next(), Next(), Next(), Next(), Next(), Next());
                }
            }

            using var image = new Image(48, 32, PixelFormat.Rgba32);
            using var canvas = new Canvas(image);
            try
            {
                canvas.Stroke(shape, random.Next(2) == 0 ? Next() * 0.1f : 2, White, random.Next(4) == 0 ? Matrix3x2.CreateScale(1e20f) : Matrix3x2.Identity);
            }
            catch (ArgumentException)
            {
                // A negative width, or a transform that overflowed a float: refused, as documented.
            }
        }
    }
}
