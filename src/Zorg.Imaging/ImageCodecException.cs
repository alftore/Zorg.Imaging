using System;

namespace Zorg.Imaging;

/// <summary>
/// An <see cref="ImageCodec"/> added by another package failed in a way that isn't the file's fault: a bug, a missing
/// library, or an operating system decoder's error. The inner exception has the details.
/// <see cref="Image.Identify(byte[], IdentifyOptions?)"/> reports it as <see cref="IdentifyStatus.CodecFailed"/>.
/// </summary>
/// <example>
/// <code>
/// catch (ImageCodecException e)
/// {
///     logger.LogError(e, "The {Codec} codec failed.", e.CodecName);
/// }
/// </code>
/// </example>
public class ImageCodecException : ImageException
{
    /// <summary>Creates the exception with a default message.</summary>
    /// <example><code>throw new ImageCodecException();</code></example>
    public ImageCodecException()
        : base("An image codec failed.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What failed.</param>
    /// <example><code>throw new ImageCodecException("The HEIC decoder failed.");</code></example>
    public ImageCodecException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="innerException">The cause.</param>
    /// <example><code>throw new ImageCodecException("The HEIC decoder failed.", e);</code></example>
    public ImageCodecException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a codec's failure.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="codecName">The codec's <see cref="ImageCodec.Name"/>.</param>
    /// <param name="innerException">The cause.</param>
    /// <example><code>throw new ImageCodecException("The decoder failed.", codec.Name, e);</code></example>
    public ImageCodecException(string message, string? codecName, Exception? innerException)
        : base(message, innerException!)
    {
        CodecName = codecName;
    }

    /// <summary>The codec's <see cref="ImageCodec.Name"/>, when known.</summary>
    /// <example><code>string? name = e.CodecName;</code></example>
    public string? CodecName { get; }
}
