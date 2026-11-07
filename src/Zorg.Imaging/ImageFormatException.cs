using System;

namespace Zorg.Imaging;

/// <summary>The data breaks its format's rules, or ends before the image does.</summary>
/// <example>
/// <code>
/// catch (ImageFormatException e)
/// {
///     logger.LogInformation("Rejected a damaged upload: {Reason}", e.Message);
/// }
/// </code>
/// </example>
public class ImageFormatException : ImageException
{
    /// <summary>Creates the exception with a default message.</summary>
    /// <example><code>throw new ImageFormatException();</code></example>
    public ImageFormatException()
        : base("The image data is corrupt.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What is wrong with the data. Name the format: "The PNG file ends inside its IHDR chunk."</param>
    /// <example><code>throw new ImageFormatException("The PNG file has a bad IHDR checksum.");</code></example>
    public ImageFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    /// <param name="message">What is wrong with the data.</param>
    /// <param name="innerException">The cause, such as an <see cref="System.IO.InvalidDataException"/> from the deflate stream.</param>
    /// <example><code>throw new ImageFormatException("The PNG image data doesn't decompress.", e);</code></example>
    public ImageFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal ImageFormatException(string message, bool truncated)
        : base(message)
    {
        IsTruncated = truncated;
    }

    /// <summary>Whether the data ended early, rather than breaking a rule: often an upload cut off part-way.</summary>
    internal bool IsTruncated { get; }
}
