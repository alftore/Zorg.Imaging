using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Zorg.Imaging;

/// <summary>
/// One pixel of a <see cref="PixelFormat.Rgb48"/> image: red, green and blue, 16 bits each. Use it with
/// <see cref="Image.GetRow{T}(int)"/>.
/// </summary>
/// <example>
/// <code>
/// Span&lt;Rgb48&gt; row = image.GetRow&lt;Rgb48&gt;(0);
/// row[0] = new Rgb48(65535, 32768, 0); // orange
/// </code>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct Rgb48 : IEquatable<Rgb48>
{
    /// <summary>Creates a pixel.</summary>
    /// <param name="r">The red.</param>
    /// <param name="g">The green.</param>
    /// <param name="b">The blue.</param>
    /// <example><code>var pixel = new Rgb48(65535, 32768, 0);</code></example>
    public Rgb48(ushort r, ushort g, ushort b)
    {
        R = r;
        G = g;
        B = b;
    }

    /// <summary>The red.</summary>
    /// <example><code>ushort r = pixel.R;</code></example>
    public ushort R { get; set; }

    /// <summary>The green.</summary>
    /// <example><code>ushort g = pixel.G;</code></example>
    public ushort G { get; set; }

    /// <summary>The blue.</summary>
    /// <example><code>ushort b = pixel.B;</code></example>
    public ushort B { get; set; }

    /// <summary>Whether two pixels have the same channel values.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    /// <example><code>bool same = row[0] == row[1];</code></example>
    public static bool operator ==(Rgb48 left, Rgb48 right) => left.Equals(right);

    /// <summary>Whether two pixels differ in any channel.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    /// <example><code>bool changed = row[0] != before;</code></example>
    public static bool operator !=(Rgb48 left, Rgb48 right) => !left.Equals(right);

    /// <summary>Whether this pixel has the same channel values as <paramref name="other"/>.</summary>
    /// <param name="other">The pixel to compare with.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    /// <example><code>bool same = row[0].Equals(row[1]);</code></example>
    public readonly bool Equals(Rgb48 other) => R == other.R && G == other.G && B == other.B;

    /// <summary>Whether <paramref name="obj"/> is a <see cref="Rgb48"/> with the same channel values.</summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    /// <example><code>bool same = row[0].Equals((object)row[1]);</code></example>
    public override readonly bool Equals(object? obj) => obj is Rgb48 other && Equals(other);

    /// <summary>A hash of the channel values.</summary>
    /// <returns>The hash code.</returns>
    /// <example><code>var seen = new HashSet&lt;Rgb48&gt;(); seen.Add(row[0]);</code></example>
    public override readonly int GetHashCode() => HashCode.Combine(R, G, B);

    /// <summary>The channel values, such as <c>Rgb48(65535, 65535, 65535)</c>.</summary>
    /// <returns>A short description.</returns>
    /// <example><code>Console.WriteLine(row[0]);</code></example>
    public override readonly string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "Rgb48({0}, {1}, {2})", R, G, B);
}
