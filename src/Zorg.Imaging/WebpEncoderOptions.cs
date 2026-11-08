using System;

namespace Zorg.Imaging;

/// <summary>Options for <see cref="Image.SaveAsWebp"/> and <see cref="Image.SaveAsWebpAsync"/>.</summary>
/// <example>
/// <code>
/// var options = new WebpEncoderOptions { Quality = 75 };
/// image.SaveAsWebp(output, options);
/// </code>
/// </example>
public sealed class WebpEncoderOptions
{
    /// <summary>The default <see cref="Quality"/>: 80, as sharp and libvips use for the web.</summary>
    /// <example><code>int quality = WebpEncoderOptions.DefaultQuality;</code></example>
    public const int DefaultQuality = 80;

    /// <summary>The default <see cref="Effort"/>: 4, as cwebp's default method.</summary>
    /// <example><code>int effort = WebpEncoderOptions.DefaultEffort;</code></example>
    public const int DefaultEffort = 4;

    /// <summary>The default options: lossy, quality 80, effort 4.</summary>
    /// <example><code>WebpEncoderOptions options = WebpEncoderOptions.Default;</code></example>
    public static WebpEncoderOptions Default { get; } = new();

    /// <summary>
    /// Whether to keep every pixel exactly (VP8L), rather than the smaller lossy coding (VP8) that suits photos. Lossless
    /// suits logos, screenshots, line art and anything that will be edited again. Defaults to <see langword="false"/>.
    /// </summary>
    /// <example><code>var exact = new WebpEncoderOptions { Lossless = true };</code></example>
    public bool Lossless { get; init; }

    /// <summary>
    /// The lossy quality, 1 to 100: higher keeps more detail in larger files. 75 to 85 suits photos on the web. The scale is
    /// fitted to cwebp's, so a quality gives about the file size cwebp gives for it. Lossless files ignore it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value isn't 1 to 100.</exception>
    /// <example><code>var small = new WebpEncoderOptions { Quality = 70 };</code></example>
    public int Quality
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100);
            field = value;
        }
    } = DefaultQuality;

    /// <summary>
    /// How hard to work, 0 (fastest) to 6, as cwebp's <c>-m</c>. Lossless: higher efforts search more for a smaller file,
    /// which decodes to the same pixels. Lossy: higher efforts try more prediction modes per block; 0 uses whole-block
    /// modes only.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value isn't 0 to 6.</exception>
    /// <example><code>var fast = new WebpEncoderOptions { Lossless = true, Effort = 1 };</code></example>
    public int Effort
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 6);
            field = value;
        }
    } = DefaultEffort;

    /// <summary>
    /// How many cores this encode may use, or 0 (the default) for <see cref="ImageDefaults.MaxDegreeOfParallelism"/>.
    /// The file is the same, byte for byte, whatever the value.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    /// <example><code>var options = new WebpEncoderOptions { Lossless = true, MaxDegreeOfParallelism = 1 };</code></example>
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
