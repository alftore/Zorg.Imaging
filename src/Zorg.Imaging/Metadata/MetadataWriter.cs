namespace Zorg.Imaging.Metadata;

/// <summary>What an encoder writes of an image's metadata under <see cref="MetadataOptions"/>.</summary>
internal static class MetadataWriter
{
    /// <summary>The EXIF block to write (a TIFF structure) and the XMP packet, either null when there's nothing.</summary>
    public static (byte[]? Exif, byte[]? Xmp) Prepare(Image image, MetadataOptions options)
    {
        if (image.SourceMetadata is not { } metadata || !options.KeepsExif)
        {
            return (null, null);
        }

        byte[]? exif = metadata.Exif is null
            ? null
            : ExifWriter.Rewrite(metadata.Exif, image.Width, image.Height, metadata.OrientationApplied, options);
        return (exif, options.KeepsXmp ? metadata.Xmp : null);
    }

    /// <summary>The IPTC records to write, filtered as <see cref="Iptc.Filter"/> describes, or null.</summary>
    public static byte[]? PrepareIptc(Image image, MetadataOptions options) =>
        image.SourceMetadata is { Iptc: { } iptc } && options.KeepsExif ? Iptc.Filter(iptc, options) : null;
}
