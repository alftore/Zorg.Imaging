using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Zorg.Imaging;

/// <summary>
/// One pixel of a <see cref="PixelFormat.Rgba64"/> image: red, green, blue and alpha, 16 bits each. Use it with
/// <see cref="Image.GetRow{T}(int)"/>.
/// </summary>
/// <example>
/// <code>
/// Span&lt;Rgba64&gt; row = image.GetRow&lt;Rgba64&gt;(0);
/// row[0] = new Rgba64(65535, 0, 0, 32768); // a half-transparent red
/// </code>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct Rgba64 : IEquatable<Rgba64>
{
    /// <summary>Creates a pixel.</summary>
    /// <param name="r">The red.</param>
    /// <param name="g">The green.</param>
    /// <param name="b">The blue.</param>
    /// <param name="a">The alpha.</param>
    /// <example><code>var pixel = new Rgba64(65535, 0, 0, 32768);</code></example>
    public Rgba64(ushort r, ushort g, ushort b, ushort a = 65535)
    {
        R = r;
        G = g;
        B = b;
        A = a;
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

    /// <summary>The alpha: 0 is transparent, 65535 opaque.</summary>
    /// <example><code>ushort a = pixel.A;</code></example>
    public ushort A { get; set; }

    /// <summary>Whether two pixels have the same channel values.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    /// <example><code>bool same = row[0] == row[1];</code></example>
    public static bool operator ==(Rgba64 left, Rgba64 right) => left.Equals(right);

    /// <summary>Whether two pixels differ in any channel.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    /// <example><code>bool changed = row[0] != before;</code></example>
    public static bool operator !=(Rgba64 left, Rgba64 right) => !left.Equals(right);

    /// <summary>Whether this pixel has the same channel values as <paramref name="other"/>.</summary>
    /// <param name="other">The pixel to compare with.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    /// <example><code>bool same = row[0].Equals(row[1]);</code></example>
    public readonly bool Equals(Rgba64 other) => R == other.R && G == other.G && B == other.B && A == other.A;

    /// <summary>Whether <paramref name="obj"/> is a <see cref="Rgba64"/> with the same channel values.</summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    /// <example><code>bool same = row[0].Equals((object)row[1]);</code></example>
    public override readonly bool Equals(object? obj) => obj is Rgba64 other && Equals(other);

    /// <summary>A hash of the channel values.</summary>
    /// <returns>The hash code.</returns>
    /// <example><code>var seen = new HashSet&lt;Rgba64&gt;(); seen.Add(row[0]);</code></example>
    public override readonly int GetHashCode() => HashCode.Combine(R, G, B, A);

    /// <summary>The channel values, such as <c>Rgba64(65535, 65535, 65535, 65535)</c>.</summary>
    /// <returns>A short description.</returns>
    /// <example><code>Console.WriteLine(row[0]);</code></example>
    public override readonly string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "Rgba64({0}, {1}, {2}, {3})", R, G, B, A);
}
