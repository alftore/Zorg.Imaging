using System;

namespace Zorg.Imaging;

/// <summary>
/// The filter a resize uses to weigh the source pixels around each new one. Sharper filters keep more detail and
/// can ring slightly at hard edges; softer ones blur a little.
/// </summary>
/// <remarks>
/// <para>
/// When shrinking, the filter widens with the scale so every source pixel counts, which avoids moiré and
/// aliasing at any shrink factor.
/// </para>
/// <para>
/// <see cref="Lanczos3"/> is the usual choice for photos made smaller, <see cref="Mitchell"/> for photos made
/// larger, and <see cref="Nearest"/> for pixel art and masks, where no new colours may appear.
/// </para>
/// </remarks>
/// <example><code>using Image thumb = image.Resize(320, 0, Resampler.Lanczos3);</code></example>
public sealed class Resampler
{
    private readonly Kind _kind;

    private Resampler(string name, Kind kind, double radius)
    {
        Name = name;
        _kind = kind;
        Radius = radius;
    }

    private enum Kind
    {
        Nearest,
        Triangle,
        CatmullRom,
        Mitchell,
        Lanczos3,
    }

    /// <summary>
    /// Takes the source pixel nearest each new pixel's centre, so the result has only the source's colours. For
    /// pixel art, masks and palette images.
    /// </summary>
    /// <example><code>using Image big = sprite.Resize(sprite.Width * 4, 0, Resampler.Nearest);</code></example>
    public static Resampler Nearest { get; } = new("Nearest", Kind.Nearest, 0.5);

    /// <summary>A straight-line blend of the nearest pixels (a triangle filter). Fast, a little soft.</summary>
    /// <example><code>using Image preview = image.Resize(640, 0, Resampler.Bilinear);</code></example>
    public static Resampler Bilinear { get; } = new("Bilinear", Kind.Triangle, 1);

    /// <summary>The Catmull-Rom cubic (B = 0, C = 1/2): sharp, with a slight halo at hard edges.</summary>
    /// <example><code>using Image larger = image.Resize(image.Width * 2, 0, Resampler.CatmullRom);</code></example>
    public static Resampler CatmullRom { get; } = new("Catmull-Rom", Kind.CatmullRom, 2);

    /// <summary>
    /// The Mitchell-Netravali cubic (B = C = 1/3): a balance of sharpness, blur and ringing. The default when
    /// making an image larger.
    /// </summary>
    /// <example><code>using Image larger = image.Resize(image.Width * 2, 0, Resampler.Mitchell);</code></example>
    public static Resampler Mitchell { get; } = new("Mitchell", Kind.Mitchell, 2);

    /// <summary>
    /// A windowed sinc over three lobes: the sharpest of these, with slight ringing at hard edges. The default when
    /// making an image smaller.
    /// </summary>
    /// <example><code>using Image thumb = image.Resize(320, 0, Resampler.Lanczos3);</code></example>
    public static Resampler Lanczos3 { get; } = new("Lanczos3", Kind.Lanczos3, 3);

    /// <summary>The filter's name, such as <c>Lanczos3</c>.</summary>
    /// <example><code>Console.WriteLine(Resampler.Mitchell.Name); // Mitchell</code></example>
    public string Name { get; }

    /// <summary>
    /// How far the filter reaches, in source pixels, when the scale is 1. Shrinking widens it by the shrink factor.
    /// </summary>
    /// <example><code>double radius = Resampler.Lanczos3.Radius; // 3</code></example>
    public double Radius { get; }

    internal bool IsNearest => _kind == Kind.Nearest;

    /// <summary>Returns the name.</summary>
    /// <returns><see cref="Name"/>.</returns>
    /// <example><code>Console.WriteLine(Resampler.Lanczos3); // Lanczos3</code></example>
    public override string ToString() => Name;

    /// <summary>The filter's weight at a distance, in source pixels at scale 1.</summary>
    internal double Weight(double x)
    {
        x = Math.Abs(x);
        return _kind switch
        {
            Kind.Triangle => x < 1 ? 1 - x : 0,
            Kind.CatmullRom => Cubic(x, 0, 0.5),
            Kind.Mitchell => Cubic(x, 1.0 / 3, 1.0 / 3),
            Kind.Lanczos3 => x < 3 ? Sinc(x) * Sinc(x / 3) : 0,
            _ => x < 0.5 ? 1 : 0,
        };
    }

    // Mitchell and Netravali, "Reconstruction Filters in Computer Graphics" (1988), for x >= 0.
    private static double Cubic(double x, double b, double c)
    {
        if (x < 1)
        {
            return (((12 - (9 * b) - (6 * c)) * x * x * x) + ((-18 + (12 * b) + (6 * c)) * x * x) + (6 - (2 * b))) / 6;
        }

        if (x < 2)
        {
            return (((-b - (6 * c)) * x * x * x) + (((6 * b) + (30 * c)) * x * x) + (((-12 * b) - (48 * c)) * x) + ((8 * b) + (24 * c))) / 6;
        }

        return 0;
    }

    private static double Sinc(double x)
    {
        if (x < 1e-9)
        {
            return 1;
        }

        double px = Math.PI * x;
        return Math.Sin(px) / px;
    }
}
