using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Imaging.Formats;

/// <summary>
/// The header check behind <see cref="Image.Identify"/>: detect the format, run its header reader, apply the limits,
/// and turn every <see cref="ImageException"/> into a result.
/// </summary>
internal static class Identifier
{
    public static async ValueTask<IdentifyResult> IdentifyAsync(HeaderReader reader, IdentifyOptions options, CancellationToken cancellationToken)
    {
        byte[] head = new byte[ImageFormats.MaxSignatureLength];
        int length = await reader.PeekAsync(head, cancellationToken).ConfigureAwait(false);
        ImageFormat? format = FormatSignatures.Detect(head.AsSpan(0, length));
        if (format is null)
        {
            return IdentifyResult.Failed(IdentifyStatus.NotAnImage, format: null, "The file isn't a PNG, JPEG, GIF, WebP, BMP or TIFF image.");
        }

        if (!format.IsSupported)
        {
            return await IdentifyWithCodecAsync(format, reader, options, cancellationToken).ConfigureAwait(false);
        }

        reader.FormatName = format.Name;
        try
        {
            ImageInfo info = await ReadHeadersAsync(format, reader, options, cancellationToken).ConfigureAwait(false);
            CheckLimits(info, options.Limits);
            return IdentifyResult.Ok(info);
        }
        catch (ImageException failure)
        {
            return IdentifyResult.Failed(failure.Status, format, failure.Message);
        }
    }

    // A format the library recognises but doesn't read: a codec from another package, if one was added.
    private static async ValueTask<IdentifyResult> IdentifyWithCodecAsync(ImageFormat format, HeaderReader reader, IdentifyOptions options, CancellationToken cancellationToken)
    {
        reader.FormatName = format.Name;
        try
        {
            ImageCodec codec = PluginCodecs.Find(options.Codecs, format)
                ?? throw new UnsupportedImageFormatException(
                    string.Create(CultureInfo.InvariantCulture, $"{format.Name} images aren't supported. Use PNG, JPEG, GIF, WebP, BMP or TIFF."),
                    format);
            ReadOnlyMemory<byte> head = await reader.PeekHeadAsync(cancellationToken).ConfigureAwait(false);
            return IdentifyResult.Ok(PluginCodecs.ReadHeader(codec, head.Span, options.Limits, cancellationToken));
        }
        catch (ImageException failure)
        {
            return IdentifyResult.Failed(failure.Status, format, failure.Message);
        }
    }

    private static ValueTask<ImageInfo> ReadHeadersAsync(ImageFormat format, HeaderReader reader, IdentifyOptions options, CancellationToken cancellationToken) =>
        CodecTable.For(format) is { } codec
            ? codec.ReadHeaders(reader, options, cancellationToken)
            : throw new UnsupportedImageFormatException(
                string.Create(CultureInfo.InvariantCulture, $"{format.Name} images aren't supported."),
                format);

    internal static void CheckLimits(ImageInfo info, ImageLimits limits)
    {
        if (info.Width <= 0 || info.Height <= 0)
        {
            throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The {info.Format.Name} file has a size of {info.Width}x{info.Height}."));
        }

        long pixels = (long)info.Width * info.Height;
        if (pixels > limits.MaxPixels)
        {
            throw ImageErrors.ExceedsLimits(string.Create(
                CultureInfo.InvariantCulture,
                $"The image is {info.Width}x{info.Height} ({pixels} pixels), over the limit of {limits.MaxPixels} pixels."));
        }

        if (info.FrameCount < 1)
        {
            throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The {info.Format.Name} file declares an animation with no frames."));
        }

        if (info.FrameCount > limits.MaxFrames)
        {
            throw ImageErrors.ExceedsLimits(FramesOverLimit(limits));
        }
    }

    /// <summary>The message for too many frames. Readers that stop counting early use it too.</summary>
    /// <param name="limits">The limits.</param>
    /// <returns>The message.</returns>
    public static string FramesOverLimit(ImageLimits limits) =>
        string.Create(CultureInfo.InvariantCulture, $"The image has more than {limits.MaxFrames} frames, the limit (ImageLimits.MaxFrames).");
}
