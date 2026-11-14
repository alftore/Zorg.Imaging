namespace Zorg.Imaging.Drawing;

/// <summary>Which points a shape whose outline crosses itself, or has holes, covers.</summary>
/// <example><code>canvas.Fill(ring, black, FillRule.EvenOdd);</code></example>
public enum FillRule
{
    /// <summary>
    /// Inside where the outline winds around the point at least once in total, counting each turn by its direction.
    /// A hole needs its outline drawn the opposite way round. TrueType glyphs, PDF's <c>f</c> and SVG's default use it.
    /// </summary>
    NonZero = 0,

    /// <summary>Inside where a ray from the point crosses the outline an odd number of times, whichever way round it is drawn.</summary>
    EvenOdd = 1,
}
