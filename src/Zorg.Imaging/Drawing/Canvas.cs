using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Zorg.Imaging.Drawing;

/// <summary>
/// Draws filled shapes into an <see cref="Image"/> in place, anti-aliased by exact pixel coverage: page previews,
/// charts, badges. Colours are blended in the stored values, as PDF viewers and browsers blend, so edges look as they do
/// there.
/// </summary>
/// <remarks>
/// <para>
/// The canvas draws into the image it was given and doesn't own it. The image must be sRGB or linear sRGB, so a
/// colour means what it says; call <see cref="Image.ToSrgb"/> first for another colour space. Grey images take each
/// colour's Rec. 709 luma.
/// </para>
/// <para>
/// A shape is the caller's data, as an image's size is: drawing takes time in proportion to its lines and the rows
/// they cross, and memory up to 4 bytes per pixel of the canvas, reused from one call to the next. A caller drawing
/// untrusted outlines limits <see cref="Shape.SegmentCount"/>. Curves are cut into at most 1024 lines; coordinates
/// beyond a million pixels are clamped.
/// </para>
/// <para>
/// The canvas is new and may still change, or move to its own package: using it needs the diagnostic
/// <c>ZORGDRAW</c> suppressed. It isn't safe to use from several threads at once.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// using var page = new Image(400, 300, PixelFormat.Rgb24);
/// using var canvas = new Canvas(page);
/// canvas.Clear(new Rgba32(255, 255, 255));
/// canvas.Fill(Shape.Ellipse(200, 150, 80, 60), new Rgba32(30, 90, 200));
/// page.SaveAsPng(output);
/// </code>
/// </example>
[Experimental(Diagnostic)]
public sealed class Canvas : IDisposable
{
    /// <summary>The diagnostic that marks the drawing API as new.</summary>
    internal const string Diagnostic = "ZORGDRAW";

    private readonly Rasterizer _rasterizer = new();
    private Matrix3x2 _transform = Matrix3x2.Identity;
    private float _tolerance = 0.2f;
    private float _minimumStrokeWidth = 1;
    private int _clipLeft, _clipTop, _clipRight, _clipBottom;

    /// <summary>Starts drawing into an image.</summary>
    /// <param name="image">The image to draw into, in place. The canvas doesn't dispose it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The image isn't sRGB or linear sRGB.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <example><code>using var canvas = new Canvas(page);</code></example>
    public Canvas(Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _ = image.GetPixelBytes();
        if (!(image.ColorSpace.IsSrgb || image.ColorSpace.Equals(ColorSpace.LinearSrgb)))
        {
            throw new ArgumentException($"The image is {image.ColorSpace}; drawing needs sRGB. Call ToSrgb first.", nameof(image));
        }

        Image = image;
        ResetClip();
    }

    /// <summary>The image drawn into.</summary>
    /// <example><code>canvas.Image.SaveAsPng(output);</code></example>
    public Image Image { get; }

    /// <summary>
    /// Maps shape coordinates to pixels, applied after each call's own transform. Identity by default: one unit is one
    /// pixel, y down. A PDF page sets a scale and a flip once.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An element isn't finite.</exception>
    /// <example><code>canvas.Transform = Matrix3x2.CreateScale(2, -2) * Matrix3x2.CreateTranslation(0, page.Height);</code></example>
    public Matrix3x2 Transform
    {
        get => _transform;
        set
        {
            Finite(value, nameof(value));
            _transform = value;
        }
    }

