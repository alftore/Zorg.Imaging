using System;

namespace Zorg.Imaging;

/// <summary>
/// Which of the decoded file's metadata an encoder writes back: none by default (<see cref="Strip"/>), because EXIF can
/// reveal where a photo was taken and with which device; all of it (<see cref="Keep"/>); all but the personal parts
/// (<see cref="KeepNonPersonal"/>); or only who owns it (<see cref="CopyrightOnly"/>). <see cref="Without"/> leaves out
/// other groups of EXIF tags.
/// </summary>
/// <remarks>
/// <para>
/// Metadata comes from the file the image was decoded from: EXIF and XMP from JPEG, PNG and WebP, and IPTC from JPEG.
/// Images made in code have none. <see cref="Image.Metadata"/> reads it whatever these options say. Colour profiles
/// aren't metadata here: see <see cref="ColorSpaceHandling"/>.
/// </para>
/// <para>
/// EXIF is rebuilt, not copied: its orientation becomes 1 when the pixels have been turned upright (the default
/// decode), its pixel dimensions are updated to the image saved, and the embedded thumbnail and the maker note (private
/// camera data, which can't be moved safely and often holds serial numbers) are dropped. A damaged EXIF block is
/// dropped rather than copied. XMP is copied as it is; it can repeat what EXIF says, location included, so it's kept
/// only by <see cref="Keep"/> with no group left out.
/// </para>
/// <para>
/// IPTC is written back to JPEG only (PNG and WebP have no standard place for it), and only its descriptive records:
/// the groups left out drop its place names and the creator's contact details (<see cref="ExifGroup.Location"/>) and
/// its dates (<see cref="ExifGroup.Dates"/>), and <see cref="CopyrightOnly"/> keeps just the by-line and copyright.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var options = new JpegEncoderOptions { Metadata = MetadataOptions.Keep.Without(ExifGroup.Location) };
/// image.SaveAsJpeg(output, options);
/// </code>
/// </example>
public sealed class MetadataOptions
{
    private MetadataOptions(bool exif, bool xmp, bool copyrightOnly, ExifGroup without)
    {
        KeepsExif = exif;
        KeepsXmp = xmp;
        IsCopyrightOnly = copyrightOnly;
        Excluded = without;
    }

    /// <summary>No metadata: the default, and the safe choice for images published on the web.</summary>
    /// <example><code>var options = new PngEncoderOptions { Metadata = MetadataOptions.Strip };</code></example>
    public static MetadataOptions Strip { get; } = new(exif: false, xmp: false, copyrightOnly: false, ExifGroup.None);

    /// <summary>EXIF and XMP, as described in the remarks.</summary>
    /// <example><code>var options = new JpegEncoderOptions { Metadata = MetadataOptions.Keep };</code></example>
    public static MetadataOptions Keep { get; } = new(exif: true, xmp: true, copyrightOnly: false, ExifGroup.None);

    /// <summary>
    /// EXIF without what can identify a person or place: no location (GPS) and no camera details (make, model, lens,
    /// serial numbers, owner's name). Exposure, dates, artist and copyright stay. XMP is dropped, since it can repeat
    /// what was left out. The same as <c>Keep.Without(ExifGroup.Location | ExifGroup.Camera)</c>.
    /// </summary>
    /// <example><code>var options = new JpegEncoderOptions { Metadata = MetadataOptions.KeepNonPersonal };</code></example>
    public static MetadataOptions KeepNonPersonal { get; } = Keep.Without(ExifGroup.Location | ExifGroup.Camera);

    /// <summary>Only the EXIF artist and copyright, so the image still says whose it is, and nothing else.</summary>
    /// <example><code>var options = new WebpEncoderOptions { Metadata = MetadataOptions.CopyrightOnly };</code></example>
    public static MetadataOptions CopyrightOnly { get; } = new(exif: true, xmp: false, copyrightOnly: true, ExifGroup.None);

    /// <summary>The EXIF groups left out.</summary>
    /// <example><code>bool noGps = options.Excluded.HasFlag(ExifGroup.Location);</code></example>
    public ExifGroup Excluded { get; }

    internal bool KeepsExif { get; }

    internal bool KeepsXmp { get; }

    internal bool IsCopyrightOnly { get; }

    /// <summary>These options with more EXIF groups left out. Leaving any out also drops XMP, which may repeat them.</summary>
    /// <param name="groups">The groups to leave out.</param>
    /// <returns>The new options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="groups"/> has bits that aren't groups.</exception>
    /// <example><code>MetadataOptions options = MetadataOptions.Keep.Without(ExifGroup.Location);</code></example>
    public MetadataOptions Without(ExifGroup groups)
    {
        if ((groups & ~(ExifGroup.Location | ExifGroup.Camera | ExifGroup.Dates)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(groups), groups, "Not a combination of ExifGroup values.");
        }

        ExifGroup excluded = Excluded | groups;
        return new MetadataOptions(KeepsExif, KeepsXmp && excluded == ExifGroup.None, IsCopyrightOnly, excluded);
    }

    /// <summary>Describes the options, such as <c>Keep without Location</c>.</summary>
    /// <returns>A short description.</returns>
    /// <example><code>Console.WriteLine(options);</code></example>
    public override string ToString()
    {
        string name = !KeepsExif ? "Strip" : IsCopyrightOnly ? "CopyrightOnly" : "Keep";
        return Excluded == ExifGroup.None ? name : $"{name} without {Excluded}";
    }
}
