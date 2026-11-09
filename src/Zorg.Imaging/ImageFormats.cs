using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging;

/// <summary>
/// The signature check: recognises an image format from the first bytes of a file, so an upload that can't be
/// processed can be refused before any real work.
/// </summary>
/// <remarks>
/// <para>
/// Detection uses the content, never a file name or extension. It answers "is this worth trying?", not "is this
/// safe?": a file with a valid signature can still be truncated, corrupt or too large. Use
/// <see cref="Image.IdentifyAsync"/> to read the headers with limits applied.
/// </para>
/// <para>SVG is text, not a raster format, and isn't detected.</para>
/// </remarks>
/// <example>
/// <code>
/// FormatPeek peek = await ImageFormats.PeekAsync(request.Body, cancellationToken);
/// if (peek.Format is not { IsSupported: true })
/// {
///     return Results.BadRequest("Upload a PNG, JPEG, GIF or WebP image.");
/// }
/// </code>
/// </example>
public static class ImageFormats
{
    /// <summary>The most bytes any signature check looks at. Passing this many to <see cref="Detect"/> is always enough.</summary>
    /// <example><code>Span&lt;byte&gt; head = stackalloc byte[ImageFormats.MaxSignatureLength];</code></example>
    public const int MaxSignatureLength = 64;

    /// <summary>Every format that can be recognised, supported ones first.</summary>
    /// <example><code>string[] names = [.. ImageFormats.Known.Select(f => f.Name)];</code></example>
    public static IReadOnlyList<ImageFormat> Known { get; } =
    [
        ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Gif, ImageFormat.Webp,
        ImageFormat.Avif, ImageFormat.Heif, ImageFormat.JpegXl, ImageFormat.Tiff,
        ImageFormat.Bmp, ImageFormat.Ico, ImageFormat.Psd, ImageFormat.Qoi,
    ];

    /// <summary>Recognises the format from the first bytes of a file. Allocates nothing and never throws.</summary>
    /// <param name="head">
    /// The start of the file: <see cref="MaxSignatureLength"/> bytes, or the whole file if it's shorter. Bytes past
    /// that are ignored.
    /// </param>
    /// <returns>The format, or <see langword="null"/> when no known signature matches.</returns>
    /// <example>
    /// <code>
    /// ImageFormat? format = ImageFormats.Detect(bytes);
    /// Console.WriteLine(format?.Name ?? "not an image");
    /// </code>
    /// </example>
    public static ImageFormat? Detect(ReadOnlySpan<byte> head) => FormatSignatures.Detect(head);

    /// <summary>Whether the bytes start with the signature of a format this library supports.</summary>
    /// <param name="head">The start of the file, as for <see cref="Detect"/>.</param>
    /// <returns><see langword="true"/> for PNG, JPEG, GIF, WebP, BMP and TIFF.</returns>
    /// <example>
    /// <code>
    /// if (!ImageFormats.IsSupported(bytes))
    /// {
    ///     return Results.BadRequest("Upload a PNG, JPEG, GIF or WebP image.");
    /// }
    /// </code>
    /// </example>
    public static bool IsSupported(ReadOnlySpan<byte> head) => Detect(head) is { IsSupported: true };

    /// <summary>Recognises the format of a file on disk with a single read of its first bytes.</summary>
    /// <param name="path">The file to check.</param>
    /// <returns>The format, or <see langword="null"/> when no known signature matches.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="IOException">The file can't be opened or read.</exception>
    /// <exception cref="UnauthorizedAccessException">The caller isn't allowed to read the file.</exception>
    /// <example><code>ImageFormat? format = ImageFormats.DetectFile("photo.jpg");</code></example>
    public static ImageFormat? DetectFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> head = stackalloc byte[MaxSignatureLength];
        int length = 0;
        while (length < head.Length)
        {
            int read = RandomAccess.Read(handle, head[length..], length);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        return Detect(head[..length]);
    }

