using System;
using System.Collections.Generic;
using System.Globalization;
using Zorg.Imaging.Metadata;

namespace Zorg.Imaging;

/// <summary>
/// The metadata an image was decoded with: the EXIF, XMP and IPTC blocks as the file had them, and the facts most often
/// wanted from them. Reading it changes nothing: whether metadata is written back is up to the encoder's
/// <see cref="MetadataOptions"/>, which strip it by default.
/// </summary>
/// <remarks>
/// <para>
/// Each fact is taken from EXIF where it has one, then from IPTC. XMP is given only as its packet, since it is XML and
/// often repeats the other two. A damaged block gives no facts rather than an error; its raw bytes are still here.
/// </para>
/// <para>
/// Text is as the file stored it. Nothing here has been checked against the picture: a camera's clock may be wrong and
/// a location may have been edited, so treat these as claims made by the file.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// using Image photo = Image.Decode(bytes);
/// if (photo.Metadata is { Location: { } where })
/// {
///     Console.WriteLine($"Taken at {where.Latitude}, {where.Longitude}");
/// }
/// </code>
/// </example>
public sealed class ImageMetadata
{
    internal ImageMetadata(SourceMetadata source)
    {
        Source = source;
        Exif = source.Exif;
        Xmp = source.Xmp;
        Iptc = source.Iptc;

        ExifReader.Tags? exif = source.Exif is null ? null : ExifReader.Read(source.Exif);
        List<Iptc.DataSet> iptc = source.Iptc is null ? [] : Metadata.Iptc.Read(source.Iptc);

        if (exif is not null)
        {
            CameraMake = ExifReader.Tags.Text(exif.Main, 0x010F);
            CameraModel = ExifReader.Tags.Text(exif.Main, 0x0110);
            LensModel = ExifReader.Tags.Text(exif.Exif, 0xA434);
            Artist = ExifReader.Tags.Text(exif.Main, 0x013B);
            Copyright = ExifReader.Tags.Text(exif.Main, 0x8298);
            Description = ExifReader.Tags.Text(exif.Main, 0x010E);
            Orientation = exif.Number(exif.Main, 0x0112) is uint orientation and >= 1 and <= 8 ? (int)orientation : 1;
            (DateTaken, DateTakenOffset) = ExifDate(exif);
            Location = ExifLocation(exif);
        }

        if (iptc.Count > 0)
        {
            Title = First(Metadata.Iptc.Texts(iptc, 5));
            Artist ??= Joined(Metadata.Iptc.Texts(iptc, 80));
            Copyright ??= First(Metadata.Iptc.Texts(iptc, 116));
            Description ??= First(Metadata.Iptc.Texts(iptc, 120));
            Keywords = Metadata.Iptc.Texts(iptc, 25);
            if (DateTaken is null)
            {
                (DateTaken, DateTakenOffset) = IptcDate(First(Metadata.Iptc.Texts(iptc, 55)), First(Metadata.Iptc.Texts(iptc, 60)));
            }
        }
    }

    /// <summary>The EXIF block as a TIFF structure (byte order mark first), or empty when the file had none.</summary>
    /// <example><code>ReadOnlyMemory&lt;byte&gt; exif = photo.Metadata?.Exif ?? default;</code></example>
    public ReadOnlyMemory<byte> Exif { get; }

    /// <summary>The XMP packet, UTF-8 XML, or empty when the file had none.</summary>
    /// <example><code>string xmp = System.Text.Encoding.UTF8.GetString(photo.Metadata!.Xmp.Span);</code></example>
    public ReadOnlyMemory<byte> Xmp { get; }

    /// <summary>The IPTC IIM records (from a JPEG's Photoshop APP13 segment), or empty when the file had none.</summary>
    /// <example><code>bool hasIptc = !photo.Metadata!.Iptc.IsEmpty;</code></example>
    public ReadOnlyMemory<byte> Iptc { get; }

    /// <summary>The camera's maker (EXIF Make), such as <c>Canon</c>.</summary>
    /// <example><code>string? make = photo.Metadata?.CameraMake;</code></example>
    public string? CameraMake { get; }

