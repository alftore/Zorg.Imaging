using System.Diagnostics.CodeAnalysis;

namespace Zorg.Imaging;

/// <summary>
/// The result of <see cref="Image.Identify"/> or <see cref="Image.IdentifyAsync"/>. Rejecting a file is a normal
/// outcome, so it is reported here rather than thrown.
/// </summary>
/// <example>
/// <code>
/// IdentifyResult result = await Image.IdentifyAsync(stream, cancellationToken: cancellationToken);
/// if (!result.Succeeded)
/// {
///     return Results.BadRequest(result.Message);
/// }
///
/// Console.WriteLine($"{result.Info.DisplayWidth} x {result.Info.DisplayHeight}");
/// </code>
/// </example>
public sealed class IdentifyResult
{
    private IdentifyResult(IdentifyStatus status, ImageFormat? format, ImageInfo? info, string? message)
    {
        Status = status;
        Format = format;
        Info = info;
        Message = message;
    }

    /// <summary>What happened.</summary>
    /// <example><code>bool tooLarge = result.Status == IdentifyStatus.ExceedsLimits;</code></example>
    public IdentifyStatus Status { get; }

    /// <summary>The format recognised from the signature, or <see langword="null"/> for <see cref="IdentifyStatus.NotAnImage"/>.</summary>
    /// <example><code>string name = result.Format?.Name ?? "unknown";</code></example>
    public ImageFormat? Format { get; }

    /// <summary>What the headers say about the image. Set only when <see cref="Succeeded"/> is <see langword="true"/>.</summary>
    /// <example><code>int width = result.Info?.Width ?? 0;</code></example>
    public ImageInfo? Info { get; }

    /// <summary>
    /// Why the file was rejected, in English, safe to show to the person who uploaded it. <see langword="null"/> when
    /// <see cref="Succeeded"/> is <see langword="true"/>.
    /// </summary>
    /// <example><code>return Results.BadRequest(result.Message);</code></example>
    public string? Message { get; }

    /// <summary>Whether the status is <see cref="IdentifyStatus.Ok"/>, in which case <see cref="Info"/> is set.</summary>
    /// <example>
    /// <code>
    /// if (result.Succeeded)
    /// {
    ///     Console.WriteLine(result.Info.Width);
    /// }
    /// </code>
    /// </example>
    [MemberNotNullWhen(true, nameof(Info))]
    public bool Succeeded => Status == IdentifyStatus.Ok;

    /// <summary>Returns the status, the format and the message or size, for logs.</summary>
    /// <returns>A one-line summary.</returns>
    /// <example><code>logger.LogInformation("Upload: {Result}", result);</code></example>
    public override string ToString() => Succeeded
        ? $"{Status}: {Info}"
        : $"{Status}: {Message}";

    internal static IdentifyResult Ok(ImageInfo info) => new(IdentifyStatus.Ok, info.Format, info, message: null);

    internal static IdentifyResult Failed(IdentifyStatus status, ImageFormat? format, string message) =>
        new(status, format, info: null, message);
}
