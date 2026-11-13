using System;
using System.Globalization;
using Zorg.Imaging.OsCodecs.Heif;
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

    /// <summary>Decodes the primary image as shown: cropped, turned and mirrored as the file says.</summary>
    /// <param name="data">The whole file.</param>
    /// <param name="header">The file's boxes.</param>
    /// <param name="context">The pixel format and limits.</param>
    /// <param name="scaledTo">A smaller size as shown to decode at, or null for full size. A decoder may ignore it.</param>
    public abstract Image Decode(ReadOnlyMemory<byte> data, HeifHeader header, CodecContext context, (int Width, int Height)? scaledTo);

    /// <summary>The pixel format to produce: the one asked for, else 8-bit RGBA.</summary>
    protected static PixelFormat Target(CodecContext context) =>
        context.PixelFormat == PixelFormat.None ? PixelFormat.Rgba32 : context.PixelFormat;

    /// <summary>
    /// The decoded image as the file says to show it. A system decoder may have applied the crop, turn and mirror
    /// already (WIC does), or returned the image as stored; the size, and what the system says it did, tell which.
    /// </summary>
    /// <param name="decoded">What the system decoded. Disposed if a new image is returned.</param>
    /// <param name="header">The file's geometry.</param>
    /// <param name="systemApplied">Whether the system says it applied the transforms.</param>
    /// <returns>The image as shown.</returns>
    /// <exception cref="ImageFormatException">The decoded size fits neither the stored nor the shown size.</exception>
    protected static Image AsShown(Image decoded, HeifHeader header, bool systemApplied)
    {
        bool shown = decoded.Width == header.DisplayWidth && decoded.Height == header.DisplayHeight;
        bool stored = decoded.Width == header.Width && decoded.Height == header.Height;
        Crop crop = header.Crop;
        bool cropped = crop.Width != header.Width || crop.Height != header.Height;

        // ImageIO crops but leaves the turn and mirror to the caller: the size is the cropped one.
        bool croppedOnly = cropped && decoded.Width == crop.Width && decoded.Height == crop.Height;
        if (shown && (systemApplied || !header.IsTransformed))
        {
            return decoded;
        }

        if (!stored && !croppedOnly)
        {
            if (shown)
            {
                return decoded;
            }

            decoded.Dispose();
            throw new ImageFormatException(string.Create(
                CultureInfo.InvariantCulture,
                $"The {header.Format.Name} file's image decoded at {decoded.Width}x{decoded.Height} pixels, but its boxes say {header.Width}x{header.Height}, shown as {header.DisplayWidth}x{header.DisplayHeight}."));
        }

        Image current = decoded;
        try
        {
            if (cropped && !croppedOnly)
            {
                current = Replace(current, current.Crop(crop.X, crop.Y, crop.Width, crop.Height));
            }

            return Turn(current, header);
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    /// <summary>Turns and mirrors a cropped picture as the file says (<c>irot</c>, then <c>imir</c>).</summary>
    /// <param name="cropped">The picture, cropped but not turned. Disposed if a new image is returned.</param>
    /// <param name="header">The file's geometry.</param>
    /// <returns>The picture as shown.</returns>
    protected static Image Turn(Image cropped, HeifHeader header)
    {
        Image current = cropped;
        try
        {
            if (header.Rotation != 0)
            {
                current = Replace(current, current.Rotate(-90 * header.Rotation));
            }

            if (header.Mirror == 0)
            {
                current = Replace(current, current.FlipHorizontal());
            }
            else if (header.Mirror == 1)
            {
                current = Replace(current, current.FlipVertical());
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static Image Replace(Image old, Image next)
    {
        next.ColorSpace = old.ColorSpace;
        old.Dispose();
        return next;
    }

    private sealed class NoDecoder : SystemDecoder
    {
        public override string Name => "no system decoder";

        public override CodecAvailability Availability(ImageFormat format) =>
            CodecAvailability.Unavailable("AVIF and HEIC decoding through the operating system works on Windows and macOS only.");

        public override Image Decode(ReadOnlyMemory<byte> data, HeifHeader header, CodecContext context, (int Width, int Height)? scaledTo) =>
            throw new PlatformNotSupportedException("AVIF and HEIC decoding through the operating system works on Windows and macOS only.");
    }
}
