using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using SkiaSharp;
using Zorg.Imaging.Drawing;

namespace Zorg.Imaging.Benchmarks;

/// <summary>
/// A page of text as a PDF preview draws it: an A4 page at 150 dpi (1240 x 1754), cleared to white, 3,000 fills of a
/// 21-pixel letter made of quadratic curves with a counter, and 50 rules. The same scene through SkiaSharp is the native
/// reference; Skia draws into 32-bit pixels, ours into 24-bit RGB as a preview would.
/// </summary>
[MemoryDiagnoser]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet disposes them in the GlobalCleanup.")]
public class CanvasBenchmarks
{
    private const int Width = 1240, Height = 1754, Glyphs = 3000, Rules = 50;
    private const float Size = 21, Bold = 0.03f * 1000;

    private readonly Shape _letter = Letter();
    private readonly SKPath _skiaLetter = SkiaLetter();
    private Image _page = null!;
    private Canvas _canvas = null!;
    private SKBitmap _skiaPage = null!;
    private SKCanvas _skiaCanvas = null!;
    private Matrix3x2[] _places = [];

    [GlobalSetup]
    public void Setup()
    {
        _page = new Image(Width, Height, PixelFormat.Rgb24);
        _canvas = new Canvas(_page);
        _skiaPage = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        _skiaCanvas = new SKCanvas(_skiaPage);
        _places = new Matrix3x2[Glyphs];
        float scale = Size / 1000;
        for (int i = 0; i < Glyphs; i++)
        {
            int column = i % 75, line = i / 75;
            _places[i] = Matrix3x2.CreateScale(scale, -scale) * Matrix3x2.CreateTranslation(40 + (column * 15.5f), 60 + (line * 42.5f));
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _canvas.Dispose();
        _page.Dispose();
        _skiaCanvas.Dispose();
        _skiaPage.Dispose();
        _skiaLetter.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Page()
    {
        _canvas.Clear(new Rgba32(255, 255, 255));
        var black = new Rgba32(0, 0, 0);
        foreach (Matrix3x2 place in _places)
        {
            _canvas.Fill(_letter, black, place);
        }

        DrawRules();
    }

    [Benchmark]
    public void PageBold()
    {
        _canvas.Clear(new Rgba32(255, 255, 255));
        var black = new Rgba32(0, 0, 0);
        foreach (Matrix3x2 place in _places)
        {
            _canvas.Fill(_letter, black, place);
            _canvas.Stroke(_letter, Bold, black, place);
        }

        DrawRules();
    }

    [Benchmark]
    public void SkiaPage() => SkiaDraw(bold: false);

    [Benchmark]
    public void SkiaPageBold() => SkiaDraw(bold: true);

    private void DrawRules()
    {
        var grey = new Rgba32(90, 90, 90);
        for (int i = 0; i < Rules; i++)
        {
            float y = 40 + (i * 34.3f);
            _canvas.StrokeLine(30, y, Width - 30, y, 0.75f, grey);
        }
    }

    private void SkiaDraw(bool bold)
    {
        _skiaCanvas.Clear(SKColors.White);
        using var fill = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint
        {
            Color = SKColors.Black,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Bold,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeCap = SKStrokeCap.Round,
        };
        foreach (Matrix3x2 m in _places)
        {
            _skiaCanvas.SetMatrix(new SKMatrix(m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, 0, 0, 1));
            _skiaCanvas.DrawPath(_skiaLetter, fill);
            if (bold)
            {
                _skiaCanvas.DrawPath(_skiaLetter, stroke);
            }
        }

        _skiaCanvas.ResetMatrix();
        using var rule = new SKPaint { Color = new SKColor(90, 90, 90), IsAntialias = true, StrokeWidth = 1 };
        for (int i = 0; i < Rules; i++)
        {
            float y = 40 + (i * 34.3f);
            _skiaCanvas.DrawLine(30, y, Width - 30, y, rule);
        }

        _skiaCanvas.Flush();
    }

    /// <summary>An "a"-like letter on a 1000-unit em: a bowl of four quadratic curves, its counter, and a stem.</summary>
    private static Shape Letter() => new Shape()
        .MoveTo(500, 0).QuadTo(800, 0, 800, 350).QuadTo(800, 700, 500, 700).QuadTo(200, 700, 200, 350).QuadTo(200, 0, 500, 0).Close()
        .MoveTo(500, 120).QuadTo(330, 120, 330, 350).QuadTo(330, 580, 500, 580).QuadTo(670, 580, 670, 350).QuadTo(670, 120, 500, 120).Close()
        .MoveTo(700, -300).LineTo(820, -300).LineTo(820, 700).LineTo(700, 700).Close();

    private static SKPath SkiaLetter()
    {
        var path = new SKPathBuilder();
        path.MoveTo(500, 0);
        path.QuadTo(800, 0, 800, 350);
        path.QuadTo(800, 700, 500, 700);
        path.QuadTo(200, 700, 200, 350);
        path.QuadTo(200, 0, 500, 0);
        path.Close();
        path.MoveTo(500, 120);
        path.QuadTo(330, 120, 330, 350);
        path.QuadTo(330, 580, 500, 580);
        path.QuadTo(670, 580, 670, 350);
        path.QuadTo(670, 120, 500, 120);
        path.Close();
        path.MoveTo(700, -300);
        path.LineTo(820, -300);
        path.LineTo(820, 700);
        path.LineTo(700, 700);
        path.Close();
        return path.Detach();
    }
}
