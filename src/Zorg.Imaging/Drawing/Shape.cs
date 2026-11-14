using System;
using System.Diagnostics.CodeAnalysis;

namespace Zorg.Imaging.Drawing;

/// <summary>
/// An outline made of straight lines and curves, to fill or stroke on a <see cref="Canvas"/>: one or more figures, each
/// started by <see cref="MoveTo"/>. Build it once and draw it as often as needed, with a different transform each time.
/// </summary>
/// <remarks>
/// Coordinates are in the shape's own units, such as font units for a glyph; the canvas's transform maps them to
/// pixels. Filling closes every figure; stroking leaves a figure open unless <see cref="Close"/> ended it.
/// </remarks>
/// <example>
/// <code>
/// var triangle = new Shape().MoveTo(10, 10).LineTo(90, 10).LineTo(50, 80).Close();
/// canvas.Fill(triangle, new Rgba32(200, 0, 0));
/// </code>
/// </example>
[Experimental(Canvas.Diagnostic)]
public sealed class Shape
{
    internal const byte Move = 0;
    internal const byte Line = 1;
    internal const byte Quad = 2;
    internal const byte Cubic = 3;
    internal const byte End = 4;

    private byte[] _verbs = new byte[16];
    private float[] _points = new float[32];
    private bool _open;

    /// <summary>The number of lines, curves and closes so far. A caller drawing untrusted outlines can refuse one above its limit.</summary>
    /// <example><code>if (glyph.SegmentCount > 10_000) { throw new InvalidDataException("Glyph too complex."); }</code></example>
    public int SegmentCount { get; private set; }

    /// <summary>Whether nothing has been added, or <see cref="Clear"/> emptied it.</summary>
    /// <example><code>if (!shape.IsEmpty) { canvas.Fill(shape, black); }</code></example>
    public bool IsEmpty => VerbCount == 0;

    internal int VerbCount { get; private set; }

    internal int PointCount { get; private set; }

    internal ReadOnlySpan<byte> Verbs => _verbs.AsSpan(0, VerbCount);

    internal ReadOnlySpan<float> Points => _points.AsSpan(0, PointCount * 2);

    /// <summary>A rectangle, drawn clockwise in y-down coordinates.</summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    /// <param name="width">The width; negative mirrors it.</param>
    /// <param name="height">The height; negative mirrors it.</param>
    /// <returns>A new shape with one closed figure.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value isn't finite.</exception>
    /// <example><code>Shape box = Shape.Rectangle(10, 10, 100, 40);</code></example>
    public static Shape Rectangle(float x, float y, float width, float height) =>
        new Shape().MoveTo(x, y).LineTo(x + width, y).LineTo(x + width, y + height).LineTo(x, y + height).Close();

    /// <summary>An ellipse, made of four cubic curves.</summary>
    /// <param name="centerX">The centre's x.</param>
    /// <param name="centerY">The centre's y.</param>
    /// <param name="radiusX">The horizontal radius.</param>
    /// <param name="radiusY">The vertical radius.</param>
    /// <returns>A new shape with one closed figure.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value isn't finite.</exception>
    /// <example><code>Shape dot = Shape.Ellipse(50, 50, 8, 8);</code></example>
    public static Shape Ellipse(float centerX, float centerY, float radiusX, float radiusY)
    {
        // The usual four-arc approximation: control points at 0.5523 of the radius, within 0.03% of a circle.
        const float K = 0.5522847498f;
        float kx = radiusX * K, ky = radiusY * K;
        return new Shape()
            .MoveTo(centerX + radiusX, centerY)
            .CubicTo(centerX + radiusX, centerY + ky, centerX + kx, centerY + radiusY, centerX, centerY + radiusY)
            .CubicTo(centerX - kx, centerY + radiusY, centerX - radiusX, centerY + ky, centerX - radiusX, centerY)
            .CubicTo(centerX - radiusX, centerY - ky, centerX - kx, centerY - radiusY, centerX, centerY - radiusY)
            .CubicTo(centerX + kx, centerY - radiusY, centerX + radiusX, centerY - ky, centerX + radiusX, centerY)
            .Close();
    }

    /// <summary>Starts a new figure at a point.</summary>
    /// <param name="x">The x coordinate.</param>
    /// <param name="y">The y coordinate.</param>
    /// <returns>This shape, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate isn't finite.</exception>
    /// <example><code>shape.MoveTo(0, 0).LineTo(10, 0);</code></example>
    public Shape MoveTo(float x, float y)
    {
        Add(Move, x, y);
        _open = true;
        return this;
    }

