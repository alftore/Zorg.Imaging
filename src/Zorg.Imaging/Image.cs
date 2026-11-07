using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging;

/// <summary>An image. For now this holds the header check; the pixel buffer comes in a later slice.</summary>
/// <example>
/// <code>
/// IdentifyResult result = Image.Identify(bytes);
/// if (!result.Succeeded)
/// {
///     return Results.BadRequest(result.Message);
/// }
/// </code>
/// </example>
[SuppressMessage("Design", "CA1052:Static holder types should be Static or NotInheritable", Justification = "Becomes the pixel buffer type; the identify methods stay as statics on it.")]
public sealed class Image
{
    private Image()
    {
    }

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
    /// and skipped data is seeked over rather than read. A stream that can't seek keeps whatever was read: use
    /// <c>Request.EnableBuffering()</c> or <c>IFormFile.OpenReadStream()</c> when the body is needed afterwards.
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
    /// IdentifyResult result = await Image.IdentifyAsync(upload.OpenReadStream(), cancellationToken: cancellationToken);
    /// if (!result.Succeeded)
    /// {
    ///     return Results.BadRequest(result.Message);
    /// }
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
        long start = stream.CanSeek ? stream.Position : 0;
        try
        {
            var reader = HeaderReader.FromStream(stream, options.Limits.MaxHeaderBytes);
            return await Identifier.IdentifyAsync(reader, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = start;
            }
        }
    }
}
