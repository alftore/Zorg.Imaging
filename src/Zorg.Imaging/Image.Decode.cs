using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Codecs.Gif;
using Zorg.Imaging.Codecs.Jpeg;
using Zorg.Imaging.Codecs.Png;
using Zorg.Imaging.Codecs.Webp;
using Zorg.Imaging.Formats;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging;

/// <content>Decoding.</content>
public sealed partial class Image
{
    /// <summary>Decodes an image file.</summary>
    /// <param name="data">The whole file.</param>
    /// <param name="options">The limits and options, or <see langword="null"/> for <see cref="DecodeOptions.Default"/>.</param>
    /// <returns>The image, which the caller disposes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    /// <exception cref="UnsupportedImageFormatException">
    /// The data isn't an image format this version can decode. So far that is PNG, JPEG, GIF and WebP; of an animation, the first frame.
    /// </exception>
    /// <exception cref="ImageFormatException">The file is damaged or ends early.</exception>
    /// <exception cref="ImageLimitException">The image is larger than <see cref="ImageLimits"/> allows.</exception>
    /// <remarks>
    /// The pixels are turned upright from the file's EXIF orientation unless <see cref="DecodeOptions.ApplyOrientation"/>
    /// is off. Colour values are as stored: colour profiles and gamma aren't applied yet. To check a file without
    /// decoding it, use <see cref="Identify"/>, which never throws for bad input.
    /// </remarks>
    /// <example>
    /// <code>
    /// using Image image = Image.Decode(await File.ReadAllBytesAsync("photo.png"));
    /// Console.WriteLine(image); // Rgba32 640x480
    /// </code>
    /// </example>
    public static Image Decode(byte[] data, DecodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        options ??= DecodeOptions.Default;

        // Over memory every read completes at once, so the task has already finished.
        ValueTask<Image> task = DecodeCoreAsync(DataReader.FromMemory(data), options, sync: true, CancellationToken.None);
        Debug.Assert(task.IsCompleted, "Decode over memory must complete synchronously.");
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Decodes an image file from a stream.</summary>
    /// <param name="stream">
    /// A readable stream positioned at the start of the file. It's read up to the end of the image; a seekable stream is
    /// left just after it. The stream from <see cref="ImageFormats.PeekAsync"/> works here, including after
    /// <see cref="IdentifyAsync"/>.
    /// </param>
    /// <param name="options">The limits and options, or <see langword="null"/> for <see cref="DecodeOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The image, which the caller disposes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="stream"/> can't read.</exception>
    /// <exception cref="UnsupportedImageFormatException">The data isn't an image format this version can decode.</exception>
    /// <exception cref="ImageFormatException">The file is damaged or ends early.</exception>
    /// <exception cref="ImageLimitException">The image is larger than <see cref="ImageLimits"/> allows.</exception>
    /// <exception cref="IOException">The stream itself fails.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <example>
    /// <code>
    /// await using FileStream file = File.OpenRead("photo.png");
    /// using Image image = await Image.DecodeAsync(file, cancellationToken: cancellationToken);
    /// </code>
    /// </example>
    public static async ValueTask<Image> DecodeAsync(Stream stream, DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new NotSupportedException("DecodeAsync needs a readable stream.");
        }

        options ??= DecodeOptions.Default;
        return await DecodeCoreAsync(DataReader.FromStream(stream), options, sync: false, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<Image> DecodeCoreAsync(DataReader reader, DecodeOptions options, bool sync, CancellationToken cancellationToken)
    {
        using Parallelism.Scope scope = Parallelism.Use(options.MaxDegreeOfParallelism);
        ReadOnlyMemory<byte> head = await reader.PeekAsync(ImageFormats.MaxSignatureLength, cancellationToken).ConfigureAwait(false);
        ImageFormat? format = ImageFormats.Detect(head.Span);
        if (format == ImageFormat.Png)
        {
            return await PngDecoder.DecodeAsync(reader, options, sync, cancellationToken).ConfigureAwait(false);
        }

        if (format == ImageFormat.Jpeg)
        {
            return await JpegDecoder.DecodeAsync(reader, options, cancellationToken).ConfigureAwait(false);
        }

        if (format == ImageFormat.Gif)
        {
            return await GifDecoder.DecodeAsync(reader, options, cancellationToken).ConfigureAwait(false);
        }

        if (format == ImageFormat.Webp)
        {
            return await WebpDecoder.DecodeAsync(reader, options, cancellationToken).ConfigureAwait(false);
        }

        throw format is null
            ? new UnsupportedImageFormatException("The data isn't in an image format Zorg.Imaging recognises.")
            : new UnsupportedImageFormatException(
                string.Create(CultureInfo.InvariantCulture, $"Decoding {format.Name} isn't supported yet."),
                format);
    }
}
