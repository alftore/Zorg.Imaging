using System;

namespace Zorg.Imaging;

/// <summary>Whether an <see cref="ImageCodec"/> can run on this machine, and if not, what's missing.</summary>
/// <example>
/// <code>
/// if (!codec.Availability.IsAvailable)
/// {
///     Console.WriteLine(codec.Availability.Reason);
/// }
/// </code>
/// </example>
public sealed class CodecAvailability
{
    private CodecAvailability(bool isAvailable, string? reason)
    {
        IsAvailable = isAvailable;
        Reason = reason;
    }

    /// <summary>The codec can run.</summary>
    /// <example><code>public override CodecAvailability Availability => CodecAvailability.Available;</code></example>
    public static CodecAvailability Available { get; } = new(isAvailable: true, reason: null);

    /// <summary>Whether the codec can run.</summary>
    /// <example><code>bool ok = codec.Availability.IsAvailable;</code></example>
    public bool IsAvailable { get; }

    /// <summary>What's missing, in a sentence a user can act on, or <see langword="null"/> when available.</summary>
    /// <example><code>string? why = codec.Availability.Reason;</code></example>
    public string? Reason { get; }

    /// <summary>The codec can't run here.</summary>
    /// <param name="reason">
    /// What's missing, as a sentence a user can act on: "HEIC decoding needs the HEIF Image Extension and HEVC Video
    /// Extensions from the Microsoft Store."
    /// </param>
    /// <returns>The availability.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is empty.</exception>
    /// <example><code>return CodecAvailability.Unavailable("AVIF decoding needs macOS 13 or later.");</code></example>
    public static CodecAvailability Unavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new CodecAvailability(isAvailable: false, reason);
    }

    /// <summary>"Available", or the reason.</summary>
    /// <returns>The text.</returns>
    /// <example><code>Console.WriteLine(codec.Availability);</code></example>
    public override string ToString() => Reason ?? "Available";
}
