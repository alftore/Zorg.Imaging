using System;

namespace Zorg.Imaging;

/// <summary>
/// The base of every exception the library throws for an image it can't use: corrupt or truncated data, a format it
/// doesn't support, or a size over the limits. Catch this to turn any of them into a 400 response.
/// </summary>
/// <remarks>
/// Messages are in English, name the format, and are safe to show to whoever sent the file: they never contain paths,
/// memory addresses or the file's bytes.
/// </remarks>
/// <example>
/// <code>
/// try
/// {
///     using Image image = Image.Decode(bytes);
/// }
/// catch (ImageException e)
/// {
///     return Results.BadRequest(e.Message);
/// }
/// </code>
/// </example>
public class ImageException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    /// <example><code>throw new ImageException();</code></example>
    public ImageException()
        : base("The image can't be used.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">Why the image can't be used.</param>
    /// <example><code>throw new ImageException("The image can't be used.");</code></example>
    public ImageException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    /// <param name="message">Why the image can't be used.</param>
    /// <param name="innerException">The cause.</param>
    /// <example><code>throw new ImageException("The image can't be used.", e);</code></example>
    public ImageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
