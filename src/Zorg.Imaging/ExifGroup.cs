using System;

namespace Zorg.Imaging;

/// <summary>Groups of EXIF tags that <see cref="MetadataOptions.Without"/> can leave out.</summary>
/// <example><code>MetadataOptions options = MetadataOptions.Keep.Without(ExifGroup.Location | ExifGroup.Camera);</code></example>
[Flags]
public enum ExifGroup
{
    /// <summary>No group.</summary>
    None = 0,

    /// <summary>Where the photo was taken: the whole GPS block.</summary>
    Location = 1,

    /// <summary>
    /// What took it and whose it is: make, model, lens, serial numbers and the camera owner's name, which can identify a
    /// person's camera across photos.
    /// </summary>
    Camera = 2,

    /// <summary>When: the date and time tags, their sub-seconds and time zone offsets.</summary>
    Dates = 4,
}
