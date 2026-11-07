using System;
using System.Diagnostics.CodeAnalysis;

namespace Zorg.Imaging.Formats;

/// <summary>
/// Thrown by header readers to stop at the first problem. Never escapes the library: <see cref="Identifier"/> turns it
/// into an <see cref="IdentifyResult"/>.
/// </summary>
[SuppressMessage("Design", "CA1064:Exceptions should be public", Justification = "Internal control flow; always caught by Identifier.")]
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Internal control flow; always constructed with a status.")]
internal sealed class IdentifyFailure : Exception
{
    private IdentifyFailure(IdentifyStatus status, string message)
        : base(message)
    {
        Status = status;
    }

    /// <summary>The status to report: <see cref="IdentifyStatus.Truncated"/>, <see cref="IdentifyStatus.Corrupt"/> or <see cref="IdentifyStatus.ExceedsLimits"/>.</summary>
    public IdentifyStatus Status { get; }

    /// <summary>The data ended before the headers did.</summary>
    /// <param name="message">Why, in English, safe to show to the uploader. Name the format: "The PNG file ends inside its IHDR chunk."</param>
    /// <returns>The exception to throw.</returns>
    public static IdentifyFailure Truncated(string message) => new(IdentifyStatus.Truncated, message);

    /// <summary>The headers break the format's rules.</summary>
    /// <param name="message">Why, in English, safe to show to the uploader.</param>
    /// <returns>The exception to throw.</returns>
    public static IdentifyFailure Corrupt(string message) => new(IdentifyStatus.Corrupt, message);

    /// <summary>A limit in <see cref="ImageLimits"/> was exceeded.</summary>
    /// <param name="message">Which limit and the values, in English, safe to show to the uploader.</param>
    /// <returns>The exception to throw.</returns>
    public static IdentifyFailure ExceedsLimits(string message) => new(IdentifyStatus.ExceedsLimits, message);
}
