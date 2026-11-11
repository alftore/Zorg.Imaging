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
        return new ImageInfo(format, header.Width, header.Height)
        {
            Orientation = header.Orientation,
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
        return decoder.Decode(data, header, context);
    }
}
