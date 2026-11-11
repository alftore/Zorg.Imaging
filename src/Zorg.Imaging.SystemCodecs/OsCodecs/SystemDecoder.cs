using System;
using Zorg.Imaging.OsCodecs.MacOS;
using Zorg.Imaging.OsCodecs.Windows;

namespace Zorg.Imaging.OsCodecs;

/// <summary>The operating system's decoder for HEIF files: WIC on Windows, ImageIO on macOS, none elsewhere.</summary>
internal abstract class SystemDecoder
{
    /// <summary>A short name for messages.</summary>
    public abstract string Name { get; }

    /// <summary>The decoder for this operating system.</summary>
    /// <returns>WIC, ImageIO, or one that is never available.</returns>
    public static SystemDecoder ForThisSystem() =>
        OperatingSystem.IsWindows() ? new WicDecoder()
        : OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() ? new ImageIODecoder()
        : new NoDecoder();

    /// <summary>Whether this system can decode the format, and if not, what's missing.</summary>
    public abstract CodecAvailability Availability(ImageFormat format);

    /// <summary>Decodes the primary image at its stored orientation.</summary>
    public abstract Image Decode(ReadOnlyMemory<byte> data, ImageInfo header, CodecContext context);

    /// <summary>The pixel format to produce: the one asked for, else 8-bit RGBA.</summary>
    protected static PixelFormat Target(CodecContext context) =>
        context.PixelFormat == PixelFormat.None ? PixelFormat.Rgba32 : context.PixelFormat;

    private sealed class NoDecoder : SystemDecoder
    {
        public override string Name => "no system decoder";

        public override CodecAvailability Availability(ImageFormat format) =>
            CodecAvailability.Unavailable("AVIF and HEIC decoding through the operating system works on Windows and macOS only.");

        public override Image Decode(ReadOnlyMemory<byte> data, ImageInfo header, CodecContext context) =>
            throw new PlatformNotSupportedException("AVIF and HEIC decoding through the operating system works on Windows and macOS only.");
    }
}
