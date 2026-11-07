using System;

namespace Zorg.Imaging;

/// <summary>Options for <see cref="Image.SaveAsPng"/> and <see cref="Image.SaveAsPngAsync"/>.</summary>
/// <example>
/// <code>
/// var options = new PngEncoderOptions { CompressionLevel = 9 };
/// image.SaveAsPng(output, options);
/// </code>
/// </example>
public sealed class PngEncoderOptions
{
    /// <summary>The default for <see cref="CompressionLevel"/>: 6, zlib's own default.</summary>
    /// <example><code>int level = PngEncoderOptions.DefaultCompressionLevel;</code></example>
    public const int DefaultCompressionLevel = 6;

    /// <summary>The default options.</summary>
    /// <example><code>PngEncoderOptions options = PngEncoderOptions.Default;</code></example>
    public static PngEncoderOptions Default { get; } = new();

    /// <summary>
    /// The zlib compression level, 0 to 9. 0 stores the data uncompressed and unfiltered, the fastest and largest; 9 is
    /// the smallest and slowest. The output is lossless at every level.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value isn't 0 to 9.</exception>
    /// <example><code>var fast = new PngEncoderOptions { CompressionLevel = 1 };</code></example>
    public int CompressionLevel
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 9);
            field = value;
        }
    } = DefaultCompressionLevel;
}
