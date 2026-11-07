namespace Zorg.Imaging;

/// <summary>The outcome of <see cref="Image.Identify"/> or <see cref="Image.IdentifyAsync"/>.</summary>
/// <example>
/// <code>
/// IdentifyResult result = Image.Identify(bytes);
/// int statusCode = result.Status switch
/// {
///     IdentifyStatus.Ok => 200,
///     IdentifyStatus.UnsupportedFormat => 415,
///     IdentifyStatus.ExceedsLimits => 413,
///     _ => 400,
/// };
/// </code>
/// </example>
public enum IdentifyStatus
{
    /// <summary>The default value. Never returned.</summary>
    None = 0,

    /// <summary>The headers were read and are within the limits. <see cref="IdentifyResult.Info"/> is set.</summary>
    Ok,

    /// <summary>The data doesn't start with the signature of any known image format.</summary>
    NotAnImage,

    /// <summary>The format was recognised but isn't one this library decodes. <see cref="IdentifyResult.Format"/> says which.</summary>
    UnsupportedFormat,

    /// <summary>The data ended before the headers did.</summary>
    Truncated,

    /// <summary>The headers break the format's rules.</summary>
    Corrupt,

    /// <summary>
    /// The image is larger than <see cref="ImageLimits"/> allows: too many pixels or frames, or headers longer than
    /// <see cref="ImageLimits.MaxHeaderBytes"/>.
    /// </summary>
    ExceedsLimits,
}
