using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Drawing;

namespace Zorg.Imaging.Tests.Drawing;

/// <summary>Fills compared with ImageMagick's <c>-draw path</c>, which anti-aliases by its own method, not exact area.</summary>
public class CanvasOracleTests
{
    public static TheoryData<string> Shapes() => ["star", "letter", "ellipse", "ring-evenodd", "curves"];

    private static (Shape Shape, string Path, FillRule Rule) Build(string name)
    {
        var path = new StringBuilder();
        var shape = new Shape();
        void M(float x, float y)
        {
            shape.MoveTo(x, y);
            path.Append(Inv($"M {x} {y} "));
        }

        void L(float x, float y)
        {
            shape.LineTo(x, y);
            path.Append(Inv($"L {x} {y} "));
        }

        void Q(float cx, float cy, float x, float y)
        {
            shape.QuadTo(cx, cy, x, y);
            path.Append(Inv($"Q {cx} {cy} {x} {y} "));
        }

        void C(float ax, float ay, float bx, float by, float x, float y)
        {
            shape.CubicTo(ax, ay, bx, by, x, y);
            path.Append(Inv($"C {ax} {ay} {bx} {by} {x} {y} "));
        }

        void Z()
        {
            shape.Close();
            path.Append("Z ");
        }

        FillRule rule = FillRule.NonZero;
        switch (name)
        {
            case "star":
                for (int i = 0; i < 10; i++)
                {
                    double angle = (Math.PI * i / 5) - (Math.PI / 2);
                    float r = i % 2 == 0 ? 37.3f : 14.6f;
                    float x = 40.4f + (r * (float)Math.Cos(angle)), y = 41.2f + (r * (float)Math.Sin(angle));
                    if (i == 0)
                    {
                        M(x, y);
                    }
                    else
                    {
                        L(x, y);
                    }
                }

                Z();
                break;
            case "letter":
                // The test letter at 64 pixels, its stem going up.
                M(40, 75);
                Q(59.2f, 75, 59.2f, 52.6f);
                Q(59.2f, 30.2f, 40, 30.2f);
                Q(20.8f, 30.2f, 20.8f, 52.6f);
                Q(20.8f, 75, 40, 75);
                Z();
                M(40, 67.3f);
                Q(29.1f, 67.3f, 29.1f, 52.6f);
                Q(29.1f, 37.9f, 40, 37.9f);
                Q(50.9f, 37.9f, 50.9f, 52.6f);
                Q(50.9f, 67.3f, 40, 67.3f);
                Z();
                M(52.8f, 75);
                L(60.5f, 75);
                L(60.5f, 10.8f);
                L(52.8f, 10.8f);
                Z();
                break;
            case "ellipse":
                M(70.5f, 40);
                C(70.5f, 56.6f, 56.9f, 70.1f, 40, 70.1f);
                C(23.1f, 70.1f, 9.5f, 56.6f, 9.5f, 40);
                C(9.5f, 23.4f, 23.1f, 9.9f, 40, 9.9f);
                C(56.9f, 9.9f, 70.5f, 23.4f, 70.5f, 40);
                Z();
                break;
            case "ring-evenodd":
                rule = FillRule.EvenOdd;
                M(8.5f, 8.5f);
                L(71.5f, 8.5f);
                L(71.5f, 71.5f);
                L(8.5f, 71.5f);
                Z();
                M(25.25f, 25.25f);
                L(54.75f, 25.25f);
                L(54.75f, 54.75f);
                L(25.25f, 54.75f);
                Z();
                break;
            default: // curves that cross themselves
                M(10, 70);
                C(10, -20, 90, 120, 70, 10);
                Q(40, 90, 10, 40);
                L(75, 75);
                Z();
                break;
        }

        return (shape, path.ToString().Trim(), rule);

        static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Fills_match_imagemagick(string name)
    {
        string magick = Oracle.Require("magick");
        (Shape shape, string path, FillRule rule) = Build(name);
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        string output = folder.File("out.png");
        string draw = $"fill-rule {(rule == FillRule.EvenOdd ? "evenodd" : "nonzero")} path '{path}'";
        (int code, _, string errors) = await Oracle.RunAsync(magick, "-size", "80x80", "xc:none", "-fill", "white", "-stroke", "none", "-draw", draw, "PNG32:" + output);
        Assert.True(code == 0, errors);

        using Image theirs = Image.Decode(await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken), new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        // ImageMagick puts pixel centres at whole numbers; the canvas, like PDF and SVG, puts pixel edges there.
        using Image ours = CanvasTests.Coverage(80, 80, c => c.Fill(shape, new Rgba32(255, 255, 255), Matrix3x2.CreateTranslation(0.5f, 0.5f), rule));
        float[] a = CanvasTests.Alphas(ours), b = CanvasTests.Alphas(theirs);

        // ImageMagick doesn't measure exact area: its edges come out about a quarter of a pixel wider all round (an
        // even-odd ring of exact area 3098.75 gets 3198.6 from it, 3099 from the canvas, 2026-11-14). So this checks
        // what it can: no pixel is fully inside in one and fully outside in the other, the ink is in the same place,
        // and there is a little less of it here. Exact coverage is CanvasTests' job.
        int solidDisagreements = a.Zip(b, (x, y) => (x == 1 && y == 0) || (x == 0 && y == 1)).Count(d => d);
        double ink = a.Sum(), theirInk = b.Sum();
        double mean = a.Zip(b, (x, y) => Math.Abs(x - y)).Average();
        (double X, double Y) ourCentre = Centre(a), theirCentre = Centre(b);
        Assert.Equal(0, solidDisagreements);
        Assert.InRange(theirInk / ink, 1.0, 1.08);
        Assert.True(mean <= 5.0 / 255, $"{name}: mean difference {mean * 255:F2}/255");
        Assert.InRange(ourCentre.X - theirCentre.X, -0.35, 0.35);
        Assert.InRange(ourCentre.Y - theirCentre.Y, -0.35, 0.35);

        static (double X, double Y) Centre(float[] v) =>
            (v.Select((c, k) => c * (k % 80)).Sum() / v.Sum(), v.Select((c, k) => c * (k / 80)).Sum() / v.Sum());
    }

    [Theory]
    [InlineData("star")]
    [InlineData("letter")]
    [InlineData("curves")]
    public async Task Strokes_match_imagemagick(string name)
    {
        string magick = Oracle.Require("magick");
        (Shape shape, string path, _) = Build(name);
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        string output = folder.File("out.png");
        string draw = $"stroke-linejoin round stroke-linecap round path '{path}'";
        (int code, _, string errors) = await Oracle.RunAsync(
            magick, "-size", "80x80", "xc:none", "-fill", "none", "-stroke", "white", "-strokewidth", "3", "-draw", draw, "PNG32:" + output);
        Assert.True(code == 0, errors);

        using Image theirs = Image.Decode(await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken), new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        using Image ours = CanvasTests.Coverage(80, 80, c => c.Stroke(shape, 3, new Rgba32(255, 255, 255), Matrix3x2.CreateTranslation(0.5f, 0.5f)));
        float[] a = CanvasTests.Alphas(ours), b = CanvasTests.Alphas(theirs);

        // As for fills: the same solid pixels, the ink in the same place, ImageMagick's edges a little wider.
        int solidDisagreements = a.Zip(b, (x, y) => (x == 1 && y == 0) || (x == 0 && y == 1)).Count(d => d);
        double ink = a.Sum(), theirInk = b.Sum();
        double mean = a.Zip(b, (x, y) => Math.Abs(x - y)).Average();
        Assert.Equal(0, solidDisagreements);
        Assert.True(theirInk / ink is >= 0.97 and <= 1.15, FormattableString.Invariant($"{name}: ink {ink:F1} against {theirInk:F1}"));
        Assert.True(mean <= 8.0 / 255, $"{name}: mean difference {mean * 255:F2}/255");
    }
}
