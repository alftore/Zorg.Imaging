using System;

namespace Zorg.Imaging;

/// <summary>How much colour detail a JPEG keeps, relative to brightness.</summary>
/// <example><code>var options = new JpegEncoderOptions { ChromaSubsampling = JpegChromaSubsampling.Yuv444 };</code></example>
public enum JpegChromaSubsampling
{
    /// <summary>
    /// Colour at half the width and half the height (4:2:0): the usual choice for photos, and the smallest files. Eyes
    /// notice brightness detail far more than colour detail.
    /// </summary>
    Yuv420 = 0,

    /// <summary>
    /// Colour at full size (4:4:4): larger files, but sharp coloured text, thin coloured lines and screenshots keep their
    /// edges.
    /// </summary>
    Yuv444 = 1,
}

/// <summary>Options for <see cref="Image.SaveAsJpeg"/> and <see cref="Image.SaveAsJpegAsync"/>.</summary>
/// <example>
/// <code>
/// var options = new JpegEncoderOptions { Quality = 85 };
/// image.SaveAsJpeg(output, options);
/// </code>
/// </example>
public sealed class JpegEncoderOptions
{
    /// <summary>The default <see cref="Quality"/>: 80, as sharp and libvips use for the web.</summary>
    /// <example><code>int quality = JpegEncoderOptions.DefaultQuality;</code></example>
    public const int DefaultQuality = 80;

    /// <summary>The default options: quality 80, 4:2:0, transparency drawn over white.</summary>
    /// <example><code>JpegEncoderOptions options = JpegEncoderOptions.Default;</code></example>
    public static JpegEncoderOptions Default { get; } = new();

    /// <summary>
    /// The quality, 1 to 100, on the scale libjpeg and most tools use: the standard's example tables scaled, 50 as they
    /// are. 75 to 85 suits photos on the web; above 90 files grow quickly for little visible gain.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value isn't 1 to 100.</exception>
    /// <example><code>var small = new JpegEncoderOptions { Quality = 70 };</code></example>
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

    /// <summary>How much colour detail to keep. Defaults to <see cref="JpegChromaSubsampling.Yuv420"/>. Grey images ignore it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value isn't one of the enumeration's.</exception>
    /// <example><code>var crisp = new JpegEncoderOptions { ChromaSubsampling = JpegChromaSubsampling.Yuv444 };</code></example>
    public JpegChromaSubsampling ChromaSubsampling
    {
        get;
        init
        {
            if (value is not (JpegChromaSubsampling.Yuv420 or JpegChromaSubsampling.Yuv444))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Use Yuv420 or Yuv444.");
            }

            field = value;
        }
    }

    /// <summary>
    /// The colour transparent pixels are drawn over, since JPEG has no transparency. Defaults to white, which suits a
    /// logo or cut-out on a web page; its alpha is ignored.
    /// </summary>
    /// <example><code>var onBlack = new JpegEncoderOptions { Background = new Rgba32(0, 0, 0) };</code></example>
    public Rgba32 Background { get; init; } = new(255, 255, 255);

    /// <summary>
    /// How many cores this encode may use, or 0 (the default) for <see cref="ImageDefaults.MaxDegreeOfParallelism"/>.
    /// The file is the same, byte for byte, whatever the value.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    /// <example><code>var options = new JpegEncoderOptions { MaxDegreeOfParallelism = 1 };</code></example>
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
