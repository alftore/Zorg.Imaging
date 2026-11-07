using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging;

/// <summary>
/// An image in memory: a width, a height, a <see cref="PixelFormat"/> and the pixels, row after row with no padding.
/// The static <see cref="Identify"/> methods read a file's headers without decoding it.
/// </summary>
/// <remarks>
/// The pixel memory is rented from a shared pool and goes back when the image is disposed, so dispose every image. An
/// image isn't thread-safe for writes; several threads may read the same image at once.
/// </remarks>
/// <example>
/// <code>
/// using var image = new Image(320, 200, PixelFormat.Rgba32);
/// Span&lt;Rgba32&gt; top = image.GetRow&lt;Rgba32&gt;(0);
/// top.Fill(new Rgba32(255, 255, 255));
/// </code>
/// </example>
public sealed partial class Image
{
    /// <summary>
    /// Reads an image's headers, without decoding pixels, and checks them against the limits. Bad input gives a
    /// result, never an exception.
    /// </summary>
    /// <param name="data">The whole file, or at least its start.</param>
    /// <param name="options">The limits and options, or <see langword="null"/> for <see cref="IdentifyOptions.Default"/>.</param>
    /// <returns>The result: <see cref="IdentifyResult.Succeeded"/> with <see cref="IdentifyResult.Info"/>, or a status and message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// IdentifyResult result = Image.Identify(await File.ReadAllBytesAsync(path));
    /// if (result.Succeeded)
    /// {
    ///     Console.WriteLine(result.Info); // JPEG 3024x4032
    /// }
    /// </code>
    /// </example>
    public static IdentifyResult Identify(byte[] data, IdentifyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        options ??= IdentifyOptions.Default;
        var reader = HeaderReader.FromMemory(data, options.Limits.MaxHeaderBytes);

        // Over memory every read completes at once, so the task has already finished.
        ValueTask<IdentifyResult> task = Identifier.IdentifyAsync(reader, options, CancellationToken.None);
        Debug.Assert(task.IsCompleted, "Identify over memory must complete synchronously.");
        return task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Reads an image's headers from a stream, without decoding pixels, and checks them against the limits. Bad input
    /// gives a result, never an exception.
    /// </summary>
    /// <param name="stream">
    /// A readable stream positioned at the start of the file. A seekable stream is put back at that position afterwards,
    /// and skipped data is seeked over rather than read. The stream from <see cref="ImageFormats.PeekAsync"/> gets back
    /// every byte read, so it can still be read in full afterwards. Any other stream that can't seek loses what was read:
    /// pass it through <see cref="ImageFormats.PeekAsync"/> first when the body is needed afterwards.
    /// </param>
    /// <param name="options">The limits and options, or <see langword="null"/> for <see cref="IdentifyOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The result: <see cref="IdentifyResult.Succeeded"/> with <see cref="IdentifyResult.Info"/>, or a status and message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="stream"/> can't read.</exception>
    /// <exception cref="IOException">The stream itself fails.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <example>
    /// <code>
    /// FormatPeek peek = await ImageFormats.PeekAsync(request.Body, cancellationToken);
    /// IdentifyResult result = await Image.IdentifyAsync(peek.Stream, cancellationToken: cancellationToken);
    /// if (!result.Succeeded)
    /// {
    ///     return Results.BadRequest(result.Message);
    /// }
    ///
    /// await peek.Stream.CopyToAsync(storage, cancellationToken); // still the whole file
    /// </code>
    /// </example>
    public static async ValueTask<IdentifyResult> IdentifyAsync(Stream stream, IdentifyOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new NotSupportedException("IdentifyAsync needs a readable stream.");
        }

        options ??= IdentifyOptions.Default;
        if (stream is ReplayStream replay)
        {
            using ReplayStream.Rewind rewind = replay.Record();
            return await IdentifyStreamAsync(stream, options, cancellationToken).ConfigureAwait(false);
        }

        long start = stream.CanSeek ? stream.Position : 0;
        try
        {
            return await IdentifyStreamAsync(stream, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = start;
            }
        }
    }

    private static ValueTask<IdentifyResult> IdentifyStreamAsync(Stream stream, IdentifyOptions options, CancellationToken cancellationToken)
    {
        var reader = HeaderReader.FromStream(stream, options.Limits.MaxHeaderBytes);
        return Identifier.IdentifyAsync(reader, options, cancellationToken);
    }
}
