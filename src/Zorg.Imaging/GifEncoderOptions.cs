using System;

namespace Zorg.Imaging;

/// <summary>Options for <see cref="Image.SaveAsGif"/> and <see cref="Image.SaveAsGifAsync"/>.</summary>
/// <example>
/// <code>
/// var options = new GifEncoderOptions { MaxColors = 64, Dithering = 0 };
/// image.SaveAsGif(output, options);
/// </code>
/// </example>
public sealed class GifEncoderOptions
{
    /// <summary>The default <see cref="MaxColors"/>: 256, all a GIF can hold.</summary>
    /// <example><code>int colors = GifEncoderOptions.DefaultMaxColors;</code></example>
    public const int DefaultMaxColors = 256;

    /// <summary>The default options: up to 256 colours, full dithering, alpha cut at half.</summary>
    /// <example><code>GifEncoderOptions options = GifEncoderOptions.Default;</code></example>
    public static GifEncoderOptions Default { get; } = new();

    /// <summary>
    /// The most colours to use, 2 to 256, counting the transparent one. An image with no more colours than this is
    /// stored exactly; one with more is reduced to this many. Fewer colours make smaller files. Defaults to 256.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value isn't 2 to 256.</exception>
    /// <example><code>var small = new GifEncoderOptions { MaxColors = 32 };</code></example>
    public int MaxColors
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 2);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 256);
            field = value;
        }
    } = DefaultMaxColors;

    /// <summary>
    /// How much Floyd–Steinberg dithering to use when colours are reduced, 0 (none) to 1 (full, the default). Dithering
    /// hides banding in gradients and photos; without it, flat areas compress better. Images stored exactly aren't
    /// dithered.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value isn't 0 to 1.</exception>
    /// <example><code>var flat = new GifEncoderOptions { Dithering = 0 };</code></example>
    public double Dithering
    {
        get;
        init
        {
            if (!(value >= 0 && value <= 1))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Dithering must be 0 to 1.");
            }

            field = value;
        }
    } = 1;

    /// <summary>
    /// The alpha, 1 to 255, from which a pixel is kept: GIF has no partial transparency, so a pixel with less alpha is
    /// left fully transparent and any other is drawn fully opaque. Defaults to 128, half, as most GIF encoders do. Lower
    /// keeps more of a soft edge or shadow; higher keeps less. With <see cref="AlphaDithering"/>, the alpha level about
    /// which the dithering works.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value isn't 1 to 255.</exception>
    /// <example><code>var options = new GifEncoderOptions { AlphaThreshold = 32 };</code></example>
    public int AlphaThreshold
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 255);
            field = value;
        }
    } = DefaultAlphaThreshold;

    /// <summary>The default <see cref="AlphaThreshold"/>: 128.</summary>
    /// <example><code>int threshold = GifEncoderOptions.DefaultAlphaThreshold;</code></example>
    public const int DefaultAlphaThreshold = 128;

    /// <summary>
    /// How much to dither partly transparent pixels, 0 (none, the default) to 1 (full). Dithered, a soft edge, shadow or
    /// fade keeps its look as a fine pattern of opaque and transparent pixels, the more opaque the denser, instead of
    /// being cut at <see cref="AlphaThreshold"/>. Fully transparent and fully opaque pixels are never changed. The
    /// pattern costs some file size, and in an animation it can shimmer where a soft edge moves.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value isn't 0 to 1.</exception>
    /// <example><code>var options = new GifEncoderOptions { AlphaDithering = 1 };</code></example>
    public double AlphaDithering
    {
        get;
        init
        {
            if (!(value >= 0 && value <= 1))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "AlphaDithering must be 0 to 1.");
            }

            field = value;
        }
    }

    /// <summary>
    /// A colour to blend partly transparent pixels over, or <see langword="null"/> (the default) to keep their own
    /// colour. Set it to the colour of the page the GIF will sit on and a soft edge fades into it, without the light
    /// or dark fringe it would otherwise leave; on any other background the edge shows a rim of this colour. Its alpha
    /// is ignored.
    /// </summary>
    /// <example><code>var options = new GifEncoderOptions { Matte = new Rgba32(255, 255, 255) };</code></example>
    public Rgba32? Matte { get; init; }

    /// <summary>
    /// How many cores this encode may use, or 0 (the default) for <see cref="ImageDefaults.MaxDegreeOfParallelism"/>.
    /// The file is the same, byte for byte, whatever the value.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    /// <example><code>var options = new GifEncoderOptions { MaxDegreeOfParallelism = 1 };</code></example>
    public int MaxDegreeOfParallelism
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    }

    /// <summary>
    /// What to do when the image's <see cref="Image.ColorSpace"/> isn't sRGB. Defaults to
    /// <see cref="ColorSpaceHandling.SrgbWhenLossless"/>: few viewers read a GIF's profile.
    /// </summary>
    /// <example><code>var options = new GifEncoderOptions { ColorSpaceHandling = ColorSpaceHandling.Srgb };</code></example>
    public ColorSpaceHandling ColorSpaceHandling
    {
        get;
        init
        {
            if (!System.Enum.IsDefined(value))
            {
                throw new System.ArgumentOutOfRangeException(nameof(value), value, "Not a ColorSpaceHandling value.");
            }

            field = value;
        }
    }
}
