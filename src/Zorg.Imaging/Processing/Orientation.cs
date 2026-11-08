using System;
using System.Runtime.InteropServices;

namespace Zorg.Imaging.Processing;

/// <summary>Turns an image upright from its EXIF orientation (CIPA DC-008, tag 0x0112).</summary>
/// <remarks>
/// The orientation says how the stored pixels must be transformed for display: 2 mirrors left to right, 3 turns
/// 180°, 4 mirrors top to bottom, 5 transposes, 6 turns 90° clockwise, 7 transverses, 8 turns 90° anticlockwise.
/// 5 to 8 swap width and height.
/// </remarks>
internal static class Orientation
{
    /// <summary>Creates the upright copy of an image.</summary>
    /// <param name="source">The image as stored.</param>
    /// <param name="orientation">The EXIF orientation, 1 to 8.</param>
    /// <returns>A new image, which the caller disposes; a copy when <paramref name="orientation"/> is 1.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="orientation"/> isn't 1 to 8.</exception>
    public static Image Apply(Image source, int orientation)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(orientation, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(orientation, 8);

        bool swap = orientation >= 5;
        var result = new Image(swap ? source.Height : source.Width, swap ? source.Width : source.Height, source.PixelFormat)
        {
            ColorSpace = source.ColorSpace,

            // The pixels are turned, so an EXIF orientation no longer describes them.
            SourceMetadata = orientation == 1 ? source.SourceMetadata : source.SourceMetadata is { } metadata ? metadata with { OrientationApplied = true } : null,
        };
        switch (source.BytesPerPixel)
        {
            case 8:
                Transform<ulong>(source, result, orientation);
                break;
            case 6:
                Transform<Rgb48>(source, result, orientation);
                break;
            case 2:
                Transform<ushort>(source, result, orientation);
                break;
            case 4:
                Transform<uint>(source, result, orientation);
                break;
            case 3:
                Transform<Rgb24>(source, result, orientation);
                break;
            default:
                Transform<byte>(source, result, orientation);
                break;
        }

        return result;
    }

    private static void Transform<T>(Image source, Image result, int orientation)
        where T : unmanaged
    {
        int w = source.Width;
        int h = source.Height;
        ReadOnlySpan<T> src = MemoryMarshal.Cast<byte, T>(source.GetPixelBytes());
        for (int y = 0; y < result.Height; y++)
        {
            Span<T> row = MemoryMarshal.Cast<byte, T>(result.GetRowBytes(y));
            for (int x = 0; x < row.Length; x++)
            {
                (int sx, int sy) = orientation switch
                {
                    2 => (w - 1 - x, y),
                    3 => (w - 1 - x, h - 1 - y),
                    4 => (x, h - 1 - y),
                    5 => (y, x),
                    6 => (y, h - 1 - x),
                    7 => (w - 1 - y, h - 1 - x),
                    8 => (w - 1 - y, x),
                    _ => (x, y),
                };
                row[x] = src[(sy * w) + sx];
            }
        }
    }
}
