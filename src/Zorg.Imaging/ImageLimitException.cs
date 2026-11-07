using System;

namespace Zorg.Imaging;

/// <summary>
/// The image is bigger than <see cref="ImageLimits"/> allows: too many pixels, frames or header bytes. Thrown before
/// the memory is allocated, so a small hostile file can't exhaust the server.
/// </summary>
/// <example>
/// <code>
/// catch (ImageLimitException e)
/// {
///     return Results.Problem(e.Message, statusCode: StatusCodes.Status413PayloadTooLarge);
/// }
/// </code>
/// </example>
public class ImageLimitException : ImageException
{
    /// <summary>Creates the exception with a default message.</summary>
    /// <example><code>throw new ImageLimitException();</code></example>
    public ImageLimitException()
        : base("The image is larger than the limits allow.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">Which limit, the value and the limit, naming the <see cref="ImageLimits"/> property.</param>
    /// <example><code>throw new ImageLimitException("The image has 400000000 pixels, more than the limit (ImageLimits.MaxPixels).");</code></example>
    public ImageLimitException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    /// <param name="message">Which limit, the value and the limit.</param>
    /// <param name="innerException">The cause.</param>
    /// <example><code>throw new ImageLimitException("The image is too large.", e);</code></example>
    public ImageLimitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
