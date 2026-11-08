using System;

namespace Zorg.Imaging;

/// <summary>Options for <see cref="Image.Identify"/> and <see cref="Image.IdentifyAsync"/>.</summary>
/// <example>
/// <code>
/// var options = new IdentifyOptions { CountFrames = true };
/// IdentifyResult result = Image.Identify(bytes, options);
/// int frames = result.Info?.FrameCount ?? 0;
/// </code>
/// </example>
public sealed class IdentifyOptions
{
    /// <summary>The default options: default limits, frames not counted.</summary>
    /// <example><code>IdentifyOptions options = IdentifyOptions.Default;</code></example>
    public static IdentifyOptions Default { get; } = new();

    /// <summary>The limits to apply. Defaults to <see cref="ImageLimits.Default"/>.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <example><code>var options = new IdentifyOptions { Limits = new ImageLimits { MaxPixels = 25_000_000 } };</code></example>
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
    /// Whether to count every frame of a GIF or animated WebP, so <see cref="ImageInfo.FrameCount"/> is set and
    /// <see cref="ImageLimits.MaxFrames"/> is checked. This walks the whole file (skipping frame data without
    /// decompressing it), so on a stream that can't seek it reads the whole file and needs a large enough
    /// <see cref="ImageLimits.MaxHeaderBytes"/>.
    /// </summary>
    /// <example><code>var options = new IdentifyOptions { CountFrames = true };</code></example>
    public bool CountFrames { get; init; }

    /// <summary>
    /// Whether to read the colour space into <see cref="ImageInfo.ColorSpace"/>: the ICC profile and the colour chunks.
    /// A profile can be hundreds of kilobytes (and a PNG's is compressed), so this reads more than the dimensions need;
    /// it stays within the same limits. Defaults to <see langword="false"/>.
    /// </summary>
    /// <example><code>var options = new IdentifyOptions { ReadColorSpace = true };</code></example>
    public bool ReadColorSpace { get; init; }
}
