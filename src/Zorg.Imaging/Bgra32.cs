using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Zorg.Imaging;

/// <summary>
/// One pixel of a <see cref="PixelFormat.Bgra32"/> image: blue, green, red and alpha, one byte each. Use it with
/// <see cref="Image.GetRow{T}(int)"/>.
/// </summary>
/// <example>
/// <code>
/// Span&lt;Bgra32&gt; row = image.GetRow&lt;Bgra32&gt;(0);
/// row[0] = new Bgra32(0, 0, 255, 128); // a half-transparent red
/// </code>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct Bgra32 : IEquatable<Bgra32>
{
    /// <summary>Creates a pixel.</summary>
    /// <param name="b">The blue.</param>
    /// <param name="g">The green.</param>
    /// <param name="r">The red.</param>
    /// <param name="a">The alpha.</param>
    /// <example><code>var pixel = new Bgra32(0, 0, 255, 128);</code></example>
    public Bgra32(byte b, byte g, byte r, byte a = 255)
    {
        B = b;
        G = g;
        R = r;
        A = a;
    }

    /// <summary>The blue.</summary>
    /// <example><code>byte b = pixel.B;</code></example>
    public byte B { get; set; }

    /// <summary>The green.</summary>
    /// <example><code>byte g = pixel.G;</code></example>
    public byte G { get; set; }

    /// <summary>The red.</summary>
    /// <example><code>byte r = pixel.R;</code></example>
    public byte R { get; set; }

    /// <summary>The alpha: 0 is transparent, 255 opaque.</summary>
    /// <example><code>byte a = pixel.A;</code></example>
    public byte A { get; set; }

    /// <summary>Whether two pixels have the same channel values.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    /// <example><code>bool same = row[0] == row[1];</code></example>
    public static bool operator ==(Bgra32 left, Bgra32 right) => left.Equals(right);

    /// <summary>Whether two pixels differ in any channel.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    /// <example><code>bool changed = row[0] != before;</code></example>
    public static bool operator !=(Bgra32 left, Bgra32 right) => !left.Equals(right);

    /// <summary>Whether this pixel has the same channel values as <paramref name="other"/>.</summary>
    /// <param name="other">The pixel to compare with.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    /// <example><code>bool same = row[0].Equals(row[1]);</code></example>
    public readonly bool Equals(Bgra32 other) => B == other.B && G == other.G && R == other.R && A == other.A;

    /// <summary>Whether <paramref name="obj"/> is a <see cref="Bgra32"/> with the same channel values.</summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns><see langword="true"/> when they are equal.</returns>
    /// <example><code>bool same = row[0].Equals((object)row[1]);</code></example>
    public override readonly bool Equals(object? obj) => obj is Bgra32 other && Equals(other);

    /// <summary>A hash of the channel values.</summary>
    /// <returns>The hash code.</returns>
    /// <example><code>var seen = new HashSet&lt;Bgra32&gt;(); seen.Add(row[0]);</code></example>
    public override readonly int GetHashCode() => HashCode.Combine(B, G, R, A);

    /// <summary>The channel values, such as <c>Bgra32(255, 255, 255, 255)</c>.</summary>
    /// <returns>A short description.</returns>
    /// <example><code>Console.WriteLine(row[0]);</code></example>
    public override readonly string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "Bgra32({0}, {1}, {2}, {3})", B, G, R, A);
}