    /// <summary>
    /// Recognises the format at the current position of a seekable stream, then puts the position back where it was.
    /// </summary>
    /// <param name="stream">
    /// A readable, seekable stream, or the stream from <see cref="PeekAsync"/>, which gets its bytes back. For any other
    /// stream that can't seek, use <see cref="PeekAsync"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The format, or <see langword="null"/> when no known signature matches.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="stream"/> can't read or can't seek.</exception>
    /// <example>
    /// <code>
    /// await using FileStream file = File.OpenRead(path);
    /// ImageFormat? format = await ImageFormats.DetectAsync(file, cancellationToken);
    /// </code>
    /// </example>
    public static async ValueTask<ImageFormat?> DetectAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream is ReplayStream replay)
        {
            return await DetectReplayAsync(replay, cancellationToken).ConfigureAwait(false);
        }

        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new NotSupportedException("DetectAsync needs a readable, seekable stream. Use PeekAsync for other streams.");
        }

        long start = stream.Position;
        byte[] head = ArrayPool<byte>.Shared.Rent(MaxSignatureLength);
        try
        {
            int length = await stream.ReadAtLeastAsync(head.AsMemory(0, MaxSignatureLength), MaxSignatureLength, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            return Detect(head.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(head);
            stream.Position = start;
        }
    }

    /// <summary>
    /// Recognises the format of any readable stream, including one that can't seek such as a request body, without
    /// losing the bytes it read.
    /// </summary>
    /// <param name="stream">A readable stream.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The format and a stream to read the whole file from. For a seekable stream that is <paramref name="stream"/>
    /// itself, back at its starting position. Otherwise it is a read-only stream that returns the bytes already read,
    /// then the rest of <paramref name="stream"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Reads at most <see cref="MaxSignatureLength"/> bytes. The returned stream doesn't own <paramref name="stream"/>:
    /// disposing it leaves <paramref name="stream"/> open.
    /// </para>
    /// <para>
    /// Pass the returned stream to the rest of the library: <see cref="Image.IdentifyAsync"/>, <see cref="DetectAsync"/>
    /// and this method give back every byte they read from it, so it still holds the whole file for decoding or storing.
    /// Peeking it again returns the same stream rather than wrapping it a second time.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="stream"/> can't read.</exception>
    /// <example>
    /// <code>
    /// FormatPeek peek = await ImageFormats.PeekAsync(request.Body, cancellationToken);
    /// if (peek.Format is not { IsSupported: true })
    /// {
    ///     return Results.BadRequest("Upload a PNG, JPEG, GIF or WebP image.");
    /// }
    ///
    /// IdentifyResult result = await Image.IdentifyAsync(peek.Stream, cancellationToken: cancellationToken);
    /// </code>
    /// </example>
    public static async ValueTask<FormatPeek> PeekAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new NotSupportedException("PeekAsync needs a readable stream.");
        }

        if (stream.CanSeek || stream is ReplayStream)
        {
            ImageFormat? format = await DetectAsync(stream, cancellationToken).ConfigureAwait(false);
            return new FormatPeek(format, stream);
        }

        byte[] head = new byte[MaxSignatureLength];
        int length = await stream.ReadAtLeastAsync(head, MaxSignatureLength, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        return new FormatPeek(Detect(head.AsSpan(0, length)), new ReplayStream(head.AsMemory(0, length), stream));
    }

    private static async ValueTask<ImageFormat?> DetectReplayAsync(ReplayStream stream, CancellationToken cancellationToken)
    {
        byte[] head = ArrayPool<byte>.Shared.Rent(MaxSignatureLength);
        try
        {
            using ReplayStream.Rewind rewind = stream.Record();
            int length = await stream.ReadAtLeastAsync(head.AsMemory(0, MaxSignatureLength), MaxSignatureLength, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            return Detect(head.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(head);
        }
    }
}
