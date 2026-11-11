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
        return decoder.Decode(data, heif, context);
    }
}
