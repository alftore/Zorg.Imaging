using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Codecs.Bmp;
using Zorg.Imaging.Codecs.Gif;
using Zorg.Imaging.Codecs.Jpeg;
using Zorg.Imaging.Codecs.Png;
using Zorg.Imaging.Codecs.Tiff;
using Zorg.Imaging.Codecs.Webp;

namespace Zorg.Imaging.Formats;

/// <summary>
/// The built-in codecs, one entry per format: its header reader, its decoder and, when it has one, its encoder with
/// default options. <see cref="Image.Decode(byte[], DecodeOptions?)"/>, <see cref="Image.Identify(byte[], IdentifyOptions?)"/>
/// and <see cref="ImagePipeline"/> choose through this table rather than by naming formats.
/// </summary>
internal static class CodecTable
{
    private static readonly BuiltInCodec[] All =
    [
        new(ImageFormat.Png, PngHeaderReader.ReadAsync, PngDecoder.DecodeAsync, static (image, stream, token) => image.SaveAsPngAsync(stream, null, token)),
        new(ImageFormat.Jpeg, JpegHeaderReader.ReadAsync, static (reader, options, _, token) => JpegDecoder.DecodeAsync(reader, options, token), static (image, stream, token) => image.SaveAsJpegAsync(stream, null, token)),
        new(ImageFormat.Gif, GifHeaderReader.ReadAsync, static (reader, options, _, token) => GifDecoder.DecodeAsync(reader, options, token), static (image, stream, token) => image.SaveAsGifAsync(stream, null, token)),
        new(ImageFormat.Webp, WebpHeaderReader.ReadAsync, static (reader, options, _, token) => WebpDecoder.DecodeAsync(reader, options, token), static (image, stream, token) => image.SaveAsWebpAsync(stream, null, token)),
        new(ImageFormat.Tiff, TiffHeaderReader.ReadAsync, static (reader, options, _, token) => TiffDecoder.DecodeAsync(reader, options, token), Save: null),
        new(ImageFormat.Bmp, BmpHeaderReader.ReadAsync, static (reader, options, _, token) => BmpDecoder.DecodeAsync(reader, options, token), Save: null),
    ];

    /// <summary>The codec for a format, or <see langword="null"/> when none is built in.</summary>
    public static BuiltInCodec? For(ImageFormat? format)
    {
        foreach (BuiltInCodec codec in All)
        {
            if (codec.Format == format)
            {
                return codec;
            }
        }

        return null;
    }

    /// <summary>The format with an encoder whose file extensions include this one (with its dot), ignoring case.</summary>
    public static ImageFormat? EncodableFormatOf(string extension)
    {
        foreach (BuiltInCodec codec in All)
        {
            if (codec.Save is null)
            {
                continue;
            }

            foreach (string known in codec.Format.FileExtensions)
            {
                if (string.Equals(known, extension, StringComparison.OrdinalIgnoreCase))
                {
                    return codec.Format;
                }
            }
        }

        return null;
    }
}

/// <summary>Reads a format's headers for <see cref="Identifier"/>.</summary>
internal delegate ValueTask<ImageInfo> ReadHeaders(HeaderReader reader, IdentifyOptions options, CancellationToken cancellationToken);

/// <summary>Decodes a format. <paramref name="sync"/> is true when the reader is over memory and must not yield.</summary>
internal delegate ValueTask<Image> DecodeImage(DataReader reader, DecodeOptions options, bool sync, CancellationToken cancellationToken);

/// <summary>A built-in format: its header reader, decoder and, if it can be written, encoder with default options.</summary>
internal sealed record BuiltInCodec(
    ImageFormat Format,
    ReadHeaders ReadHeaders,
    DecodeImage Decode,
    Func<Image, Stream, CancellationToken, ValueTask>? Save);
