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

    /// <summary>The default options: up to 256 colours, full dithering.</summary>
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
}
