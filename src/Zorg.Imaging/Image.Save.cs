using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Codecs.Jpeg;
using Zorg.Imaging.Codecs.Png;
using Zorg.Imaging.Codecs.Gif;
using Zorg.Imaging.Codecs.Webp;
using Zorg.Imaging.Processing;

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
    /// <para>
    /// The file is lossless and, by default, in the smallest layout that holds the pixels exactly: an opaque RGBA image
    /// is written as RGB, a grey one as grey, one of up to 256 colours as a palette, and so on
    /// (<see cref="PngEncoderOptions.ReduceColorType"/>). Turned off, the file is grey, RGB or RGBA at 8 or 16 bits as
    /// the image holds it. <see cref="PngEncoderOptions.Optimize"/> searches harder for a smaller file, as oxipng does.
    /// </para>
    /// <para>
    /// The <see cref="ColorSpace"/> is written so the file shows the same colours: nothing for sRGB, a <c>cICP</c> chunk
    /// for CICP code points (Display P3, HDR), an <c>iCCP</c> chunk for an ICC profile, or <c>gAMA</c> and <c>cHRM</c> as
    /// they were read. PNG keeps the colour space by default; <see cref="PngEncoderOptions.ColorSpaceHandling"/> can
    /// convert to sRGB first. EXIF and XMP from the source file are stripped unless
    /// <see cref="PngEncoderOptions.Metadata"/> keeps them.
    /// </para>
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
        options ??= PngEncoderOptions.Default;
        ValueTask task = SaveConvertedAsync(options.ColorSpaceHandling, 0, image => PngEncoder.EncodeAsync(image, stream, options, sync: true, CancellationToken.None));
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
        options ??= PngEncoderOptions.Default;
        return SaveConvertedAsync(options.ColorSpaceHandling, 0, image => PngEncoder.EncodeAsync(image, stream, options, sync: false, cancellationToken));
    }

    /// <summary>Writes the image as a JPEG file.</summary>
    /// <param name="stream">A writable stream. The file is written at its current position; the stream stays open.</param>
    /// <param name="options">The options, or <see langword="null"/> for <see cref="JpegEncoderOptions.Default"/>: quality 80, 4:2:0.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="stream"/> can't write; a side is longer than 65,535 pixels; or the <see cref="ColorSpace"/> is HDR
    /// (PQ or HLG), or one that can't be described as an ICC profile yet.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <exception cref="IOException">The stream itself fails.</exception>
    /// <remarks>
    /// <para>
    /// A baseline JFIF file, which every decoder reads: grey for <see cref="PixelFormat.L8"/> and
    /// <see cref="PixelFormat.L16"/>, YCbCr otherwise. JPEG has no transparency, so pixels with alpha are drawn over
    /// <see cref="JpegEncoderOptions.Background"/> (white unless set), and 16-bit samples are rounded to 8 bits.
    /// </para>
    /// <para>
    /// The <see cref="ColorSpace"/> is written as an ICC profile, so the file shows the same colours: nothing for sRGB,
    /// the image's own profile when it has one, or a small generated one (about 600 bytes) for Display P3, Rec.2020 and
    /// other spaces known by their primaries. First, by default (<see cref="ColorSpaceHandling.SrgbWhenLossless"/>), an image in another space whose
    /// colours all fit inside sRGB is converted to sRGB and written with no profile; <see cref="JpegEncoderOptions.ColorSpaceHandling"/>
    /// changes that. EXIF and XMP from the source file are stripped unless
    /// <see cref="JpegEncoderOptions.Metadata"/> keeps them.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// using FileStream file = File.Create("photo.jpg");
    /// image.SaveAsJpeg(file, new JpegEncoderOptions { Quality = 85 });
    /// </code>
    /// </example>
    public void SaveAsJpeg(Stream stream, JpegEncoderOptions? options = null)
    {
        CheckWritable(stream);
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        options ??= JpegEncoderOptions.Default;
        ValueTask task = SaveConvertedAsync(options.ColorSpaceHandling, options.MaxDegreeOfParallelism, image => JpegEncoder.EncodeAsync(image, stream, options, sync: true, CancellationToken.None));
        Debug.Assert(task.IsCompleted, "A synchronous encode must complete synchronously.");
        task.GetAwaiter().GetResult();
    }

    /// <summary>Writes the image as a JPEG file, asynchronously.</summary>
    /// <param name="stream">A writable stream. The file is written at its current position; the stream stays open.</param>
    /// <param name="options">The options, or <see langword="null"/> for <see cref="JpegEncoderOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="stream"/> can't write, a side is longer than 65,535 pixels, or the colour space can't be written.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <exception cref="IOException">The stream itself fails.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>The file is the same as <see cref="SaveAsJpeg"/> writes.</remarks>
    /// <example><code>await image.SaveAsJpegAsync(response.Body, cancellationToken: cancellationToken);</code></example>
    public ValueTask SaveAsJpegAsync(Stream stream, JpegEncoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        CheckWritable(stream);
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        options ??= JpegEncoderOptions.Default;
        return SaveConvertedAsync(options.ColorSpaceHandling, options.MaxDegreeOfParallelism, image => JpegEncoder.EncodeAsync(image, stream, options, sync: false, cancellationToken));
    }

    /// <summary>Writes the image as a WebP file.</summary>
    /// <param name="stream">A writable stream. The file is written at its current position; the stream stays open.</param>
    /// <param name="options">The options, or <see langword="null"/> for <see cref="WebpEncoderOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="stream"/> can't write; a side is longer than 16,383 pixels (lossy) or 16,384 (lossless); a lossy
    /// picture of tens of megapixels has more macroblocks than VP8's first partition can describe; or the
    /// <see cref="ColorSpace"/> is HDR (PQ or HLG), or one that can't be described as an ICC profile yet.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <exception cref="IOException">The stream itself fails.</exception>
    /// <remarks>
    /// <para>
    /// Lossy WebP (VP8, the default) stores Y'CbCr with chroma at half resolution, like a JPEG, at
    /// <see cref="WebpEncoderOptions.Quality"/>; transparency is kept exactly, coded losslessly beside it. Lossless WebP
    /// (VP8L) keeps every pixel, alpha included. Both store 8 bits a sample: 16-bit samples are rounded and grey is stored
    /// as RGB. Lossless images of up to 256 colours are stored as a colour table, which suits logos and screenshots.
    /// </para>
    /// <para>
    /// The <see cref="ColorSpace"/> is written as an ICC profile (in a <c>VP8X</c> file), so the file shows the same
    /// colours: nothing for sRGB, the image's own profile when it has one, or a small generated one for Display P3,
    /// Rec.2020 and other spaces known by their primaries. First, by default (<see cref="ColorSpaceHandling.SrgbWhenLossless"/>), an image in another space whose
    /// colours all fit inside sRGB is converted to sRGB and written with no profile; <see cref="WebpEncoderOptions.ColorSpaceHandling"/>
    /// changes that. EXIF and XMP from the source file are stripped unless
    /// <see cref="WebpEncoderOptions.Metadata"/> keeps them.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// using FileStream photo = File.Create("photo.webp");
    /// image.SaveAsWebp(photo); // lossy, quality 80
    ///
    /// using FileStream logo = File.Create("logo.webp");
    /// image.SaveAsWebp(logo, new WebpEncoderOptions { Lossless = true });
    /// </code>
    /// </example>
    public void SaveAsWebp(Stream stream, WebpEncoderOptions? options = null)
    {
        CheckWritable(stream);
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        options ??= WebpEncoderOptions.Default;
        ValueTask task = SaveConvertedAsync(options.ColorSpaceHandling, options.MaxDegreeOfParallelism, image => WebpEncoder.EncodeAsync(image, stream, options, sync: true, CancellationToken.None));
        Debug.Assert(task.IsCompleted, "A synchronous encode must complete synchronously.");
        task.GetAwaiter().GetResult();
    }

    /// <summary>Writes the image as a WebP file, asynchronously.</summary>
    /// <param name="stream">A writable stream. The file is written at its current position; the stream stays open.</param>
    /// <param name="options">The options, or <see langword="null"/> for <see cref="WebpEncoderOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="stream"/> can't write, the picture is too large for WebP, or the colour space can't be written.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <exception cref="IOException">The stream itself fails.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>The file is the same as <see cref="SaveAsWebp"/> writes.</remarks>
    /// <example><code>await image.SaveAsWebpAsync(response.Body, cancellationToken: cancellationToken);</code></example>
    public ValueTask SaveAsWebpAsync(Stream stream, WebpEncoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        CheckWritable(stream);
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        options ??= WebpEncoderOptions.Default;
        return SaveConvertedAsync(options.ColorSpaceHandling, options.MaxDegreeOfParallelism, image => WebpEncoder.EncodeAsync(image, stream, options, sync: false, cancellationToken));
    }

    /// <summary>Writes the image as a GIF file.</summary>
    /// <param name="stream">A writable stream. The file is written at its current position; the stream stays open.</param>
    /// <param name="options">The options, or <see langword="null"/> for <see cref="GifEncoderOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="stream"/> can't write; a side is longer than 65,535 pixels; or the <see cref="ColorSpace"/> is HDR
    /// (PQ or HLG), or one that can't be described as an ICC profile yet.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <exception cref="IOException">The stream itself fails.</exception>
    /// <remarks>
    /// <para>
    /// A GIF holds at most 256 colours. An image with no more than <see cref="GifEncoderOptions.MaxColors"/> is stored
    /// exactly, which suits logos, icons and pixel art; one with more is reduced with Wu's quantiser and dithered
    /// (<see cref="GifEncoderOptions.Dithering"/>). PNG or WebP keep photos better and smaller. Transparency is one bit:
    /// pixels with alpha below 128 are transparent, the rest opaque.
    /// </para>
    /// <para>
    /// The <see cref="ColorSpace"/> is written as an ICC profile in an <c>ICCRGBG1012</c> extension when it isn't sRGB.
    /// Few viewers read it: by default an image whose colours fit inside sRGB is converted to sRGB first, and
    /// <see cref="GifEncoderOptions.ColorSpaceHandling"/> set to <see cref="ColorSpaceHandling.Srgb"/> converts every
    /// image. No other metadata is written.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// using FileStream file = File.Create("icon.gif");
    /// image.SaveAsGif(file);
    /// </code>
    /// </example>
    public void SaveAsGif(Stream stream, GifEncoderOptions? options = null)
    {
        CheckWritable(stream);
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        options ??= GifEncoderOptions.Default;
        ValueTask task = SaveConvertedAsync(options.ColorSpaceHandling, options.MaxDegreeOfParallelism, image => GifEncoder.EncodeAsync(image, stream, options, sync: true, CancellationToken.None));
        Debug.Assert(task.IsCompleted, "A synchronous encode must complete synchronously.");
        task.GetAwaiter().GetResult();
    }

    /// <summary>Writes the image as a GIF file, asynchronously.</summary>
    /// <param name="stream">A writable stream. The file is written at its current position; the stream stays open.</param>
    /// <param name="options">The options, or <see langword="null"/> for <see cref="GifEncoderOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="stream"/> can't write, the picture is too large for GIF, or the colour space can't be written.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <exception cref="IOException">The stream itself fails.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>The file is the same as <see cref="SaveAsGif"/> writes.</remarks>
    /// <example><code>await image.SaveAsGifAsync(response.Body, cancellationToken: cancellationToken);</code></example>
    public ValueTask SaveAsGifAsync(Stream stream, GifEncoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        CheckWritable(stream);
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        options ??= GifEncoderOptions.Default;
        return SaveConvertedAsync(options.ColorSpaceHandling, options.MaxDegreeOfParallelism, image => GifEncoder.EncodeAsync(image, stream, options, sync: false, cancellationToken));
    }

    // Converts to sRGB first when the handling says so, encodes, and disposes of the converted copy. Completes
    // synchronously when the encode does.
    private async ValueTask SaveConvertedAsync(ColorSpaceHandling handling, int degree, Func<Image, ValueTask> encode)
    {
        Image? converted = null;
        if (handling != ColorSpaceHandling.Keep && SrgbConverter.CanConvert(ColorSpace))
        {
            using Parallelism.Scope scope = Parallelism.Use(degree);
            converted = SrgbConverter.Convert(this, onlyIfInside: handling == ColorSpaceHandling.SrgbWhenLossless);
        }

        try
        {
            await encode(converted ?? this).ConfigureAwait(false);
        }
        finally
        {
            converted?.Dispose();
        }
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
