using System;

namespace Zorg.Imaging;

/// <summary>
/// The data is an image, but in a format or a variant of one that the library can't decode, such as TIFF, or a PNG
/// from Apple's iOS build tools.
/// </summary>
/// <example>
/// <code>
/// catch (UnsupportedImageFormatException)
/// {
///     return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
/// }
/// </code>
/// </example>
public class UnsupportedImageFormatException : ImageException
{
    /// <summary>Creates the exception with a default message.</summary>
    /// <example><code>throw new UnsupportedImageFormatException();</code></example>
    public UnsupportedImageFormatException()
        : base("The image format isn't supported.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">Which format or variant, and why.</param>
    /// <example><code>throw new UnsupportedImageFormatException("TIFF images aren't supported.");</code></example>
    public UnsupportedImageFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    /// <param name="message">Which format or variant, and why.</param>
    /// <param name="innerException">The cause.</param>
    /// <example><code>throw new UnsupportedImageFormatException("TIFF images aren't supported.", e);</code></example>
    public UnsupportedImageFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a recognised format.</summary>
    /// <param name="message">Which format or variant, and why.</param>
    /// <param name="format">The format, when it was recognised.</param>
    /// <example><code>throw new UnsupportedImageFormatException("TIFF images aren't supported.", ImageFormat.Tiff);</code></example>
    public UnsupportedImageFormatException(string message, ImageFormat? format)
        : base(message)
    {
        Format = format;
    }

    /// <summary>The format, or <see langword="null"/> when it wasn't recognised.</summary>
    /// <example><code>string name = e.Format?.Name ?? "unknown";</code></example>
    public ImageFormat? Format { get; }
}
