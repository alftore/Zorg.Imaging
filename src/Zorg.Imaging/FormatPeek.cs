using System.IO;

namespace Zorg.Imaging;

/// <summary>The result of <see cref="ImageFormats.PeekAsync"/>: the format and a stream that still holds every byte.</summary>
/// <example>
/// <code>
/// FormatPeek peek = await ImageFormats.PeekAsync(request.Body, cancellationToken);
/// if (peek.Format is { IsSupported: true })
/// {
///     await peek.Stream.CopyToAsync(destination, cancellationToken);
/// }
/// </code>
/// </example>
public sealed class FormatPeek
{
    internal FormatPeek(ImageFormat? format, Stream stream)
    {
        Format = format;
        Stream = stream;
    }

    /// <summary>The format, or <see langword="null"/> when no known signature matches.</summary>
    /// <example><code>string name = peek.Format?.Name ?? "not an image";</code></example>
    public ImageFormat? Format { get; }

    /// <summary>
    /// The stream to read the whole file from, starting with the bytes the peek read. It doesn't own the stream that
    /// was peeked, so disposing it leaves that stream open.
    /// </summary>
    /// <example><code>IdentifyResult result = await Image.IdentifyAsync(peek.Stream, cancellationToken: cancellationToken);</code></example>
    public Stream Stream { get; }
}
