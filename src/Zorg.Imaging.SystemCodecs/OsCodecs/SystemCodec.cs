using System;
using Zorg.Imaging.OsCodecs.Heif;

namespace Zorg.Imaging.OsCodecs;

/// <summary>One of the two codecs: reads the header in managed code, then hands the file to the system's decoder.</summary>
internal sealed class SystemCodec(ImageFormat format, SystemDecoder decoder) : ImageCodec
{
    public override ImageFormat Format => format;

    public override string Name => (format == ImageFormat.Avif ? "AVIF" : "HEIC") + " (" + decoder.Name + ")";

    public override bool UsesNativeCode => true;

    public override CodecAvailability Availability => decoder.Availability(format);

    public override ImageInfo ReadHeader(ReadOnlySpan<byte> head, CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        HeifHeader header = HeifReader.Read(head, context.Limits);

        // HEIF's crop, turn and mirror are part of the image (MIAF), not a hint: the size is the shown one, and the
        // decoders return the image as shown, so there's no orientation left for Zorg.Imaging to apply.
        return new ImageInfo(format, header.DisplayWidth, header.DisplayHeight)
        {
            HasAlpha = header.HasAlpha,
            ColorSpace = header.ColorSpace,
            HasIccProfile = header.ColorSpace is not null,
            HasExif = header.HasExif,
        };
    }

    public override Image Decode(ReadOnlyMemory<byte> data, ImageInfo header, CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(context);
        context.CancellationToken.ThrowIfCancellationRequested();
        HeifHeader heif = HeifReader.Read(data.Span[..Math.Min(data.Length, context.Limits.MaxHeaderBytes)], context.Limits);
        Image image = decoder.Decode(data, heif, context, ScaleFor(heif, context));
        try
        {
            byte[]? exif = heif.ExifItem is { } exifItem && HeifReader.ReadItem(data.Span, exifItem, context.Limits.MaxHeaderBytes) is { } exifBytes
                ? HeifReader.ExifTiff(exifBytes)
                : null;
            byte[]? xmp = heif.XmpItem is { } xmpItem ? HeifReader.ReadItem(data.Span, xmpItem, context.Limits.MaxHeaderBytes) : null;

            // The pixels come back turned as the file says, so an EXIF orientation is written as 1.
            AttachMetadata(image, exif, xmp, orientationApplied: true);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The size to decode at when the caller will shrink the picture anyway (as <see cref="ImagePipeline"/> does before a
    /// <c>Fit</c>): the smallest of 1/2, 1/4 and 1/8 that keeps at least twice what it needs, as JPEG's shrink-on-load
    /// does, so the library's linear-light resize does the last halving. Null for full size.
    /// </summary>
    internal static (int Width, int Height)? ScaleFor(HeifHeader heif, CodecContext context)
    {
        if (context.ShrinkTo(heif.DisplayWidth, heif.DisplayHeight) is not (int needWidth, int needHeight))
        {
            return null;
        }

        for (int factor = 8; factor >= 2; factor /= 2)
        {
            int width = (heif.DisplayWidth + factor - 1) / factor;
            int height = (heif.DisplayHeight + factor - 1) / factor;
            if (width >= 2L * needWidth && height >= 2L * needHeight)
            {
                return (width, height);
            }
        }

        return null;
    }
}
