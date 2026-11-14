using System;
using System.Collections.Generic;
using System.Numerics;

namespace Zorg.Imaging.Drawing;

/// <summary>
/// Turns an outline into the area a round pen covers along it: a rectangle for every straight piece and a disc at every
/// corner and end, all wound the same way so a non-zero fill unites them. Built in the shape's own units, so the width
/// transforms with the shape as PDF's line width does, then handed to the rasteriser in pixels.
/// </summary>
internal static class Stroker
{
    /// <summary>The fewest sides a round corner gets, however narrow the pen.</summary>
    private const int MinSides = 12;

    /// <summary>The most sides a round corner gets, however wide the pen.</summary>
    private const int MaxSides = 128;

    /// <summary>How much a transform enlarges lengths, on average over directions: the square root of its area scale.</summary>
    public static float Scale(in Matrix3x2 transform) => MathF.Sqrt(MathF.Abs(transform.GetDeterminant()));

    public static void AddShape(Rasterizer rasterizer, Shape shape, in Matrix3x2 transform, float width, float tolerance)
    {
        float scale = Scale(transform);
        if (!(scale > 0) || !float.IsFinite(scale))
        {
            return; // a transform that flattens everything to a line covers nothing
        }

        float half = width / 2;
        float flatness = tolerance / scale;
        int sides = Sides(half * scale, tolerance / 4); // finer than the curves: a disc's corners show where it meets the sides
        var figure = new List<Vector2>();
        ReadOnlySpan<byte> verbs = shape.Verbs;
        ReadOnlySpan<float> points = shape.Points;
        int p = 0;
        foreach (byte verb in verbs)
        {
            switch (verb)
            {
                case Shape.Move:
                    AddFigure(rasterizer, figure, closed: false, half, sides, transform);
                    figure.Clear();
                    figure.Add(Point(points, p++));
                    break;
                case Shape.Line:
                    figure.Add(Point(points, p++));
                    break;
                case Shape.Quad:
                    Vector2 control = Point(points, p++), end = Point(points, p++);
                    Quad(figure, figure[^1], control, end, flatness);
                    break;
                case Shape.Cubic:
                    Vector2 c1 = Point(points, p++), c2 = Point(points, p++), last = Point(points, p++);
                    Cubic(figure, figure[^1], c1, c2, last, flatness);
                    break;
                default: // End
                    AddFigure(rasterizer, figure, closed: true, half, sides, transform);
                    figure.Clear();
                    break;
            }
        }

        AddFigure(rasterizer, figure, closed: false, half, sides, transform);
    }

    /// <summary>A straight line with square-cut ends: one rectangle.</summary>
    public static void AddLine(Rasterizer rasterizer, Vector2 from, Vector2 to, float width, in Matrix3x2 transform) =>
        Rectangle(rasterizer, from, to, width / 2, transform);

    /// <summary>Enough sides that a polygon of this radius in pixels strays less than the tolerance from the circle.</summary>
    internal static int Sides(float radius, float tolerance)
    {
        if (radius <= tolerance)
        {
            return MinSides;
        }

        // A side spanning angle a strays r (1 - cos(a / 2)) from the arc.
        double angle = 2 * Math.Acos(1 - (tolerance / radius));
        return Math.Clamp((int)Math.Ceiling(2 * Math.PI / angle), MinSides, MaxSides);
    }

    private static Vector2 Point(ReadOnlySpan<float> points, int index) => new(points[index * 2], points[(index * 2) + 1]);

    private static void AddFigure(Rasterizer rasterizer, List<Vector2> figure, bool closed, float half, int sides, in Matrix3x2 transform)
    {
        // Repeated points make no line and no corner.
        for (int i = figure.Count - 1; i > 0; i--)
        {
            if (figure[i] == figure[i - 1])
            {
                figure.RemoveAt(i);
            }
        }

        if (closed && figure.Count > 1 && figure[^1] == figure[0])
        {
            figure.RemoveAt(figure.Count - 1);
        }

        if (figure.Count == 0)
        {
            return;
        }

        if (figure.Count == 1)
        {
            // A dot, as a round cap makes a zero-length line in PDF.
            Arc(rasterizer, figure[0], new Vector2(half, 0), 2 * MathF.PI, sides, transform);
            return;
        }

        int count = figure.Count;
        int segments = closed ? count : count - 1;
        for (int i = 0; i < segments; i++)
        {
            Rectangle(rasterizer, figure[i], figure[(i + 1) % count], half, transform);
        }

        // Round corners: at each, the wedge on the outer side between the two rectangles' ends. Its straight edges
        // lie on those ends and cancel them, so the outer edge of the stroke is one unbroken line.
        for (int i = closed ? 0 : 1; i < (closed ? count : count - 1); i++)
        {
            Vector2 before = figure[(i - 1 + count) % count], at = figure[i], after = figure[(i + 1) % count];
            Vector2 inward = Vector2.Normalize(at - before), outward = Vector2.Normalize(after - at);
            float turn = MathF.Atan2((inward.X * outward.Y) - (inward.Y * outward.X), Vector2.Dot(inward, outward));
            if (MathF.Abs(turn) < 1e-4f)
            {
                continue; // straight on: the rectangles meet edge to edge
            }

            // The outer side is the one the path turns away from; the wedge sweeps from the incoming end's normal there
            // to the outgoing one's.
            Vector2 start = new Vector2(-inward.Y, inward.X) * (turn > 0 ? -half : half);
            Arc(rasterizer, at, start, turn, sides, transform);
        }

        if (!closed)
        {
            // Round ends: half a disc beyond each end, swept from one side round the back to the other.
            Vector2 first = Vector2.Normalize(figure[1] - figure[0]), last = Vector2.Normalize(figure[^1] - figure[^2]);
            Arc(rasterizer, figure[0], new Vector2(first.Y, -first.X) * half, -MathF.PI, sides, transform);
            Arc(rasterizer, figure[^1], new Vector2(-last.Y, last.X) * half, -MathF.PI, sides, transform);
        }
    }

