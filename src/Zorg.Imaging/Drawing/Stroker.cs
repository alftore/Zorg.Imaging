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
        List<Vector2> figure = rasterizer.Figure;
        figure.Clear();
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
        int kept = Math.Min(1, figure.Count);
        for (int i = 1; i < figure.Count; i++)
        {
            if (figure[i] != figure[kept - 1])
            {
                figure[kept++] = figure[i];
            }
        }

        figure.RemoveRange(kept, figure.Count - kept);

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

        // Inner corners: where the two sides' inner edges meet. A segment ending there instead of in a square end
        // doesn't overlap its neighbour, and an overlap would be counted twice in the pixels it crosses.
        List<Vector2> inner = rasterizer.Inner;
        List<sbyte> side = rasterizer.InnerSide;
        inner.Clear();
        side.Clear();
        for (int i = 0; i < count; i++)
        {
            (Vector2 point, sbyte which) = closed || (i > 0 && i < count - 1)
                ? Miter(figure[(i - 1 + count) % count], figure[i], figure[(i + 1) % count], half)
                : (default, (sbyte)0);
            inner.Add(point);
            side.Add(which);
        }

        Span<Vector2> corners = stackalloc Vector2[6];
        for (int i = 0; i < segments; i++)
        {
            int end = (i + 1) % count;
            Vector2 from = figure[i], to = figure[end];
            Vector2 left = Vector2.Normalize(to - from);
            left = new Vector2(-left.Y, left.X) * half;
            int n = 0;
            corners[n++] = side[i] > 0 ? inner[i] : from + left;
            corners[n++] = side[end] > 0 ? inner[end] : to + left;
            if (side[end] != 0)
            {
                corners[n++] = to;
            }

            corners[n++] = side[end] < 0 ? inner[end] : to - left;
            corners[n++] = side[i] < 0 ? inner[i] : from - left;
            if (side[i] != 0)
            {
                corners[n++] = from;
            }

            Polygon(rasterizer, transform, corners[..n]);
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

    /// <summary>
    /// Where the inner edges of the segments into and out of <paramref name="at"/> meet, and on which side of them (1 for
    /// the left, -1 the right); side 0 where they don't meet within half of either segment, which then end square and
    /// overlap.
    /// </summary>
    private static (Vector2 Point, sbyte Side) Miter(Vector2 before, Vector2 at, Vector2 after, float half)
    {
        Vector2 inward = at - before, outward = after - at;
        float lengthIn = inward.Length(), lengthOut = outward.Length();
        inward /= lengthIn;
        outward /= lengthOut;
        float cross = (inward.X * outward.Y) - (inward.Y * outward.X), dot = Vector2.Dot(inward, outward);
        if (MathF.Abs(cross) < 1e-4f || dot < -0.999f)
        {
            return (default, 0); // straight on, or turning back on itself
        }

        // Turning left (as the wedges measure it), the inner side is the left.
        sbyte which = cross > 0 ? (sbyte)1 : (sbyte)-1;
        Vector2 normalIn = new Vector2(-inward.Y, inward.X) * which, normalOut = new Vector2(-outward.Y, outward.X) * which;
        Vector2 point = at + ((normalIn + normalOut) * (half / (1 + Vector2.Dot(normalIn, normalOut))));
        float back = Vector2.Dot(at - point, inward), on = Vector2.Dot(point - at, outward);
        return back <= 0.5f * lengthIn && on <= 0.5f * lengthOut ? (point, which) : (default, 0);
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
