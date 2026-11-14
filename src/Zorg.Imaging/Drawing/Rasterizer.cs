using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;

namespace Zorg.Imaging.Drawing;

/// <summary>
/// Exact-area coverage of filled outlines by signed-area accumulation (the method of font-rs and stb_truetype): every
/// line segment adds, to the cells of the rows it crosses, the area it covers to its right; a running sum along each row
/// then gives each pixel's winding number integrated over its area. Curves are flattened first, in pixels.
/// </summary>
internal sealed class Rasterizer : IDisposable
{
    /// <summary>Larger coordinates are clamped: far off any canvas, and where a float still has an eighth of a pixel.</summary>
    internal const float Limit = 1 << 20;

    /// <summary>The most lines a curve is cut into.</summary>
    internal const int MaxSteps = 1024;

    /// <summary>The most accumulation cells rendered at once: 4 MB, whatever the canvas size.</summary>
    internal const int MaxCells = 1 << 20;

    private float[] _lines = new float[256]; // x0, y0, x1, y1 per line, in pixels
    private int _lineCount;
    private float[]? _cells;
    private float[]? _coverage;

    public float MinX { get; private set; }

    public float MinY { get; private set; }

    public float MaxX { get; private set; }

    public float MaxY { get; private set; }

    public int LineCount => _lineCount;

    /// <summary>The stroker's working list of one figure's points, kept so a page of strokes allocates it once.</summary>
    internal List<Vector2> Figure { get; } = [];

    /// <summary>The stroker's inner corner points of one figure, and which side each is on.</summary>
    internal List<Vector2> Inner { get; } = [];

    /// <summary>For each point in <see cref="Inner"/>: 1 left of the path, -1 right, 0 none.</summary>
    internal List<sbyte> InnerSide { get; } = [];

    public void Reset()
    {
        _lineCount = 0;
        MinX = MinY = float.MaxValue;
        MaxX = MaxY = float.MinValue;
    }

    /// <summary>Adds a shape's outline, transformed to pixels; every figure is closed, as filling needs.</summary>
    public void AddShape(Shape shape, in Matrix3x2 transform, float tolerance)
    {
        ReadOnlySpan<byte> verbs = shape.Verbs;
        ReadOnlySpan<float> points = shape.Points;
        int p = 0;
        Vector2 start = default, current = default;
        bool open = false;
        foreach (byte verb in verbs)
        {
            switch (verb)
            {
                case Shape.Move:
                    if (open)
                    {
                        AddLine(current, start);
                    }

                    start = current = Point(points, p++, transform);
                    open = true;
                    break;
                case Shape.Line:
                    Vector2 to = Point(points, p++, transform);
                    AddLine(current, to);
                    current = to;
                    break;
                case Shape.Quad:
                    Vector2 control = Point(points, p++, transform), end = Point(points, p++, transform);
                    AddQuad(current, control, end, tolerance);
                    current = end;
                    break;
                case Shape.Cubic:
                    Vector2 c1 = Point(points, p++, transform), c2 = Point(points, p++, transform), last = Point(points, p++, transform);
                    AddCubic(current, c1, c2, last, tolerance);
                    current = last;
                    break;
                default: // End
                    AddLine(current, start);
                    current = start;
                    open = false;
                    break;
            }
        }

        if (open)
        {
            AddLine(current, start);
        }
    }

    public void AddLine(Vector2 from, Vector2 to)
    {
        if (from.Y == to.Y)
        {
            return; // a level line covers no rows
        }

        if ((_lineCount + 1) * 4 > _lines.Length)
        {
            Array.Resize(ref _lines, _lines.Length * 2);
        }

        Span<float> line = _lines.AsSpan(_lineCount++ * 4, 4);
        (line[0], line[1], line[2], line[3]) = (from.X, from.Y, to.X, to.Y);
        MinX = Math.Min(MinX, Math.Min(from.X, to.X));
        MaxX = Math.Max(MaxX, Math.Max(from.X, to.X));
        MinY = Math.Min(MinY, Math.Min(from.Y, to.Y));
        MaxY = Math.Max(MaxY, Math.Max(from.Y, to.Y));
    }

    public void AddQuad(Vector2 p0, Vector2 p1, Vector2 p2, float tolerance)
    {
        // After n equal steps the chord is at most |p0 - 2 p1 + p2| / (4 n^2) from the curve.
        float bend = (p0 - (2 * p1) + p2).Length();
        int n = Steps(bend / (4 * tolerance));
        Vector2 previous = p0;
        for (int i = 1; i <= n; i++)
        {
            float t = (float)i / n, u = 1 - t;
            Vector2 next = i == n ? p2 : (u * u * p0) + (2 * u * t * p1) + (t * t * p2);
            AddLine(previous, next);
            previous = next;
        }
    }

