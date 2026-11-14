using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

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
/// It isn't safe to use from several threads at once.
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
public sealed class Canvas : IDisposable
{
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
    /// visible, as PDF viewers draw it. A pen the transforms make narrower than this in every direction is drawn this
    /// many pixels wide, round, whichever way the transforms stretch. 0 draws every stroke at its own width, however thin.
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
        _ = Image.GetPixelBytes();
        var painter = new Painter(Image, color);
        if (color.A != 255 && !painter.HasAlpha)
        {
            throw new ArgumentException($"{Image.PixelFormat} has no alpha, so it can't be cleared to a colour that isn't opaque.", nameof(color));
        }

        if (_clipLeft >= _clipRight || _clipTop >= _clipBottom)
        {
            return;
        }

        // One row of the colour, made by doubling, then copied down.
        int size = Image.BytesPerPixel;
        Span<byte> first = Image.GetRowBytes(_clipTop).Slice(_clipLeft * size, (_clipRight - _clipLeft) * size);
        painter.CopySolidTo(first);
        for (int done = size; done < first.Length; done *= 2)
        {
            first[..Math.Min(done, first.Length - done)].CopyTo(first[done..]);
        }

        for (int y = _clipTop + 1; y < _clipBottom; y++)
        {
            first.CopyTo(Image.GetRowBytes(y)[(_clipLeft * size)..]);
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
    /// <exception cref="ArgumentException">The transform takes the rectangle beyond what a float can hold.</exception>
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
        Vector2 a = Rasterizer.ToPixels(new Vector2(x, y), _transform), b = Rasterizer.ToPixels(new Vector2(x + width, y), _transform);
        Vector2 c = Rasterizer.ToPixels(new Vector2(x + width, y + height), _transform), d = Rasterizer.ToPixels(new Vector2(x, y + height), _transform);
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

        (float pen, bool inPixels) = Pen(width, _transform);
        _ = Image.GetPixelBytes();
        if (color.A == 0 || pen == 0)
        {
            return;
        }

        _rasterizer.Reset();
        Vector2 from = new(x1, y1), to = new(x2, y2);
        if (inPixels)
        {
            Stroker.AddLine(_rasterizer, Rasterizer.ToPixels(from, _transform), Rasterizer.ToPixels(to, _transform), pen, Matrix3x2.Identity);
        }
        else
        {
            Stroker.AddLine(_rasterizer, from, to, pen, _transform);
        }
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
        (float pen, bool inPixels) = Pen(width, whole);
        _ = Image.GetPixelBytes();
        if (color.A == 0 || shape.IsEmpty || pen == 0)
        {
            return;
        }

        _rasterizer.Reset();
        Stroker.AddShape(_rasterizer, shape, whole, pen, _tolerance, inPixels);
        Paint(color, FillRule.NonZero);
    }

