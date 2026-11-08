namespace Zorg.Imaging.Metadata;

/// <summary>
/// The metadata an image was decoded with, kept as the file had it so an encoder can write it back when asked
/// (<see cref="MetadataOptions"/>). Operations carry it to the images they make.
/// </summary>
/// <param name="Exif">The EXIF block as a TIFF structure (byte order mark first, no <c>Exif\0\0</c> prefix), or null.</param>
/// <param name="Xmp">The XMP packet, UTF-8, or null.</param>
/// <param name="OrientationApplied">
/// Whether the pixels have been turned since the file was read (by the decoder's orientation, a rotation or a flip),
/// so the EXIF orientation no longer describes them and is written as 1.
/// </param>
internal sealed record ImageMetadata(byte[]? Exif, byte[]? Xmp, bool OrientationApplied)
{
    /// <summary>Metadata from a decoder, or null when the file had none worth keeping.</summary>
    public static ImageMetadata? From(byte[]? exif, byte[]? xmp) =>
        exif is null && xmp is null ? null : new ImageMetadata(exif, xmp, OrientationApplied: false);
}