    /// <summary>The rectangle a pen of half-width <paramref name="half"/> sweeps from one point to the next.</summary>
    private static void Rectangle(Rasterizer rasterizer, Vector2 from, Vector2 to, float half, in Matrix3x2 transform)
    {
        Vector2 along = to - from;
        float length = along.Length();
        if (!(length > 0))
        {
            return;
        }

        Vector2 normal = new Vector2(-along.Y, along.X) * (half / length);
        Polygon(rasterizer, transform, [from + normal, to + normal, to - normal, from - normal]);
    }

    /// <summary>
    /// A pie slice: from the centre out to <paramref name="start"/> (relative to it), round by <paramref name="sweep"/>
    /// radians (positive turns from x towards y), and back. Its corners sit a little outside the circle so its area is
    /// the circle's share: a stroke looks as wide at a corner as along a side.
    /// </summary>
    private static void Arc(Rasterizer rasterizer, Vector2 centre, Vector2 start, float sweep, int sides, in Matrix3x2 transform)
    {
        int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(sweep) / (2 * MathF.PI) * sides));
        float step = sweep / steps;
        float grow = MathF.Sqrt(MathF.Abs(step) / MathF.Sin(MathF.Abs(step)));
        Span<Vector2> corners = steps + 2 <= 160 ? stackalloc Vector2[steps + 2] : new Vector2[steps + 2];
        int n = 0;
        if (MathF.Abs(sweep) < 2 * MathF.PI - 1e-4f)
        {
            corners[n++] = centre;
        }

        float radius = start.Length(), angle = MathF.Atan2(start.Y, start.X);
        for (int k = 0; k <= steps; k++)
        {
            // The ends lie on the rectangles' corners exactly, so the edges there cancel; the corners between are grown.
            float r = k == 0 || k == steps ? radius : radius * grow;
            float a = angle + (k * step);
            corners[n++] = centre + (r * new Vector2(MathF.Cos(a), MathF.Sin(a)));
        }

        Polygon(rasterizer, transform, corners[..n]);
    }

    /// <summary>
    /// Adds a closed polygon, turned to wind the way a rectangle from <see cref="Rectangle"/> does (negative area, y up),
    /// so every piece of a stroke adds to the winding and overlaps unite instead of cancelling.
    /// </summary>
    private static void Polygon(Rasterizer rasterizer, in Matrix3x2 transform, ReadOnlySpan<Vector2> corners)
    {
        float area = 0;
        for (int i = 0; i < corners.Length; i++)
        {
            Vector2 a = corners[i], b = corners[(i + 1) % corners.Length];
            area += (a.X * b.Y) - (b.X * a.Y);
        }

        bool reverse = area > 0;
        Vector2 first = Rasterizer.ToPixels(corners[reverse ? corners.Length - 1 : 0], transform), previous = first;
        for (int k = 1; k < corners.Length; k++)
        {
            Vector2 next = Rasterizer.ToPixels(corners[reverse ? corners.Length - 1 - k : k], transform);
            rasterizer.AddLine(previous, next);
            previous = next;
        }

        rasterizer.AddLine(previous, first);
    }

    private static void Quad(List<Vector2> figure, Vector2 p0, Vector2 p1, Vector2 p2, float flatness)
    {
        int n = Rasterizer.Steps((p0 - (2 * p1) + p2).Length() / (4 * flatness));
        for (int i = 1; i <= n; i++)
        {
            float t = (float)i / n, u = 1 - t;
            figure.Add(i == n ? p2 : (u * u * p0) + (2 * u * t * p1) + (t * t * p2));
        }
    }

    private static void Cubic(List<Vector2> figure, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float flatness)
    {
        float bend = Math.Max((p0 - (2 * p1) + p2).Length(), (p1 - (2 * p2) + p3).Length());
        int n = Rasterizer.Steps(0.75f * bend / flatness);
        for (int i = 1; i <= n; i++)
        {
            float t = (float)i / n, u = 1 - t;
            figure.Add(i == n ? p3 : (u * u * u * p0) + (3 * u * u * t * p1) + (3 * u * t * t * p2) + (t * t * t * p3));
        }
    }
}