    /// <summary>Draws an image into a rectangle, stretched to fill it.</summary>
    /// <param name="image">The image to draw. It isn't changed.</param>
    /// <param name="x">The rectangle's left edge, before <see cref="Transform"/>.</param>
    /// <param name="y">The rectangle's top edge.</param>
    /// <param name="width">The rectangle's width.</param>
    /// <param name="height">The rectangle's height.</param>
    /// <param name="resampler">
    /// How a smaller image is made first, when the rectangle is smaller than the image; <see langword="null"/> for
    /// <see cref="Resampler.Lanczos3"/>, in linear light as <see cref="Image.Resize"/> does. Otherwise pixels are sampled
    /// bilinearly, or, with <see cref="Resampler.Nearest"/>, as blocks.
    /// </param>
    /// <param name="opacity">How opaque the image is drawn, more than 0 and at most 1.</param>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A value isn't finite, or <paramref name="opacity"/> is out of range.</exception>
    /// <exception cref="ArgumentException"><paramref name="image"/> has another colour space than the canvas.</exception>
    /// <exception cref="ObjectDisposedException">Either image has been disposed.</exception>
    /// <example><code>canvas.DrawImage(photo, 40, 300, 200, 150);</code></example>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Four floats and a Matrix3x2 can't be mistaken for each other.")]
    public void DrawImage(Image image, float x, float y, float width, float height, Resampler? resampler = null, double opacity = 1)
    {
        ArgumentNullException.ThrowIfNull(image);
        foreach (float value in (ReadOnlySpan<float>)[x, y, width, height])
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(x), value, "The rectangle must be finite.");
            }
        }

        _ = image.GetPixelBytes();
        DrawImage(image, Matrix3x2.CreateScale(width / image.Width, height / image.Height) * Matrix3x2.CreateTranslation(x, y), resampler, opacity);
    }

    /// <summary>
    /// Draws an image through a transform from its pixels (0 to its width across, 0 to its height down) to the canvas's
    /// units: scaled, turned, sheared or mirrored, with anti-aliased edges.
    /// </summary>
    /// <param name="image">The image to draw. It isn't changed.</param>
    /// <param name="transform">From the image's pixels to the canvas's units, applied before <see cref="Transform"/>.</param>
    /// <param name="resampler">
    /// How a smaller image is made first, when the transform shrinks it; <see langword="null"/> for
    /// <see cref="Resampler.Lanczos3"/>. Otherwise pixels are sampled bilinearly, or, with <see cref="Resampler.Nearest"/>,
    /// as blocks.
    /// </param>
    /// <param name="opacity">How opaque the image is drawn, more than 0 and at most 1.</param>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An element of <paramref name="transform"/> isn't finite, or <paramref name="opacity"/> is out of range.</exception>
    /// <exception cref="ArgumentException"><paramref name="image"/> has another colour space than the canvas, or the transform overflows a float.</exception>
    /// <exception cref="ObjectDisposedException">Either image has been disposed.</exception>
    /// <example><code>canvas.DrawImage(logo, Matrix3x2.CreateRotation(0.2f) * Matrix3x2.CreateTranslation(100, 50));</code></example>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Four floats and a Matrix3x2 can't be mistaken for each other.")]
    public void DrawImage(Image image, in Matrix3x2 transform, Resampler? resampler = null, double opacity = 1)
    {
        ArgumentNullException.ThrowIfNull(image);
        Finite(transform, nameof(transform));
        if (!(opacity > 0 && opacity <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(opacity), opacity, "The opacity must be more than 0 and at most 1.");
        }

        _ = image.GetPixelBytes();
        _ = Image.GetPixelBytes();
        if (ReferenceEquals(image, Image))
        {
            throw new ArgumentException("An image can't be drawn onto itself; draw a Clone of it.", nameof(image));
        }

        if (!image.ColorSpace.Equals(Image.ColorSpace))
        {
            throw new ArgumentException($"The image is {image.ColorSpace} and the canvas {Image.ColorSpace}. Call ToSrgb on the image first.", nameof(image));
        }

        Matrix3x2 whole = transform * _transform;
        bool nearest = ReferenceEquals(resampler, Resampler.Nearest);

        // Shrinking: make a smaller image first, about the size it will cover, so no pixel is skipped.
        float across = new Vector2(whole.M11, whole.M12).Length() * image.Width;
        float down = new Vector2(whole.M21, whole.M22).Length() * image.Height;
        int width = image.Width, height = image.Height;
        if (!nearest && (across < width * 0.99f || down < height * 0.99f))
        {
            width = (int)Math.Clamp(MathF.Ceiling(Math.Min(across, width)), 1, width);
            height = (int)Math.Clamp(MathF.Ceiling(Math.Min(down, height)), 1, height);
        }

        Image? smaller = width == image.Width && height == image.Height ? null : image.Resize(width, height, resampler);
        try
        {
            Image source = smaller ?? image;
            Matrix3x2 place = Matrix3x2.CreateScale((float)image.Width / width, (float)image.Height / height) * whole;
            if (!Matrix3x2.Invert(place, out Matrix3x2 back))
            {
                return; // squashed to a line: covers nothing
            }

            _rasterizer.Reset();
            Vector2 a = Rasterizer.ToPixels(Vector2.Zero, place), b = Rasterizer.ToPixels(new Vector2(width, 0), place);
            Vector2 c = Rasterizer.ToPixels(new Vector2(width, height), place), d = Rasterizer.ToPixels(new Vector2(0, height), place);
            _rasterizer.AddLine(a, b);
            _rasterizer.AddLine(b, c);
            _rasterizer.AddLine(c, d);
            _rasterizer.AddLine(d, a);
            var painter = new ImagePainter(source, Image, back, nearest, (float)opacity);
            _rasterizer.Render(_clipLeft, _clipTop, _clipRight, _clipBottom, FillRule.NonZero, ref painter);
        }
        finally
        {
            smaller?.Dispose();
        }
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
    /// <summary>
    /// The pen to stroke with: the width in shape units, or, when the transform makes it narrower than the minimum in
    /// every direction, the minimum in pixels, so a hairline is a pixel wide whichever way the transform stretches.
    /// </summary>
    private (float Width, bool InPixels) Pen(float width, in Matrix3x2 transform)
    {
        if (!(width >= 0 && float.IsFinite(width)))
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "The width must be finite and not negative.");
        }

        return width * Stroker.Stretch(transform) < _minimumStrokeWidth ? (_minimumStrokeWidth, true) : (width, false);
    }

    private void Paint(Rgba32 color, FillRule rule)
    {
        var painter = new Painter(Image, color);
        _rasterizer.Render(_clipLeft, _clipTop, _clipRight, _clipBottom, rule, ref painter);
    }

    /// <summary>Up to eight bytes of one pixel, held inline.</summary>
    [InlineArray(8)]
    private struct PixelBytes
    {
        private byte _first;
    }

    /// <summary>Writes one colour into rows of any pixel layout, whole or blended by coverage, in the stored values.</summary>
    private struct Painter : ICoverageRow
    {
        private readonly Image _image;
        private readonly PixelFormat _format;
        private readonly int _size;
        private readonly Vector4 _colour; // straight RGBA from 0 to 1; grey layouts hold luma in X, Y and Z
        private readonly Vector3 _layout; // the colour channels from 0 to 255 in the layout's byte order, for 8-bit layouts
        private readonly bool _opaque;
        private PixelBytes _solid; // the colour in the layout, for fully covered pixels of an opaque colour

        public Painter(Image image, Rgba32 color)
        {
            _image = image;
            _format = image.PixelFormat;
            _size = image.BytesPerPixel;
            _colour = new Vector4(color.R, color.G, color.B, color.A) / 255;
            if (_format is PixelFormat.L8 or PixelFormat.L16)
            {
                float luma = (0.2126f * _colour.X) + (0.7152f * _colour.Y) + (0.0722f * _colour.Z);
                _colour = new Vector4(luma, luma, luma, _colour.W);
            }

            _layout = _format == PixelFormat.Bgra32
                ? new Vector3(_colour.Z, _colour.Y, _colour.X) * 255
                : new Vector3(_colour.X, _colour.Y, _colour.Z) * 255;
            _opaque = color.A == 255;
            HasAlpha = _format is PixelFormat.Rgba32 or PixelFormat.Bgra32 or PixelFormat.Rgba64;
            Pixels.Write(_format, _solid, 0, _colour);
        }

        public bool HasAlpha { get; }

        public readonly void CopySolidTo(Span<byte> pixel) => ((ReadOnlySpan<byte>)_solid)[.._size].CopyTo(pixel);

        public readonly void Paint(int y, int x, ReadOnlySpan<float> coverage)
        {
            Span<byte> row = _image.GetRowBytes(y);
            switch (_format)
            {
                case PixelFormat.Rgb24:
                case PixelFormat.L8:
                    Opaque8(row, x, coverage);
                    break;
                case PixelFormat.Rgba32:
                case PixelFormat.Bgra32:
                    Alpha8(row, x, coverage);
                    break;
                default:
                    General(row, x, coverage);
                    break;
            }
        }

        /// <summary>A byte moved a share <paramref name="a"/> of the way towards <paramref name="to"/> (0 to 255), rounded.</summary>
        private static byte Mix(byte from, float to, float a) => (byte)(from + ((to - from) * a) + 0.5f);

        /// <summary>RGB or grey bytes: always opaque underneath, so blending is a mix towards the colour.</summary>
        private readonly void Opaque8(Span<byte> row, int x, ReadOnlySpan<float> coverage)
        {
            ReadOnlySpan<byte> solid = ((ReadOnlySpan<byte>)_solid)[.._size];
            int size = _size;
            float c0 = _layout.X, c1 = _layout.Y, c2 = _layout.Z, alpha = _colour.W;
            for (int i = 0; i < coverage.Length; i++)
            {
                float cover = coverage[i];
                if (cover <= 0)
                {
                    continue;
                }

                Span<byte> pixel = row.Slice((x + i) * size, size);
                float a = alpha * cover;
                if (a >= 1)
                {
                    solid.CopyTo(pixel);
                }
                else if (size == 1)
                {
                    pixel[0] = Mix(pixel[0], c0, a);
                }
                else
                {
                    (pixel[0], pixel[1], pixel[2]) = (Mix(pixel[0], c0, a), Mix(pixel[1], c1, a), Mix(pixel[2], c2, a));
                }
            }
        }

        /// <summary>RGBA or BGRA bytes: a mix where the pixel underneath is opaque, source-over otherwise.</summary>
        private readonly void Alpha8(Span<byte> row, int x, ReadOnlySpan<float> coverage)
        {
            ReadOnlySpan<byte> solid = ((ReadOnlySpan<byte>)_solid)[.._size];
            float c0 = _layout.X, c1 = _layout.Y, c2 = _layout.Z, alpha = _colour.W;
            for (int i = 0; i < coverage.Length; i++)
            {
                float cover = coverage[i];
                if (cover <= 0)
                {
                    continue;
                }

                Span<byte> pixel = row.Slice((x + i) * 4, 4);
                float a = alpha * cover;
                if (a >= 1)
                {
                    solid.CopyTo(pixel);
                }
                else if (pixel[3] == 255)
                {
                    (pixel[0], pixel[1], pixel[2]) = (Mix(pixel[0], c0, a), Mix(pixel[1], c1, a), Mix(pixel[2], c2, a));
                }
                else
                {
                    float behind = pixel[3] / 255f * (1 - a);
                    float total = a + behind;
                    if (total > 0)
                    {
                        float scale = 1 / total;
                        pixel[0] = (byte)((((c0 * a) + (pixel[0] * behind)) * scale) + 0.5f);
                        pixel[1] = (byte)((((c1 * a) + (pixel[1] * behind)) * scale) + 0.5f);
                        pixel[2] = (byte)((((c2 * a) + (pixel[2] * behind)) * scale) + 0.5f);
                    }

                    pixel[3] = (byte)((total * 255) + 0.5f);
                }
            }
        }

        private readonly void General(Span<byte> row, int x, ReadOnlySpan<float> coverage)
        {
            ReadOnlySpan<byte> solid = ((ReadOnlySpan<byte>)_solid)[.._size];
            int size = _size;
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
                    solid.CopyTo(row.Slice(at, size));
                    continue;
                }

                float alpha = _colour.W * Math.Min(cover, 1);
                Vector4 target = Pixels.Read(_format, row, at);
                float behind = target.W * (1 - alpha);
                float total = alpha + behind;
                Vector3 mixed = total > 0
                    ? ((new Vector3(_colour.X, _colour.Y, _colour.Z) * alpha) + (new Vector3(target.X, target.Y, target.Z) * behind)) / total
                    : Vector3.Zero;
                Pixels.Write(_format, row, at, new Vector4(mixed, total));
            }
        }
    }

    /// <summary>Blends an image, sampled through the inverse transform at each pixel's centre, into rows of the canvas.</summary>
    private readonly struct ImagePainter(Image source, Image canvas, Matrix3x2 back, bool nearest, float opacity) : ICoverageRow
    {
        private readonly PixelFormat _target = canvas.PixelFormat;
        private readonly int _size = canvas.BytesPerPixel;
        private readonly bool _grey = canvas.PixelFormat is PixelFormat.L8 or PixelFormat.L16;

        public void Paint(int y, int left, ReadOnlySpan<float> coverage)
        {
            Span<byte> row = canvas.GetRowBytes(y);
            PixelFormat target = _target;
            for (int i = 0; i < coverage.Length; i++)
            {
                float cover = coverage[i];
                if (cover <= 0)
                {
                    continue;
                }

                Vector2 at = Vector2.Transform(new Vector2(left + i + 0.5f, y + 0.5f), back);
                Vector4 colour = nearest ? Nearest(at) : Bilinear(at);
                if (_grey)
                {
                    float luma = (0.2126f * colour.X) + (0.7152f * colour.Y) + (0.0722f * colour.Z);
                    colour = new Vector4(luma, luma, luma, colour.W);
                }

                int offset = (left + i) * _size;
                float alpha = colour.W * Math.Min(cover, 1) * opacity;
                if (alpha >= 1)
                {
                    Pixels.Write(target, row, offset, colour);
                    continue;
                }

                Vector4 under = Pixels.Read(target, row, offset);
                float behind = under.W * (1 - alpha);
                float total = alpha + behind;
                Vector3 mixed = total > 0
                    ? ((new Vector3(colour.X, colour.Y, colour.Z) * alpha) + (new Vector3(under.X, under.Y, under.Z) * behind)) / total
                    : Vector3.Zero;
                Pixels.Write(target, row, offset, new Vector4(mixed, total));
            }
        }

        private Vector4 Pixel(int x, int y)
        {
            x = Math.Clamp(x, 0, source.Width - 1);
            y = Math.Clamp(y, 0, source.Height - 1);
            return Pixels.Read(source.PixelFormat, source.GetRowBytes(y), x * source.BytesPerPixel);
        }

        private Vector4 Nearest(Vector2 at) => Pixel((int)MathF.Floor(at.X), (int)MathF.Floor(at.Y));

        /// <summary>Bilinear between the four nearest pixel centres, premultiplied so transparent pixels lend no colour.</summary>
        private Vector4 Bilinear(Vector2 at)
        {
            float u = at.X - 0.5f, v = at.Y - 0.5f;
            int x0 = (int)MathF.Floor(u), y0 = (int)MathF.Floor(v);
            float fx = u - x0, fy = v - y0;
            Vector4 sum = (Premultiplied(Pixel(x0, y0)) * (1 - fx) * (1 - fy)) + (Premultiplied(Pixel(x0 + 1, y0)) * fx * (1 - fy))
                + (Premultiplied(Pixel(x0, y0 + 1)) * (1 - fx) * fy) + (Premultiplied(Pixel(x0 + 1, y0 + 1)) * fx * fy);
            return sum.W > 0 ? new Vector4(new Vector3(sum.X, sum.Y, sum.Z) / sum.W, sum.W) : Vector4.Zero;
        }

        private static Vector4 Premultiplied(Vector4 p) => new(p.X * p.W, p.Y * p.W, p.Z * p.W, p.W);
    }
}
