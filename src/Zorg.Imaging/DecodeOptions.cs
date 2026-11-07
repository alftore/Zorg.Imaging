using System;

namespace Zorg.Imaging;

/// <summary>Options for <see cref="Image.Decode"/> and <see cref="Image.DecodeAsync"/>.</summary>
/// <example>
/// <code>
/// var options = new DecodeOptions { PixelFormat = PixelFormat.Rgba32 };
/// using Image image = Image.Decode(bytes, options);
/// </code>
/// </example>
public sealed class DecodeOptions
{
    /// <summary>The default options: default limits, the file's own layout, orientation applied.</summary>
    /// <example><code>DecodeOptions options = DecodeOptions.Default;</code></example>
    public static DecodeOptions Default { get; } = new();

    /// <summary>The limits to apply. Defaults to <see cref="ImageLimits.Default"/>.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <example><code>var options = new DecodeOptions { Limits = new ImageLimits { MaxPixels = 25_000_000 } };</code></example>
    public ImageLimits Limits
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    } = ImageLimits.Default;

    /// <summary>
    /// The layout of the decoded image. <see cref="PixelFormat.None"/>, the default, picks the layout that holds the
    /// file without loss: <see cref="PixelFormat.L8"/> for grey, <see cref="PixelFormat.Rgb24"/> for colour, and
    /// <see cref="PixelFormat.Rgba32"/> when the file has any transparency, or <see cref="PixelFormat.L16"/>,
    /// <see cref="PixelFormat.Rgb48"/> and <see cref="PixelFormat.Rgba64"/> for 16-bit files.
    /// <see cref="PixelFormat.Rgba64"/> holds any file exactly. <see cref="PixelFormat.Rgba32"/> and
    /// <see cref="PixelFormat.Bgra32"/> hold any 8-bit file exactly, and round 16-bit samples to the nearest 8-bit value.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is <see cref="PixelFormat.Rgb24"/>, <see cref="PixelFormat.L8"/>, <see cref="PixelFormat.Rgb48"/> or
    /// <see cref="PixelFormat.L16"/>, which would drop alpha or colour, or isn't a format.
    /// </exception>
    /// <example><code>var options = new DecodeOptions { PixelFormat = PixelFormat.Bgra32 };</code></example>
    public PixelFormat PixelFormat
    {
        get;
        init
        {
            if (value is not (PixelFormat.None or PixelFormat.Rgba32 or PixelFormat.Bgra32 or PixelFormat.Rgba64))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "Decoding can ask for None, Rgba32, Bgra32 or Rgba64. The formats without alpha, or without colour, would drop it.");
            }

            field = value;
        }
    }

    /// <summary>
    /// Whether to turn the pixels upright using the file's EXIF orientation, so the image looks the way a browser shows
    /// it. Defaults to <see langword="true"/>. The decoded size is then <see cref="ImageInfo.DisplayWidth"/> by
    /// <see cref="ImageInfo.DisplayHeight"/>.
    /// </summary>
    /// <example><code>var options = new DecodeOptions { ApplyOrientation = false }; // pixels as stored</code></example>
    public bool ApplyOrientation { get; init; } = true;
}