    /// <summary>A straight line from the current point.</summary>
    /// <param name="x">The end's x.</param>
    /// <param name="y">The end's y.</param>
    /// <returns>This shape, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate isn't finite.</exception>
    /// <exception cref="InvalidOperationException">No figure has been started with <see cref="MoveTo"/>.</exception>
    /// <example><code>shape.MoveTo(0, 0).LineTo(10, 0);</code></example>
    public Shape LineTo(float x, float y)
    {
        RequireFigure();
        Add(Line, x, y);
        SegmentCount++;
        return this;
    }

    /// <summary>A quadratic Bézier curve from the current point, as TrueType glyphs use.</summary>
    /// <param name="controlX">The control point's x.</param>
    /// <param name="controlY">The control point's y.</param>
    /// <param name="x">The end's x.</param>
    /// <param name="y">The end's y.</param>
    /// <returns>This shape, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate isn't finite.</exception>
    /// <exception cref="InvalidOperationException">No figure has been started with <see cref="MoveTo"/>.</exception>
    /// <example><code>shape.MoveTo(0, 0).QuadTo(50, 100, 100, 0);</code></example>
    public Shape QuadTo(float controlX, float controlY, float x, float y)
    {
        RequireFigure();
        Add(Quad, controlX, controlY, x, y);
        SegmentCount++;
        return this;
    }

    /// <summary>A cubic Bézier curve from the current point, as PDF, SVG and CFF fonts use.</summary>
    /// <param name="control1X">The first control point's x.</param>
    /// <param name="control1Y">The first control point's y.</param>
    /// <param name="control2X">The second control point's x.</param>
    /// <param name="control2Y">The second control point's y.</param>
    /// <param name="x">The end's x.</param>
    /// <param name="y">The end's y.</param>
    /// <returns>This shape, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate isn't finite.</exception>
    /// <exception cref="InvalidOperationException">No figure has been started with <see cref="MoveTo"/>.</exception>
    /// <example><code>shape.MoveTo(0, 0).CubicTo(0, 50, 100, 50, 100, 0);</code></example>
    public Shape CubicTo(float control1X, float control1Y, float control2X, float control2Y, float x, float y)
    {
        RequireFigure();
        Add(Cubic, control1X, control1Y, control2X, control2Y, x, y);
        SegmentCount++;
        return this;
    }

    /// <summary>Closes the current figure with a line back to its start. The next figure needs a <see cref="MoveTo"/>.</summary>
    /// <returns>This shape, for chaining.</returns>
    /// <exception cref="InvalidOperationException">No figure has been started with <see cref="MoveTo"/>.</exception>
    /// <example><code>shape.MoveTo(0, 0).LineTo(10, 0).LineTo(5, 8).Close();</code></example>
    public Shape Close()
    {
        RequireFigure();
        Grow(1, 0);
        _verbs[VerbCount++] = End;
        SegmentCount++;
        _open = false;
        return this;
    }

    /// <summary>Removes everything, keeping the memory for the next outline.</summary>
    /// <returns>This shape, for chaining.</returns>
    /// <example><code>glyph.Clear().MoveTo(0, 0);</code></example>
    public Shape Clear()
    {
        VerbCount = 0;
        PointCount = 0;
        SegmentCount = 0;
        _open = false;
        return this;
    }

    private void RequireFigure()
    {
        if (!_open)
        {
            throw new InvalidOperationException("Start a figure with MoveTo first.");
        }
    }

    private void Add(byte verb, params ReadOnlySpan<float> coordinates)
    {
        foreach (float value in coordinates)
        {
            if (!float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(coordinates), value, "Coordinates must be finite.");
            }
        }

        Grow(1, coordinates.Length / 2);
        _verbs[VerbCount++] = verb;
        coordinates.CopyTo(_points.AsSpan(PointCount * 2));
        PointCount += coordinates.Length / 2;
    }

    private void Grow(int verbs, int points)
    {
        if (VerbCount + verbs > _verbs.Length)
        {
            Array.Resize(ref _verbs, Math.Max(_verbs.Length * 2, VerbCount + verbs));
        }

        if ((PointCount + points) * 2 > _points.Length)
        {
            Array.Resize(ref _points, Math.Max(_points.Length * 2, (PointCount + points) * 2));
        }
    }
}
