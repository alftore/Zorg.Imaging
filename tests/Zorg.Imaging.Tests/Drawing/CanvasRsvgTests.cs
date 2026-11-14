using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Drawing;

namespace Zorg.Imaging.Tests.Drawing;

/// <summary>
/// Fills and strokes compared with librsvg's <c>rsvg-convert</c>. Cairo underneath measures coverage closely, and SVG
/// puts pixel edges at whole numbers as the canvas does, so this is the tight check; ImageMagick's is the loose one.
/// </summary>
public class CanvasRsvgTests
{
    public static TheoryData<string> Shapes() => ["star", "letter", "ellipse", "ring-evenodd", "curves"];

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task Fills_match_rsvg(string name)
    {
        (Shape shape, string path, FillRule rule) = CanvasOracleTests.Build(name);
        string attributes = $"fill=\"#fff\" fill-rule=\"{(rule == FillRule.EvenOdd ? "evenodd" : "nonzero")}\"";
        float[] theirs = await Render(path, attributes);
        using Image ours = CanvasTests.Coverage(80, 80, c =>
        {
            c.Tolerance = 0.1f; // cairo's, so both flatten curves alike
            c.Fill(shape, new Rgba32(255, 255, 255), rule);
        });
        Compare(name, CanvasTests.Alphas(ours), theirs, ink: 0.003, mean: 0.4, blocks: 0.3, most: 56);
    }

    [Theory]
    [InlineData("star")]
    [InlineData("letter")]
    [InlineData("curves")]
    public async Task Strokes_match_rsvg(string name)
    {
        (Shape shape, string path, _) = CanvasOracleTests.Build(name);
        string attributes = "fill=\"none\" stroke=\"#fff\" stroke-width=\"3\" stroke-linejoin=\"round\" stroke-linecap=\"round\"";
        float[] theirs = await Render(path, attributes);
        using Image ours = CanvasTests.Coverage(80, 80, c =>
        {
            c.Tolerance = 0.1f;
            c.Stroke(shape, 3, new Rgba32(255, 255, 255));
        });
        Compare(name, CanvasTests.Alphas(ours), theirs, ink: 0.005, mean: 0.8, blocks: 0.6, most: 96);
    }

    private static async Task<float[]> Render(string path, string attributes)
    {
        string rsvg = Oracle.Require("rsvg-convert");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        string svg = folder.File("in.svg"), output = folder.File("out.png");
        await File.WriteAllTextAsync(
            svg,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"80\" viewBox=\"0 0 80 80\"><path d=\"{path}\" {attributes}/></svg>",
            TestContext.Current.CancellationToken);
        (int code, _, string errors) = await Oracle.RunAsync(rsvg, "-o", output, svg);
        Assert.True(code == 0, errors);
        using Image image = Image.Decode(await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken), new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        Assert.Equal((80, 80), (image.Width, image.Height));
        return CanvasTests.Alphas(image);
    }

    /// <summary>
    /// Total ink within a share; the mean difference in 255ths; the ink of every 4 x 4 block within so many pixels; and
    /// the largest difference in 255ths. Cairo samples 15 rows in a pixel, so one pixel can be off by about 1/30 even
    /// on a straight edge, and where two parts of an outline cross in a pixel the canvas counts both: the blocks and the
    /// totals are the tight checks, the largest only a bound.
    /// </summary>
    private static void Compare(string name, float[] ours, float[] theirs, double ink, double mean, double blocks, int most)
    {
        double ourInk = ours.Sum(), theirInk = theirs.Sum();
        double average = ours.Zip(theirs, (a, b) => Math.Abs(a - b)).Average() * 255;
        double largest = ours.Zip(theirs, (a, b) => Math.Abs(a - b)).Max() * 255;
        double block = Blocks(ours).Zip(Blocks(theirs), (a, b) => Math.Abs(a - b)).Max();
        int worst = Enumerable.Range(0, ours.Length).MaxBy(k => Math.Abs(ours[k] - theirs[k]));
        string where = FormattableString.Invariant($"at ({worst % 80}, {worst / 80})");
        string report = FormattableString.Invariant($"{name}: ink {ourInk:F2} against {theirInk:F2}, mean difference {average:F3}/255, largest {largest:F1}/255, block {block:F3} {where}");
        Assert.True(Math.Abs(ourInk - theirInk) <= ink * theirInk, report);
        Assert.True(average <= mean, report);
        Assert.True(largest <= most, report);
        Assert.True(block <= blocks, report);
    }

    /// <summary>The ink in each 4 x 4 block, in pixels: where one sampling's rounding evens out.</summary>
    private static double[] Blocks(float[] alphas)
    {
        double[] blocks = new double[20 * 20];
        for (int i = 0; i < alphas.Length; i++)
        {
            blocks[((i / 80 / 4) * 20) + (i % 80 / 4)] += alphas[i];
        }

        return blocks;
    }
}