    public void AddCubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float tolerance)
    {
        // After n equal steps the chord is at most 3/4 max(|p0 - 2 p1 + p2|, |p1 - 2 p2 + p3|) / n^2 from the curve.
        float bend = Math.Max((p0 - (2 * p1) + p2).Length(), (p1 - (2 * p2) + p3).Length());
        int n = Steps(0.75f * bend / tolerance);
        Vector2 previous = p0;
        for (int i = 1; i <= n; i++)
        {
            float t = (float)i / n, u = 1 - t;
            Vector2 next = i == n ? p3 : (u * u * u * p0) + (3 * u * u * t * p1) + (3 * u * t * t * p2) + (t * t * t * p3);
            AddLine(previous, next);
            previous = next;
        }
    }

    /// <summary>
    /// Computes the coverage of the lines added so far inside the clip, and hands each row's coverage, from 0 to 1, to
    /// <paramref name="paint"/> with the row's pixel position. Tall boxes are done in bands of rows, so the working
    /// memory stays under <see cref="MaxCells"/> floats whatever the canvas size.
    /// </summary>
    public void Render<TPaint>(int clipLeft, int clipTop, int clipRight, int clipBottom, FillRule rule, ref TPaint paint)
        where TPaint : ICoverageRow, allows ref struct
    {
        if (_lineCount == 0)
        {
            return;
        }

        // The box: the outline's extent within the clip. Lines left of it still count (they carry winding in); lines
        // right of it can't change any pixel in it.
        int left = clipLeft;
        int top = Math.Max(clipTop, (int)Math.Floor(Math.Max(MinY, clipTop - 1)));
        int right = Math.Min(clipRight, (int)Math.Ceiling(Math.Min(MaxX, clipRight + 1)));
        int bottom = Math.Min(clipBottom, (int)Math.Ceiling(Math.Min(MaxY, clipBottom + 1)));
        left = Math.Max(left, Math.Min(right, (int)Math.Floor(Math.Max(MinX, clipLeft - 1))));
        if (left >= right || top >= bottom)
        {
            return;
        }

        int width = right - left, stride = width + 2;
        int band = Math.Clamp(MaxCells / stride, 1, bottom - top);
        int cells = stride * band;
        if (_cells is null || _cells.Length < cells)
        {
            if (_cells is not null)
            {
                ArrayPool<float>.Shared.Return(_cells);
            }

            _cells = ArrayPool<float>.Shared.Rent(cells);
        }

        if (_coverage is null || _coverage.Length < width)
        {
            if (_coverage is not null)
            {
                ArrayPool<float>.Shared.Return(_coverage);
            }

            _coverage = ArrayPool<float>.Shared.Rent(width);
        }

        Span<float> coverage = _coverage.AsSpan(0, width);
        for (int bandTop = top; bandTop < bottom; bandTop += band)
        {
            int height = Math.Min(band, bottom - bandTop);
            Span<float> area = _cells.AsSpan(0, stride * height);
            area.Clear();
            for (int i = 0; i < _lineCount; i++)
            {
                ReadOnlySpan<float> line = _lines.AsSpan(i * 4, 4);
                float y0 = line[1] - bandTop, y1 = line[3] - bandTop;
                if (Math.Max(y0, y1) <= 0 || Math.Min(y0, y1) >= height)
                {
                    continue; // another band's
                }

                Clip(area, stride, width, height, line[0] - left, y0, line[2] - left, y1);
            }

            for (int y = 0; y < height; y++)
            {
                if (Sum(area.Slice(y * stride, width), coverage, rule))
                {
                    paint.Paint(bandTop + y, left, coverage);
                }
            }
        }
    }

    /// <summary>The running sum along a row, as coverage; whether any pixel is covered.</summary>
    private static bool Sum(ReadOnlySpan<float> row, Span<float> coverage, FillRule rule)
    {
        float sum = 0, any = 0;
        if (rule == FillRule.NonZero)
        {
            for (int x = 0; x < row.Length; x++)
            {
                sum += row[x];
                float value = Math.Min(1, Math.Abs(sum));
                coverage[x] = value;
                any = Math.Max(any, value);
            }
        }
        else
        {
            for (int x = 0; x < row.Length; x++)
            {
                sum += row[x];
                float value = EvenOdd(sum);
                coverage[x] = value;
                any = Math.Max(any, value);
            }
        }

        return any > 0;
    }

    public void Dispose()
    {
        if (_cells is not null)
        {
            ArrayPool<float>.Shared.Return(_cells);
            _cells = null;
        }

        if (_coverage is not null)
        {
            ArrayPool<float>.Shared.Return(_coverage);
            _coverage = null;
        }
    }

    private static float EvenOdd(float winding)
    {
        float folded = Math.Abs(winding) % 2;
        return folded > 1 ? 2 - folded : folded;
    }

    internal static int Steps(float squared) =>
        float.IsFinite(squared) ? Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(squared)), 1, MaxSteps) : MaxSteps;

    private static Vector2 Point(ReadOnlySpan<float> points, int index, in Matrix3x2 transform) =>
        ToPixels(new Vector2(points[index * 2], points[(index * 2) + 1]), transform);

    /// <summary>A point in pixels: transformed, refused when the transform overflowed, clamped to <see cref="Limit"/>.</summary>
    internal static Vector2 ToPixels(Vector2 point, in Matrix3x2 transform)
    {
        Vector2 p = Vector2.Transform(point, transform);
        if (float.IsNaN(p.X) || float.IsNaN(p.Y))
        {
            throw new ArgumentException("The transform takes the shape beyond what a float can hold.", nameof(transform));
        }

        return Vector2.Clamp(p, new Vector2(-Limit), new Vector2(Limit));
    }

    /// <summary>Splits a line at the box's left and right edges: the part left of it runs down the left edge, the part right of it is dropped.</summary>
    private static void Clip(Span<float> area, int stride, int width, int height, float x0, float y0, float x1, float y1)
    {
        if (x0 >= width && x1 >= width)
        {
            return;
        }

        if (x0 <= 0 && x1 <= 0)
        {
            Accumulate(area, stride, height, 0, y0, 0, y1);
            return;
        }

        if ((x0 < 0) != (x1 < 0))
        {
            float y = y0 + ((y1 - y0) * (0 - x0) / (x1 - x0));
            Clip(area, stride, width, height, x0, y0, 0, y);
            Clip(area, stride, width, height, 0, y, x1, y1);
            return;
        }

        if ((x0 > width) != (x1 > width))
        {
            float y = y0 + ((y1 - y0) * (width - x0) / (x1 - x0));
            Clip(area, stride, width, height, x0, y0, width, y);
            Clip(area, stride, width, height, width, y, x1, y1);
            return;
        }

        Accumulate(area, stride, height, Math.Clamp(x0, 0, width), y0, Math.Clamp(x1, 0, width), y1);
    }

    /// <summary>Adds one line, with x from 0 to the box's width, to the cells of the rows it crosses.</summary>
    private static void Accumulate(Span<float> area, int stride, int height, float x0, float y0, float x1, float y1)
    {
        if (y0 == y1)
        {
            return;
        }

        float direction = 1;
        if (y0 > y1)
        {
            (x0, y0, x1, y1) = (x1, y1, x0, y0);
            direction = -1;
        }

        float dxdy = (x1 - x0) / (y1 - y0);
        float x = x0;
        int first = Math.Max(0, (int)MathF.Floor(y0));
        if (y0 < first)
        {
            x -= (y0 - first) * dxdy; // move to the top of the first row inside
        }

        int last = Math.Min(height, (int)MathF.Ceiling(y1));
        int maxCell = stride - 1;
        float maxX = stride - 2; // the box's width
        x = Math.Clamp(x, 0, maxX);
        for (int y = first; y < last; y++)
        {
            Span<float> row = area.Slice(y * stride, stride);
            float dy = Math.Min(y + 1, y1) - Math.Max(y, y0);

            // Stepping along a steep line can round a hair outside the box; that hair must not leave the row.
            float next = Math.Clamp(x + (dxdy * dy), 0, maxX);
            float d = dy * direction;
            float left = Math.Min(x, next), right = Math.Max(x, next);
            float leftFloor = MathF.Floor(left);
            int leftCell = Math.Min((int)leftFloor, maxCell - 1);
            int rightCell = Math.Min((int)MathF.Ceiling(right), maxCell);
            if (rightCell <= leftCell + 1)
            {
                // Within one pixel column: the part right of the line's middle is covered.
                float middle = (0.5f * (x + next)) - leftFloor;
                row[leftCell] += d - (d * middle);
                row[leftCell + 1] += d * middle;
            }
            else
            {
                float s = 1 / (right - left);
                float leftFraction = left - leftFloor;
                float a0 = 0.5f * s * (1 - leftFraction) * (1 - leftFraction);
                float rightFraction = right - rightCell + 1;
                float am = 0.5f * s * rightFraction * rightFraction;
                row[leftCell] += d * a0;
                if (rightCell == leftCell + 2)
                {
                    row[leftCell + 1] += d * (1 - a0 - am);
                }
                else
                {
                    float a1 = s * (1.5f - leftFraction);
                    row[leftCell + 1] += d * (a1 - a0);
                    for (int cell = leftCell + 2; cell < rightCell - 1; cell++)
                    {
                        row[cell] += d * s;
                    }

                    float a2 = a1 + ((rightCell - leftCell - 3) * s);
                    row[rightCell - 1] += d * (1 - a2 - am);
                }

                row[rightCell] += d * am;
            }

            x = next;
        }
    }
}

/// <summary>Receives rows of coverage.</summary>
internal interface ICoverageRow
{
    /// <summary>One row of coverage: the pixel row, the first pixel's x and a value from 0 to 1 per pixel.</summary>
    public void Paint(int y, int x, ReadOnlySpan<float> coverage);
}
