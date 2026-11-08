using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Zorg.Imaging.Processing;

/// <summary>
/// Image rows as straight (not premultiplied) RGBA floats from 0 to 1, in the stored values, for the effects that work
/// pixel by pixel. Grey reads as equal R, G and B; a format without alpha reads as opaque.
/// </summary>
internal static class FloatRows
{
    public static void Read(Image image, int y, Span<Vector4> row)
    {
        ReadOnlySpan<byte> bytes = image.GetRowBytes(y);
        ReadOnlySpan<ushort> wide = MemoryMarshal.Cast<byte, ushort>(bytes);
        const float Byte = 1f / 255;
        const float Word = 1f / 65535;
        for (int x = 0; x < image.Width; x++)
        {
            row[x] = image.PixelFormat switch
            {
                PixelFormat.L8 => new Vector4(bytes[x] * Byte, bytes[x] * Byte, bytes[x] * Byte, 1),
                PixelFormat.L16 => new Vector4(wide[x] * Word, wide[x] * Word, wide[x] * Word, 1),
                PixelFormat.Rgb24 => new Vector4(bytes[3 * x], bytes[(3 * x) + 1], bytes[(3 * x) + 2], 255) * Byte,
                PixelFormat.Rgba32 => new Vector4(bytes[4 * x], bytes[(4 * x) + 1], bytes[(4 * x) + 2], bytes[(4 * x) + 3]) * Byte,
                PixelFormat.Bgra32 => new Vector4(bytes[(4 * x) + 2], bytes[(4 * x) + 1], bytes[4 * x], bytes[(4 * x) + 3]) * Byte,
                PixelFormat.Rgb48 => new Vector4(wide[3 * x], wide[(3 * x) + 1], wide[(3 * x) + 2], 65535) * Word,
                _ => new Vector4(wide[4 * x], wide[(4 * x) + 1], wide[(4 * x) + 2], wide[(4 * x) + 3]) * Word,
            };
        }
    }

    /// <summary>Writes a row, clamped and rounded; grey formats take Rec. 709 luma, formats without alpha drop it.</summary>
    public static void Write(Image image, int y, ReadOnlySpan<Vector4> row)
    {
        Span<byte> bytes = image.GetRowBytes(y);
        Span<ushort> wide = MemoryMarshal.Cast<byte, ushort>(bytes);
        for (int x = 0; x < image.Width; x++)
        {
            Vector4 p = Vector4.Clamp(row[x], Vector4.Zero, Vector4.One);
            float luma = (0.2126f * p.X) + (0.7152f * p.Y) + (0.0722f * p.Z);
            switch (image.PixelFormat)
            {
                case PixelFormat.L8:
                    bytes[x] = To8(luma);
                    break;
                case PixelFormat.L16:
                    wide[x] = To16(luma);
                    break;
                case PixelFormat.Rgb24:
                    (bytes[3 * x], bytes[(3 * x) + 1], bytes[(3 * x) + 2]) = (To8(p.X), To8(p.Y), To8(p.Z));
                    break;
                case PixelFormat.Rgba32:
                    (bytes[4 * x], bytes[(4 * x) + 1], bytes[(4 * x) + 2], bytes[(4 * x) + 3]) = (To8(p.X), To8(p.Y), To8(p.Z), To8(p.W));
                    break;
                case PixelFormat.Bgra32:
                    (bytes[(4 * x) + 2], bytes[(4 * x) + 1], bytes[4 * x], bytes[(4 * x) + 3]) = (To8(p.X), To8(p.Y), To8(p.Z), To8(p.W));
                    break;
                case PixelFormat.Rgb48:
                    (wide[3 * x], wide[(3 * x) + 1], wide[(3 * x) + 2]) = (To16(p.X), To16(p.Y), To16(p.Z));
                    break;
                default:
                    (wide[4 * x], wide[(4 * x) + 1], wide[(4 * x) + 2], wide[(4 * x) + 3]) = (To16(p.X), To16(p.Y), To16(p.Z), To16(p.W));
                    break;
            }
        }
    }

    public static bool HasAlpha(PixelFormat format) => format is PixelFormat.Rgba32 or PixelFormat.Bgra32 or PixelFormat.Rgba64;

    public static bool IsGrey(PixelFormat format) => format is PixelFormat.L8 or PixelFormat.L16;

    public static bool IsSixteen(PixelFormat format) => format is PixelFormat.L16 or PixelFormat.Rgb48 or PixelFormat.Rgba64;

    private static byte To8(float v) => (byte)((v * 255) + 0.5f);

    private static ushort To16(float v) => (ushort)((v * 65535) + 0.5f);
}