    /// <summary>The camera's model (EXIF Model).</summary>
    /// <example><code>string? model = photo.Metadata?.CameraModel;</code></example>
    public string? CameraModel { get; }

    /// <summary>The lens (EXIF LensModel).</summary>
    /// <example><code>string? lens = photo.Metadata?.LensModel;</code></example>
    public string? LensModel { get; }

    /// <summary>
    /// When the photo was taken, by the camera's clock (EXIF DateTimeOriginal, else DateTime; else IPTC Date Created
    /// and Time Created). Its <see cref="DateTime.Kind"/> is unspecified: see <see cref="DateTakenOffset"/> for its
    /// offset from UTC, when the file gives one.
    /// </summary>
    /// <example><code>DateTime? taken = photo.Metadata?.DateTaken;</code></example>
    public DateTime? DateTaken { get; }

    /// <summary>The offset from UTC of <see cref="DateTaken"/> (EXIF OffsetTimeOriginal, or IPTC's time zone), when known.</summary>
    /// <example>
    /// <code>
    /// if (photo.Metadata is { DateTaken: { } at, DateTakenOffset: { } offset })
    /// {
    ///     var exact = new DateTimeOffset(at, offset);
    /// }
    /// </code>
    /// </example>
    public TimeSpan? DateTakenOffset { get; }

    /// <summary>Where the photo was taken, from the EXIF GPS tags, when both latitude and longitude are there.</summary>
    /// <example><code>GeoLocation? where = photo.Metadata?.Location;</code></example>
    public GeoLocation? Location { get; }

    /// <summary>Who made it (EXIF Artist, else IPTC By-line).</summary>
    /// <example><code>string? artist = photo.Metadata?.Artist;</code></example>
    public string? Artist { get; }

    /// <summary>The copyright notice (EXIF Copyright, else IPTC Copyright Notice).</summary>
    /// <example><code>string? copyright = photo.Metadata?.Copyright;</code></example>
    public string? Copyright { get; }

    /// <summary>A description of the picture (EXIF ImageDescription, else IPTC Caption).</summary>
    /// <example><code>string? caption = photo.Metadata?.Description;</code></example>
    public string? Description { get; }

    /// <summary>A short title (IPTC Object Name).</summary>
    /// <example><code>string? title = photo.Metadata?.Title;</code></example>
    public string? Title { get; }

    /// <summary>Keywords (IPTC Keywords), in the file's order; empty when there are none.</summary>
    /// <example><code>bool tagged = photo.Metadata?.Keywords.Contains("beach") == true;</code></example>
    public IReadOnlyList<string> Keywords { get; } = [];

    /// <summary>
    /// The EXIF orientation as the file stored it, 1 to 8 (1 when there is none). The pixels may already be turned
    /// upright (<see cref="DecodeOptions.ApplyOrientation"/>, the default); this says only what the file said.
    /// </summary>
    /// <example><code>bool sideways = photo.Metadata?.Orientation is 5 or 6 or 7 or 8;</code></example>
    public int Orientation { get; } = 1;

    internal SourceMetadata Source { get; }

    private static string? First(List<string> texts) => texts.Count > 0 ? texts[0] : null;

    private static string? Joined(List<string> texts) => texts.Count > 0 ? string.Join("; ", texts) : null;

    // "YYYY:MM:DD HH:MM:SS", the EXIF date format, with an offset such as "+01:00" from the matching OffsetTime tag.
    private static (DateTime?, TimeSpan?) ExifDate(ExifReader.Tags exif)
    {
        (string? text, string? offset) = ExifReader.Tags.Text(exif.Exif, 0x9003) is { } original
            ? (original, ExifReader.Tags.Text(exif.Exif, 0x9011))
            : (ExifReader.Tags.Text(exif.Main, 0x0132), ExifReader.Tags.Text(exif.Exif, 0x9010));
        if (text is null || !DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
        {
            return (null, null);
        }

        return (date, Offset(offset));
    }

    // IPTC: "CCYYMMDD" and "HHMMSS±HHMM".
    private static (DateTime?, TimeSpan?) IptcDate(string? date, string? time)
    {
        if (date is null || !DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day))
        {
            return (null, null);
        }

        if (time is { Length: >= 6 } && TimeSpan.TryParseExact(time[..6], "hhmmss", CultureInfo.InvariantCulture, out TimeSpan clock))
        {
            TimeSpan? offset = time.Length == 11 ? Offset(time[6..9] + ":" + time[9..]) : null;
            return (day + clock, offset);
        }

        return (day, null);
    }

