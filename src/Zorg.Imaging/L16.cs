using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Zorg.Imaging;

/// <summary>
/// One pixel of a <see cref="PixelFormat.L16"/> image: luminance (grey level), 16 bits. Use it with
/// <see cref="Image.GetRow{T}(int)"/>.
/// </summary>
/// <example>
/// <code>
/// Span&lt;L16&gt; row = image.GetRow&lt;L16&gt;(0);
/// row[0] = new L16(32768); // mid grey
/// </code>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct L16 : IEquatable<L16>
{
    /// <summary>Creates a pixel.</summary>
    /// <param name="l">The luminance.</param>
    /// <example><code>var pixel = new L16(32768);</code></example>
    public L16(ushort l)
    {
        L = l;
    }

    /// <summary>The luminance: 0 is black, 65535 white.</summary>
    /// <example><code>ushort l = pixel.L;</code></example>
    public ushort L { get; set; }

    /// <summary>Whether two pixels have the same channel values.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    /// <example><code>bool same = row[0] == row[1];</code></example>
    public static bool operator ==(L16 left, L16 right) => left.Equals(right);

    /// <summary>Whether two pixels differ in any channel.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    /// <example><code>bool changed = row[0] != before;</code></example>
    public static bool operator !=(L16 left, L16 right) => !left.Equals(right);

    /// <summary>Whether this pixel has the same channel values as <paramref name="other"/>.</summary>
    /// <param name="other">The pixel to compare with.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    /// <example><code>bool same = row[0].Equals(row[1]);</code></example>
    public readonly bool Equals(L16 other) => L == other.L;

    /// <summary>Whether <paramref name="obj"/> is a <see cref="L16"/> with the same channel values.</summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    /// <example><code>bool same = row[0].Equals((object)row[1]);</code></example>
    public override readonly bool Equals(object? obj) => obj is L16 other && Equals(other);

    /// <summary>A hash of the channel values.</summary>
    /// <returns>The hash code.</returns>
    /// <example><code>var seen = new HashSet&lt;L16&gt;(); seen.Add(row[0]);</code></example>
    public override readonly int GetHashCode() => L;

    /// <summary>The channel values, such as <c>L16(65535)</c>.</summary>
    /// <returns>A short description.</returns>
    /// <example><code>Console.WriteLine(row[0]);</code></example>
    public override readonly string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "L16({0})", L);
}
