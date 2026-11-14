using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Zorg.Imaging.Drawing;

/// <summary>One pixel of any layout as straight RGBA floats from 0 to 1, in the stored values; grey reads as equal R, G and B.</summary>
internal static class Pixels
{
    public static Vector4 Read(PixelFormat format, ReadOnlySpan<byte> row, int at)
    {
        const float Byte = 1f / 255, Word = 1f / 65535;
        ReadOnlySpan<ushort> wide = format is PixelFormat.L16 or PixelFormat.Rgb48 or PixelFormat.Rgba64
            ? MemoryMarshal.Cast<byte, ushort>(row.Slice(at, Image.GetBytesPerPixel(format)))
            : default;
        return format switch
        {
            PixelFormat.L8 => new Vector4(row[at] * Byte, row[at] * Byte, row[at] * Byte, 1),
            PixelFormat.L16 => new Vector4(wide[0] * Word, wide[0] * Word, wide[0] * Word, 1),
            PixelFormat.Rgb24 => new Vector4(row[at], row[at + 1], row[at + 2], 255) * Byte,
            PixelFormat.Rgba32 => new Vector4(row[at], row[at + 1], row[at + 2], row[at + 3]) * Byte,
            PixelFormat.Bgra32 => new Vector4(row[at + 2], row[at + 1], row[at], row[at + 3]) * Byte,
            PixelFormat.Rgb48 => new Vector4(wide[0], wide[1], wide[2], 65535) * Word,
            _ => new Vector4(wide[0], wide[1], wide[2], wide[3]) * Word,
        };
    }

    public static void Write(PixelFormat format, Span<byte> row, int at, Vector4 p)
    {
        Span<ushort> wide = format is PixelFormat.L16 or PixelFormat.Rgb48 or PixelFormat.Rgba64
            ? MemoryMarshal.Cast<byte, ushort>(row.Slice(at, Image.GetBytesPerPixel(format)))
            : default;
        switch (format)
        {
            case PixelFormat.L8:
                row[at] = To8(p.X);
                break;
            case PixelFormat.L16:
                wide[0] = To16(p.X);
                break;
            case PixelFormat.Rgb24:
                (row[at], row[at + 1], row[at + 2]) = (To8(p.X), To8(p.Y), To8(p.Z));
                break;
            case PixelFormat.Rgba32:
                (row[at], row[at + 1], row[at + 2], row[at + 3]) = (To8(p.X), To8(p.Y), To8(p.Z), To8(p.W));
                break;
            case PixelFormat.Bgra32:
                (row[at + 2], row[at + 1], row[at], row[at + 3]) = (To8(p.X), To8(p.Y), To8(p.Z), To8(p.W));
                break;
            case PixelFormat.Rgb48:
                (wide[0], wide[1], wide[2]) = (To16(p.X), To16(p.Y), To16(p.Z));
                break;
            default:
                (wide[0], wide[1], wide[2], wide[3]) = (To16(p.X), To16(p.Y), To16(p.Z), To16(p.W));
                break;
        }
    }

    private static byte To8(float v) => (byte)Math.Clamp((v * 255) + 0.5f, 0, 255);

    private static ushort To16(float v) => (ushort)Math.Clamp((v * 65535) + 0.5f, 0, 65535);
}
