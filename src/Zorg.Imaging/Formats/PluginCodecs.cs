using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Formats;

/// <summary>
/// Runs an <see cref="ImageCodec"/> from another package with the checks every built-in decoder has: the header first,
/// the limits before the pixels, the result checked against the header, and every failure as an
/// <see cref="ImageException"/>.
/// </summary>
internal static class PluginCodecs
{
    /// <summary>The first available codec for a format, from the call's list or the default one.</summary>
    /// <exception cref="UnsupportedImageFormatException">Codecs for the format exist, but none is available here.</exception>
    public static ImageCodec? Find(ImageCodecs? codecs, ImageFormat format)
    {
        ImageCodecs list = codecs ?? ImageDefaults.UseCodecs();
        ImageCodec? codec;
        string? unavailable;
        try
        {
            codec = list.Find(format, out unavailable);
        }
        catch (Exception e) when (e is not ImageException and not OperationCanceledException)
        {
            throw new ImageCodecException(
                string.Create(CultureInfo.InvariantCulture, $"Checking whether a {format.Name} codec can run failed: {e.Message}"),
                codecName: null,
                e);
        }

        if (codec is null && unavailable is not null)
        {
            throw new UnsupportedImageFormatException(unavailable, format);
        }

        return codec;
    }

    /// <summary>Reads the header with the codec and checks it against the limits.</summary>
    public static ImageInfo ReadHeader(ImageCodec codec, ReadOnlySpan<byte> head, ImageLimits limits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = new CodecContext(limits, PixelFormat.None, shrinkTarget: null, orientation: 1, cancellationToken);
        ImageInfo info;
        try
        {
            info = codec.ReadHeader(head, context);
        }
        catch (Exception e) when (e is not ImageException and not OperationCanceledException)
        {
            throw Failed(codec, "reading the header", e);
        }

        if (info is null || info.Format != codec.Format || info.Width <= 0 || info.Height <= 0 || info.Orientation is < 1 or > 8)
        {
            throw Broken(codec, "returned a header that doesn't describe a " + codec.Format.Name + " image");
        }

        Identifier.CheckLimits(info, limits);
        return info;
    }

    /// <summary>Reads the whole file, then the header, then decodes with the codec and turns the result upright.</summary>
    public static async ValueTask<Image> DecodeAsync(ImageCodec codec, DataReader reader, DecodeOptions options, CancellationToken cancellationToken)
    {
        ImageLimits limits = options.Limits;
        reader.FormatName = codec.Format.Name;
        ReadOnlyMemory<byte> data = await reader.ReadToEndAsync(limits.MaxFileBytes, cancellationToken).ConfigureAwait(false);
        ImageInfo header = ReadHeader(codec, data.Span[..Math.Min(data.Length, limits.MaxHeaderBytes)], limits, cancellationToken);

        int orientation = options.ApplyOrientation ? header.Orientation : 1;
        var context = new CodecContext(limits, options.PixelFormat, options.ShrinkTarget, orientation, cancellationToken);
        Image image;
        try
        {
            image = codec.Decode(data, header, context);
        }
        catch (Exception e) when (e is not ImageException and not OperationCanceledException)
        {
            throw Failed(codec, "decoding", e);
        }

        try
        {
            if (image is null)
            {
                throw Broken(codec, "returned no image");
            }

            CheckResult(codec, image, header, context, options.PixelFormat);
            cancellationToken.ThrowIfCancellationRequested();
            if (orientation != 1)
            {
                Image upright = Orientation.Apply(image, orientation);
                image.Dispose();
                image = upright;
            }

            return image;
        }
        catch
        {
            image?.Dispose();
            throw;
        }
    }

    private static void CheckResult(ImageCodec codec, Image image, ImageInfo header, CodecContext context, PixelFormat wanted)
    {
        bool fullSize = image.Width == header.Width && image.Height == header.Height;
        bool shrunk = context.ShrinkTo(header.Width, header.Height) is not null
            && image.Width <= header.Width && image.Height <= header.Height;
        if (!fullSize && !shrunk)
        {
            throw Broken(codec, string.Create(
                CultureInfo.InvariantCulture,
                $"decoded {image.Width}x{image.Height} pixels where the header says {header.Width}x{header.Height}"));
        }

        if (wanted != PixelFormat.None && image.PixelFormat != wanted)
        {
            throw Broken(codec, string.Create(CultureInfo.InvariantCulture, $"returned {image.PixelFormat} pixels where {wanted} was asked for"));
        }
    }

    private static ImageCodecException Failed(ImageCodec codec, string what, Exception e) =>
        new(string.Create(CultureInfo.InvariantCulture, $"The {codec.Name} codec failed {what}: {e.Message}"), codec.Name, e);

    private static ImageCodecException Broken(ImageCodec codec, string what) =>
        new(string.Create(CultureInfo.InvariantCulture, $"The {codec.Name} codec {what}."), codec.Name, innerException: null);
}
