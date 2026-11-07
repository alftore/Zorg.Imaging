using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Codecs.Png;

namespace Zorg.Imaging;

/// <content>Encoding.</content>
public sealed partial class Image
{
    /// <summary>Writes the image as a PNG file.</summary>
    /// <param name="stream">A writable stream. The file is written at its current position; the stream stays open.</param>
    /// <param name="options">The options, or <see langword="null"/> for <see cref="PngEncoderOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="stream"/> can't write.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <exception cref="IOException">The stream itself fails.</exception>
    /// <remarks>
    /// The file is 8-bit grey (<see cref="PixelFormat.L8"/>), RGB (<see cref="PixelFormat.Rgb24"/>) or RGBA
    /// (<see cref="PixelFormat.Rgba32"/> and <see cref="PixelFormat.Bgra32"/>), and carries no metadata.
    /// </remarks>
    /// <example>
    /// <code>
    /// using FileStream file = File.Create("thumb.png");
    /// image.SaveAsPng(file);
    /// </code>
    /// </example>
    public void SaveAsPng(Stream stream, PngEncoderOptions? options = null)
    {
        CheckWritable(stream);
        ObjectDisposedException.ThrowIf(_buffer is null, this);

        // Every write is synchronous, so the task has already finished.
        ValueTask task = PngEncoder.EncodeAsync(this, stream, options ?? PngEncoderOptions.Default, sync: true, CancellationToken.None);
        Debug.Assert(task.IsCompleted, "A synchronous encode must complete synchronously.");
        task.GetAwaiter().GetResult();
    }

    /// <summary>Writes the image as a PNG file, asynchronously.</summary>
    /// <param name="stream">A writable stream. The file is written at its current position; the stream stays open.</param>
    /// <param name="options">The options, or <see langword="null"/> for <see cref="PngEncoderOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="stream"/> can't write.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <exception cref="IOException">The stream itself fails.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>The file is the same as <see cref="SaveAsPng"/> writes.</remarks>
    /// <example>
    /// <code>
    /// await image.SaveAsPngAsync(response.Body, cancellationToken: cancellationToken);
    /// </code>
    /// </example>
    public ValueTask SaveAsPngAsync(Stream stream, PngEncoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        CheckWritable(stream);
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        return PngEncoder.EncodeAsync(this, stream, options ?? PngEncoderOptions.Default, sync: false, cancellationToken);
    }

    private static void CheckWritable(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new NotSupportedException("Saving needs a writable stream.");
        }
    }
}