    private static TimeSpan? Offset(string? text)
    {
        if (text is not { Length: 6 } || text[0] is not ('+' or '-')
            || !TimeSpan.TryParseExact(text[1..], @"hh\:mm", CultureInfo.InvariantCulture, out TimeSpan offset)
            || offset > TimeSpan.FromHours(14))
        {
            return null;
        }

        return text[0] == '-' ? -offset : offset;
    }

    // GPS: degrees, minutes and seconds as three rationals, with N/S and E/W references; altitude below sea level when
    // its reference byte is 1.
    private static GeoLocation? ExifLocation(ExifReader.Tags exif)
    {
        double? latitude = Degrees(exif.Rationals(exif.Gps, 0x0002), ExifReader.Tags.Text(exif.Gps, 0x0001), "S");
        double? longitude = Degrees(exif.Rationals(exif.Gps, 0x0004), ExifReader.Tags.Text(exif.Gps, 0x0003), "W");
        if (latitude is not (>= -90 and <= 90) || longitude is not (>= -180 and <= 180))
        {
            return null;
        }

        double? altitude = exif.Rationals(exif.Gps, 0x0006) is [double metres, ..] ? metres : null;
        if (altitude is not null && exif.Number(exif.Gps, 0x0005) == 1)
        {
            altitude = -altitude;
        }

        return new GeoLocation(latitude.Value, longitude.Value, altitude);
    }

    private static double? Degrees(double[]? parts, string? reference, string negative)
    {
        if (parts is not [double degrees, double minutes, double seconds, ..] || reference is null)
        {
            return null;
        }

        double value = degrees + (minutes / 60) + (seconds / 3600);
        return reference.StartsWith(negative, StringComparison.OrdinalIgnoreCase) ? -value : value;
    }
}

/// <summary>A place on Earth, as GPS gives it: degrees north and east (negative for south and west), and metres above sea level.</summary>
/// <example><code>var oslo = new GeoLocation(59.91, 10.75, null);</code></example>
public sealed class GeoLocation
{
    /// <summary>Creates a location.</summary>
    /// <param name="latitude">Degrees north, -90 to 90.</param>
    /// <param name="longitude">Degrees east, -180 to 180.</param>
    /// <param name="altitude">Metres above sea level, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">A degree value is out of range.</exception>
    /// <example><code>var summit = new GeoLocation(27.988, 86.925, 8849);</code></example>
    public GeoLocation(double latitude, double longitude, double? altitude)
    {
        if (latitude is not (>= -90 and <= 90))
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), latitude, "Latitude is -90 to 90 degrees.");
        }

        if (longitude is not (>= -180 and <= 180))
        {
            throw new ArgumentOutOfRangeException(nameof(longitude), longitude, "Longitude is -180 to 180 degrees.");
        }

        Latitude = latitude;
        Longitude = longitude;
        Altitude = altitude;
    }

    /// <summary>Degrees north of the equator; negative is south.</summary>
    /// <example><code>bool north = where.Latitude &gt; 0;</code></example>
    public double Latitude { get; }

    /// <summary>Degrees east of Greenwich; negative is west.</summary>
    /// <example><code>bool east = where.Longitude &gt; 0;</code></example>
    public double Longitude { get; }

    /// <summary>Metres above sea level (negative below), when the file gives it.</summary>
    /// <example><code>double? height = where.Altitude;</code></example>
    public double? Altitude { get; }

    /// <summary>The location as decimal degrees, such as <c>59.908333, 10.75</c>.</summary>
    /// <returns>The text.</returns>
    /// <example><code>Console.WriteLine(where);</code></example>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Latitude:0.######}, {Longitude:0.######}");
}
