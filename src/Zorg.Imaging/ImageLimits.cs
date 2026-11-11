using System;

namespace Zorg.Imaging;

/// <summary>
/// Limits that protect a server from hostile or oversized files. They are checked against the headers before anything
/// is allocated.
/// </summary>
/// <example>
/// <code>
/// var limits = new ImageLimits { MaxPixels = 50_000_000, MaxFrames = 500 };
/// IdentifyResult result = Image.Identify(bytes, new IdentifyOptions { Limits = limits });
/// </code>
/// </example>
public sealed class ImageLimits
{
    /// <summary>The default for <see cref="MaxPixels"/>: 16383 × 16383, the same as sharp's <c>limitInputPixels</c>.</summary>
    /// <example><code>long max = ImageLimits.DefaultMaxPixels;</code></example>
    public const long DefaultMaxPixels = 268_402_689;

    /// <summary>The default for <see cref="MaxFrames"/>.</summary>
    /// <example><code>int max = ImageLimits.DefaultMaxFrames;</code></example>
    public const int DefaultMaxFrames = 10_000;

    /// <summary>The default for <see cref="MaxHeaderBytes"/>: 1 MiB.</summary>
    /// <example><code>int max = ImageLimits.DefaultMaxHeaderBytes;</code></example>
    public const int DefaultMaxHeaderBytes = 1024 * 1024;

    /// <summary>The default for <see cref="MaxFileBytes"/>: 256 MiB.</summary>
    /// <example><code>long max = ImageLimits.DefaultMaxFileBytes;</code></example>
    public const long DefaultMaxFileBytes = 256L * 1024 * 1024;

    /// <summary>The default limits.</summary>
    /// <example><code>ImageLimits limits = ImageLimits.Default;</code></example>
    public static ImageLimits Default { get; } = new();

    /// <summary>The most pixels (width × height) an image or animation canvas may have.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    /// <example><code>var limits = new ImageLimits { MaxPixels = 4096L * 4096 };</code></example>
    public long MaxPixels
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = DefaultMaxPixels;

    /// <summary>The most frames an animation may have.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero or negative.</exception>
    /// <example><code>var limits = new ImageLimits { MaxFrames = 1 };</code></example>
    public int MaxFrames
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = DefaultMaxFrames;

    /// <summary>
    /// The most bytes reading the headers may consume. Bytes skipped by seeking don't count, so this matters most for
    /// streams that can't seek, and for counting GIF frames.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than <see cref="ImageFormats.MaxSignatureLength"/>.</exception>
    /// <example><code>var limits = new ImageLimits { MaxHeaderBytes = 8 * 1024 * 1024 };</code></example>
    public int MaxHeaderBytes
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, ImageFormats.MaxSignatureLength);
            field = value;
        }
    } = DefaultMaxHeaderBytes;

    /// <summary>
    /// The most bytes a file may have when it must be read whole, as for an <see cref="ImageCodec"/> added by another
    /// package. The built-in decoders read as they go and don't use it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than <see cref="ImageFormats.MaxSignatureLength"/>.</exception>
    /// <example><code>var limits = new ImageLimits { MaxFileBytes = 50L * 1024 * 1024 };</code></example>
    public long MaxFileBytes
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, ImageFormats.MaxSignatureLength);
            field = value;
        }
    } = DefaultMaxFileBytes;
}