    /// <summary>
    /// How far, in pixels, the straight lines that stand in for a curve may stray from it. 0.2 by default, which can't
    /// be seen; larger is faster.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below 0.01 or above 10, or not a number.</exception>
    /// <example><code>canvas.Tolerance = 0.5f;</code></example>
    public float Tolerance
    {
        get => _tolerance;
        set
        {
            if (!(value >= 0.01f && value <= 10))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The tolerance must be from 0.01 to 10 pixels.");
            }

            _tolerance = value;
        }
    }

    /// <summary>
    /// The narrowest a stroke is drawn, in pixels: 1 by default, so a hairline rule (width 0, as PDF's <c>0 w</c>) stays
    /// visible, as PDF viewers draw it. 0 draws every stroke at its own width, however thin.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative, above 1000 or not a number.</exception>
    /// <example><code>canvas.MinimumStrokeWidth = 0.5f;</code></example>
    public float MinimumStrokeWidth
    {
        get => _minimumStrokeWidth;
        set
        {
            if (!(value >= 0 && value <= 1000))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The minimum stroke width must be from 0 to 1000 pixels.");
            }

            _minimumStrokeWidth = value;
        }
    }

    /// <summary>Limits drawing to a rectangle of pixels, intersected with the image. <see cref="ResetClip"/> removes it.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is negative.</exception>
    /// <example><code>canvas.SetClip(0, 0, 200, 100); // only the top-left part changes</code></example>
    public void SetClip(int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        _clipLeft = (int)Math.Clamp((long)x, 0, Image.Width);
        _clipTop = (int)Math.Clamp((long)y, 0, Image.Height);
        _clipRight = (int)Math.Clamp((long)x + width, _clipLeft, Image.Width);
        _clipBottom = (int)Math.Clamp((long)y + height, _clipTop, Image.Height);
    }

    /// <summary>Lets drawing reach the whole image again.</summary>
    /// <example><code>canvas.ResetClip();</code></example>
    public void ResetClip() => (_clipLeft, _clipTop, _clipRight, _clipBottom) = (0, 0, Image.Width, Image.Height);

    /// <summary>Sets every pixel inside the clip to a colour, replacing what was there, alpha included.</summary>
    /// <param name="color">The colour.</param>
    /// <exception cref="ArgumentException">The colour isn't opaque and the image has no alpha.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <example><code>canvas.Clear(new Rgba32(255, 255, 255));</code></example>
    public void Clear(Rgba32 color)
    {
        var painter = new Painter(Image, color);
        if (color.A != 255 && !painter.HasAlpha)
        {
            throw new ArgumentException($"{Image.PixelFormat} has no alpha, so it can't be cleared to a colour that isn't opaque.", nameof(color));
        }

        for (int y = _clipTop; y < _clipBottom; y++)
        {
            painter.Set(Image.GetRowBytes(y), _clipLeft, _clipRight - _clipLeft);
        }
    }

    /// <summary>Fills a shape with a colour.</summary>
    /// <param name="shape">The outline; every figure is closed for filling.</param>
    /// <param name="color">The colour; its alpha makes the fill translucent.</param>
    /// <param name="rule">Which parts of an outline that crosses itself are inside.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The transforms take the shape beyond what a float can hold.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <example><code>canvas.Fill(Shape.Ellipse(50, 50, 20, 20), new Rgba32(0, 0, 0));</code></example>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "A Matrix3x2 and a FillRule can't be mistaken for each other.")]
    public void Fill(Shape shape, Rgba32 color, FillRule rule = FillRule.NonZero) => Fill(shape, color, Matrix3x2.Identity, rule);

    /// <summary>Fills a shape with a colour, through a transform of its own: a glyph's size and position, say.</summary>
    /// <param name="shape">The outline; every figure is closed for filling.</param>
    /// <param name="color">The colour; its alpha makes the fill translucent.</param>
    /// <param name="transform">Applied to the shape before <see cref="Transform"/>.</param>
    /// <param name="rule">Which parts of an outline that crosses itself are inside.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An element of <paramref name="transform"/> isn't finite.</exception>
    /// <exception cref="ArgumentException">The transforms take the shape beyond what a float can hold.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <example><code>canvas.Fill(glyph, black, Matrix3x2.CreateScale(size / 2048f) * Matrix3x2.CreateTranslation(x, y));</code></example>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "A Matrix3x2 and a FillRule can't be mistaken for each other.")]
    public void Fill(Shape shape, Rgba32 color, in Matrix3x2 transform, FillRule rule = FillRule.NonZero)
    {
        ArgumentNullException.ThrowIfNull(shape);
        Finite(transform, nameof(transform));
        if (rule is not (FillRule.NonZero or FillRule.EvenOdd))
        {
            throw new ArgumentOutOfRangeException(nameof(rule), rule, "The rule must be NonZero or EvenOdd.");
        }

        _ = Image.GetPixelBytes();
        if (color.A == 0 || shape.IsEmpty)
        {
            return;
        }

        _rasterizer.Reset();
        _rasterizer.AddShape(shape, transform * _transform, _tolerance);
        Paint(color, rule);
    }

    /// <summary>Fills a rectangle with a colour.</summary>
    /// <param name="x">The left edge, before <see cref="Transform"/>.</param>
    /// <param name="y">The top edge, before <see cref="Transform"/>.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="color">The colour; its alpha makes the fill translucent.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value isn't finite.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <example><code>canvas.FillRectangle(10, 10, 100, 20, new Rgba32(240, 240, 240));</code></example>
    public void FillRectangle(float x, float y, float width, float height, Rgba32 color)
    {
        foreach (float value in (ReadOnlySpan<float>)[x, y, width, height])
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(x), value, "The rectangle must be finite.");
            }
        }

        _ = Image.GetPixelBytes();
        if (color.A == 0)
        {
            return;
        }

        _rasterizer.Reset();
        Vector2 a = Vector2.Transform(new Vector2(x, y), _transform), b = Vector2.Transform(new Vector2(x + width, y), _transform);
        Vector2 c = Vector2.Transform(new Vector2(x + width, y + height), _transform), d = Vector2.Transform(new Vector2(x, y + height), _transform);
        Vector2 low = new(-Rasterizer.Limit), high = new(Rasterizer.Limit);
        (a, b, c, d) = (Vector2.Clamp(a, low, high), Vector2.Clamp(b, low, high), Vector2.Clamp(c, low, high), Vector2.Clamp(d, low, high));
        _rasterizer.AddLine(a, b);
        _rasterizer.AddLine(b, c);
        _rasterizer.AddLine(c, d);
        _rasterizer.AddLine(d, a);
        Paint(color, FillRule.NonZero);
    }

    /// <summary>Draws a straight line with square-cut ends, such as a table rule or an underline.</summary>
    /// <param name="x1">The start's x, before <see cref="Transform"/>.</param>
    /// <param name="y1">The start's y.</param>
    /// <param name="x2">The end's x.</param>
    /// <param name="y2">The end's y.</param>
    /// <param name="width">The width, in the same units; it scales with <see cref="Transform"/>, and is at least <see cref="MinimumStrokeWidth"/> pixels.</param>
    /// <param name="color">The colour; its alpha makes the line translucent.</param>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate isn't finite, or the width is negative or not finite.</exception>
    /// <exception cref="ArgumentException">The transform takes the line beyond what a float can hold.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <example><code>canvas.StrokeLine(40, 120.5f, 560, 120.5f, 0.5f, new Rgba32(0, 0, 0));</code></example>
    public void StrokeLine(float x1, float y1, float x2, float y2, float width, Rgba32 color)
    {
        foreach (float value in (ReadOnlySpan<float>)[x1, y1, x2, y2])
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(x1), value, "The line's ends must be finite.");
            }
        }

        width = Width(width, _transform);
        _ = Image.GetPixelBytes();
        if (color.A == 0)
        {
            return;
        }

        _rasterizer.Reset();
        Stroker.AddLine(_rasterizer, new Vector2(x1, y1), new Vector2(x2, y2), width, _transform);
        Paint(color, FillRule.NonZero);
    }

    /// <summary>
    /// Draws a shape's outline with a round pen: round corners and round ends. A figure is joined back to its start only
    /// when <see cref="Shape.Close"/> ended it. Synthetic bold strokes a glyph's outline as well as filling it.
    /// </summary>
    /// <param name="shape">The outline.</param>
    /// <param name="width">The pen's width in the shape's units; it scales with <see cref="Transform"/>, and is at least <see cref="MinimumStrokeWidth"/> pixels.</param>
    /// <param name="color">The colour; its alpha makes the stroke translucent, without darker overlaps.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The width is negative or not finite.</exception>
    /// <exception cref="ArgumentException">The transform takes the shape beyond what a float can hold.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <example><code>canvas.Stroke(Shape.Ellipse(50, 50, 30, 20), 2, new Rgba32(0, 0, 0));</code></example>
    public void Stroke(Shape shape, float width, Rgba32 color) => Stroke(shape, width, color, Matrix3x2.Identity);

    /// <summary>Draws a shape's outline with a round pen, through a transform of its own.</summary>
    /// <param name="shape">The outline.</param>
    /// <param name="width">The pen's width in the shape's units; it scales with both transforms, and is at least <see cref="MinimumStrokeWidth"/> pixels.</param>
    /// <param name="color">The colour; its alpha makes the stroke translucent, without darker overlaps.</param>
    /// <param name="transform">Applied to the shape before <see cref="Transform"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The width is negative or not finite, or an element of <paramref name="transform"/> isn't.</exception>
    /// <exception cref="ArgumentException">The transforms take the shape beyond what a float can hold.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <example><code>canvas.Stroke(glyph, 0.03f * 2048, black, place); // synthetic bold, with canvas.Fill(glyph, black, place)</code></example>
    public void Stroke(Shape shape, float width, Rgba32 color, in Matrix3x2 transform)
    {
        ArgumentNullException.ThrowIfNull(shape);
        Finite(transform, nameof(transform));
        Matrix3x2 whole = transform * _transform;
        width = Width(width, whole);
        _ = Image.GetPixelBytes();
        if (color.A == 0 || shape.IsEmpty)
        {
            return;
        }

        _rasterizer.Reset();
        Stroker.AddShape(_rasterizer, shape, whole, width, _tolerance);
        Paint(color, FillRule.NonZero);
    }

    /// <summary>Returns the canvas's working memory to the pool. The image stays as drawn.</summary>
    /// <example><code>canvas.Dispose();</code></example>
    public void Dispose() => _rasterizer.Dispose();

    private static void Finite(in Matrix3x2 m, string name)
    {
        if (!(float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M31) && float.IsFinite(m.M32)))
        {
            throw new ArgumentOutOfRangeException(name, "Every element of the transform must be finite.");
        }
    }

    /// <summary>The width to stroke with, in shape units: at least the minimum once the transform has scaled it.</summary>
    private float Width(float width, in Matrix3x2 transform)
    {
        if (!(width >= 0 && float.IsFinite(width)))
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "The width must be finite and not negative.");
        }

        float scale = Stroker.Scale(transform);
        return scale > 0 && width * scale < _minimumStrokeWidth ? _minimumStrokeWidth / scale : width;
    }

    private void Paint(Rgba32 color, FillRule rule)
    {
        var painter = new Painter(Image, color);
        Image image = Image;
        _rasterizer.Render(_clipLeft, _clipTop, _clipRight, _clipBottom, rule, (y, x, coverage) => painter.Blend(image.GetRowBytes(y), x, coverage));
    }

    /// <summary>Writes one colour into rows of any pixel layout, whole or blended by coverage, in the stored values.</summary>
    private readonly struct Painter
    {
        private readonly PixelFormat _format;
        private readonly Vector4 _colour; // straight RGBA from 0 to 1; grey layouts hold luma in X, Y and Z
        private readonly byte[] _solid;    // the colour's bytes in the layout, for fully covered pixels of an opaque colour
        private readonly bool _opaque;

        public Painter(Image image, Rgba32 color)
        {
            _format = image.PixelFormat;
            _colour = new Vector4(color.R, color.G, color.B, color.A) / 255;
            if (_format is PixelFormat.L8 or PixelFormat.L16)
            {
                float luma = (0.2126f * _colour.X) + (0.7152f * _colour.Y) + (0.0722f * _colour.Z);
                _colour = new Vector4(luma, luma, luma, _colour.W);
            }

            _opaque = color.A == 255;
            HasAlpha = _format is PixelFormat.Rgba32 or PixelFormat.Bgra32 or PixelFormat.Rgba64;
            _solid = new byte[image.BytesPerPixel];
            Write(_solid, 0, _colour);
        }

        public bool HasAlpha { get; }

        public void Set(Span<byte> row, int x, int count)
        {
            int size = _solid.Length;
            for (int i = 0; i < count; i++)
            {
                _solid.CopyTo(row.Slice((x + i) * size, size));
            }
        }

        public void Blend(Span<byte> row, int x, ReadOnlySpan<float> coverage)
        {
            int size = _solid.Length;
            for (int i = 0; i < coverage.Length; i++)
            {
                float cover = coverage[i];
                if (cover <= 0)
                {
                    continue;
                }

                int at = (x + i) * size;
                if (cover >= 1 && _opaque)
                {
                    _solid.CopyTo(row.Slice(at, size));
                    continue;
                }

                float alpha = _colour.W * Math.Min(cover, 1);
                Vector4 target = Read(row, at);
                float behind = target.W * (1 - alpha);
                float total = alpha + behind;
                Vector3 mixed = total > 0
                    ? ((new Vector3(_colour.X, _colour.Y, _colour.Z) * alpha) + (new Vector3(target.X, target.Y, target.Z) * behind)) / total
                    : Vector3.Zero;
                Write(row, at, new Vector4(mixed, total));
            }
        }

        private Vector4 Read(ReadOnlySpan<byte> row, int at)
        {
            const float Byte = 1f / 255, Word = 1f / 65535;
            ReadOnlySpan<ushort> wide = _format is PixelFormat.L16 or PixelFormat.Rgb48 or PixelFormat.Rgba64
                ? MemoryMarshal.Cast<byte, ushort>(row.Slice(at, _solid.Length))
                : default;
            return _format switch
            {
                PixelFormat.L8 => new Vector4(row[at] * Byte, row[at] * Byte, row[at] * Byte, 1),
                PixelFormat.L16 => new Vector4(wide[0] * Word, wide[0] * Word, wide[0] * Word, 1),
                PixelFormat.Rgb24 => new Vector4(row[at], row[at + 1], row[at + 2], 255) * Byte,
                PixelFormat.Rgba32 => new Vector4(row[at], row[at + 1], row[at + 2], row[at + 3]) * Byte,
                PixelFormat.Bgra32 => new Vector4(row[at + 2], row[at + 1], row[at], row[at + 3]) * Byte,
                PixelFormat.Rgb48 => new Vector4(wide[0], wide[1], wide[2], 65535) * Word,
                _ => new Vector4(wide[0], wide[1], wide[2], wide[3]) * Word,
            };
        }

        private void Write(Span<byte> row, int at, Vector4 p)
        {
            Span<ushort> wide = _format is PixelFormat.L16 or PixelFormat.Rgb48 or PixelFormat.Rgba64
                ? MemoryMarshal.Cast<byte, ushort>(row.Slice(at, _solid.Length))
                : default;
            switch (_format)
            {
                case PixelFormat.L8:
                    row[at] = To8(p.X);
                    break;
                case PixelFormat.L16:
                    wide[0] = To16(p.X);
                    break;
                case PixelFormat.Rgb24:
                    (row[at], row[at + 1], row[at + 2]) = (To8(p.X), To8(p.Y), To8(p.Z));
                    break;
                case PixelFormat.Rgba32:
                    (row[at], row[at + 1], row[at + 2], row[at + 3]) = (To8(p.X), To8(p.Y), To8(p.Z), To8(p.W));
                    break;
                case PixelFormat.Bgra32:
                    (row[at + 2], row[at + 1], row[at], row[at + 3]) = (To8(p.X), To8(p.Y), To8(p.Z), To8(p.W));
                    break;
                case PixelFormat.Rgb48:
                    (wide[0], wide[1], wide[2]) = (To16(p.X), To16(p.Y), To16(p.Z));
                    break;
                default:
                    (wide[0], wide[1], wide[2], wide[3]) = (To16(p.X), To16(p.Y), To16(p.Z), To16(p.W));
                    break;
            }
        }

        private static byte To8(float v) => (byte)Math.Clamp((v * 255) + 0.5f, 0, 255);

        private static ushort To16(float v) => (ushort)Math.Clamp((v * 65535) + 0.5f, 0, 65535);
    }
}
