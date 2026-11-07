namespace Zorg.Imaging.Formats;

/// <summary>Builds the exceptions that readers throw, and maps them to an <see cref="IdentifyStatus"/>.</summary>
/// <remarks>
/// Header readers and decoders share these, so a decoder fails with the same message that Identify reports.
/// <see cref="Identifier"/> turns them into an <see cref="IdentifyResult"/>.
/// </remarks>
internal static class ImageErrors
{
    /// <summary>The data ended early.</summary>
    /// <param name="message">Why, in English, safe to show to the uploader. Name the format: "The PNG file ends inside its IHDR chunk."</param>
    /// <returns>The exception to throw.</returns>
    public static ImageFormatException Truncated(string message) => new(message, truncated: true);

    /// <summary>The data breaks the format's rules.</summary>
    /// <param name="message">Why, in English, safe to show to the uploader.</param>
    /// <returns>The exception to throw.</returns>
    public static ImageFormatException Corrupt(string message) => new(message, truncated: false);

    /// <summary>A limit in <see cref="ImageLimits"/> was exceeded.</summary>
    /// <param name="message">Which limit and the values, in English, safe to show to the uploader.</param>
    /// <returns>The exception to throw.</returns>
    public static ImageLimitException ExceedsLimits(string message) => new(message);

    extension(ImageException exception)
    {
        /// <summary>The status that <see cref="Image.Identify"/> reports for this exception.</summary>
        public IdentifyStatus Status => exception switch
        {
            ImageLimitException => IdentifyStatus.ExceedsLimits,
            UnsupportedImageFormatException => IdentifyStatus.UnsupportedFormat,
            ImageFormatException { IsTruncated: true } => IdentifyStatus.Truncated,
            _ => IdentifyStatus.Corrupt,
        };
    }
}
