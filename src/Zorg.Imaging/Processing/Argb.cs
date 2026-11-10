using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Zorg.Imaging.Processing;

/// <summary>Pixels as ARGB words, alpha in the top byte, as the WebP and GIF codecs work on them.</summary>
internal static class Argb
{
    /// <summary>The pixels as ARGB words (alpha in the top byte), 16-bit samples rounded to 8, and whether any isn't opaque.</summary>
    public static (uint[] Argb, bool Alpha) FromImage(Image image)
    {
        int width = image.Width;
        uint[] argb = new uint[(long)width * image.Height];
        uint opaque = 0xFF;
        for (int y = 0; y < image.Height; y++)
        {
            ReadOnlySpan<byte> row = image.GetRowBytes(y);
            ReadOnlySpan<ushort> wide = image.PixelFormat is PixelFormat.L16 or PixelFormat.Rgb48 or PixelFormat.Rgba64
                ? MemoryMarshal.Cast<byte, ushort>(row)
                : default;
            Span<uint> output = argb.AsSpan(y * width, width);
            for (int x = 0; x < width; x++)
            {
                (uint r, uint g, uint b, uint a) = image.PixelFormat switch
                {
                    PixelFormat.L8 => (row[x], row[x], row[x], 255u),
                    PixelFormat.L16 => (To8(wide[x]), To8(wide[x]), To8(wide[x]), 255u),
                    PixelFormat.Rgb24 => (row[3 * x], row[(3 * x) + 1], row[(3 * x) + 2], 255u),
                    PixelFormat.Rgb48 => (To8(wide[3 * x]), To8(wide[(3 * x) + 1]), To8(wide[(3 * x) + 2]), 255u),
                    PixelFormat.Rgba32 => (row[4 * x], row[(4 * x) + 1], row[(4 * x) + 2], row[(4 * x) + 3]),
                    PixelFormat.Bgra32 => (row[(4 * x) + 2], row[(4 * x) + 1], row[4 * x], row[(4 * x) + 3]),
                    _ => (To8(wide[4 * x]), To8(wide[(4 * x) + 1]), To8(wide[(4 * x) + 2]), To8(wide[(4 * x) + 3])),
                };
                opaque &= a;
                output[x] = (a << 24) | (r << 16) | (g << 8) | b;
            }
        }

        return (argb, opaque != 0xFF);
    }

    private static uint To8(ushort value) => (uint)((value + 128) / 257);

    /// <summary>ARGB words as an image in <paramref name="format"/>: Rgb24, Rgba32, Bgra32 or Rgba64.</summary>
    public static Image ToImage(uint[] argb, int width, int height, bool hasAlpha, PixelFormat format)
    {
        var image = new Image(width, height, format);
        for (int y = 0; y < height; y++)
        {
            WriteRow(argb.AsSpan(y * width, width), image.GetRowBytes(y), format, hasAlpha);
        }

        return image;
    }

    /// <summary>
    /// Draws <paramref name="source"/> over <paramref name="target"/>, both ARGB with straight (not premultiplied) alpha:
    /// the "over" operator on 8-bit values, rounded to nearest.
    /// </summary>
    public static void BlendOver(ReadOnlySpan<uint> source, Span<uint> target)
    {
        for (int i = 0; i < source.Length; i++)
        {
            uint src = source[i];
            uint sa = src >> 24;
            if (sa == 255)
            {
                target[i] = src;
                continue;
            }

            if (sa == 0)
            {
                continue;
            }

            uint dst = target[i];
            uint behind = (((dst >> 24) * (255 - sa)) + 127) / 255;
            uint a = sa + behind;
            uint Channel(int shift) => ((((src >> shift) & 0xFF) * sa) + (((dst >> shift) & 0xFF) * behind) + (a / 2)) / a;
            target[i] = (a << 24) | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
        }
    }

    /// <summary>One row of ARGB words into an image row in <paramref name="format"/>: Rgb24, Rgba32, Bgra32 or Rgba64.</summary>
    public static void WriteRow(ReadOnlySpan<uint> row, Span<byte> output, PixelFormat format, bool hasAlpha) =>
        WriteRow(row, output, format, hasAlpha, useVectors: true);

    /// <summary><see cref="WriteRow(ReadOnlySpan{uint}, Span{byte}, PixelFormat, bool)"/>, optionally without vectors: the reference the vector path is tested against.</summary>
    internal static void WriteRow(ReadOnlySpan<uint> row, Span<byte> output, PixelFormat format, bool hasAlpha, bool useVectors)
    {
        // Four pixels at a time with a byte shuffle (ARGB words are B, G, R, A in memory), then the rest one by one.
        int done = useVectors && Ssse3.IsSupported && format != PixelFormat.Rgba64 ? WriteVectors(row, output, format, hasAlpha) : 0;
        row = row[done..];
        output = output[(done * (format == PixelFormat.Rgb24 ? 3 : 4))..];
        switch (format)
        {
            case PixelFormat.Rgb24:
                for (int x = 0; x < row.Length; x++)
                {
                    uint p = row[x];
                    output[3 * x] = (byte)(p >> 16);
                    output[(3 * x) + 1] = (byte)(p >> 8);
                    output[(3 * x) + 2] = (byte)p;
                }

                break;

            case PixelFormat.Rgba32:
                for (int x = 0; x < row.Length; x++)
                {
                    uint p = hasAlpha ? row[x] : row[x] | 0xFF000000u;
                    output[4 * x] = (byte)(p >> 16);
                    output[(4 * x) + 1] = (byte)(p >> 8);
                    output[(4 * x) + 2] = (byte)p;
                    output[(4 * x) + 3] = (byte)(p >> 24);
                }

                break;

            case PixelFormat.Bgra32:
                // ARGB in a little-endian word is B, G, R, A in memory: the layout itself.
                for (int x = 0; x < row.Length; x++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(output[(4 * x)..], hasAlpha ? row[x] : row[x] | 0xFF000000u);
                }

                break;

            default:
                Span<ushort> wide = MemoryMarshal.Cast<byte, ushort>(output);
                for (int x = 0; x < row.Length; x++)
                {
                    uint p = hasAlpha ? row[x] : row[x] | 0xFF000000u;
                    wide[4 * x] = (ushort)(((p >> 16) & 0xFF) * 257);
                    wide[(4 * x) + 1] = (ushort)(((p >> 8) & 0xFF) * 257);
                    wide[(4 * x) + 2] = (ushort)((p & 0xFF) * 257);
                    wide[(4 * x) + 3] = (ushort)((p >> 24) * 257);
                }

                break;
        }
    }

    // Rgb24, Rgba32 or Bgra32, four pixels per shuffle; returns how many pixels were done. A 12-byte Rgb24 group is
    // stored as 16 bytes, so it stops while the next group's 4 spare bytes still fit in the row.
    private static int WriteVectors(ReadOnlySpan<uint> row, Span<byte> output, PixelFormat format, bool hasAlpha)
    {
        ref byte source = ref Unsafe.As<uint, byte>(ref MemoryMarshal.GetReference(row));
        ref byte target = ref MemoryMarshal.GetReference(output);
        Vector128<byte> opaque = hasAlpha ? Vector128<byte>.Zero : Vector128.Create(0xFF000000u).AsByte();
        int x = 0;
        switch (format)
        {
            case PixelFormat.Rgb24:
            {
                Vector128<byte> mask = Vector128.Create((byte)2, 1, 0, 6, 5, 4, 10, 9, 8, 14, 13, 12, 0x80, 0x80, 0x80, 0x80);
                for (; x + 4 <= row.Length && (3 * x) + 16 <= output.Length; x += 4)
                {
                    Ssse3.Shuffle(Vector128.LoadUnsafe(ref source, (nuint)(4 * x)), mask).StoreUnsafe(ref target, (nuint)(3 * x));
                }

                break;
            }

            case PixelFormat.Rgba32:
            {
                Vector128<byte> mask = Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
                for (; x + 4 <= row.Length && (4 * x) + 16 <= output.Length; x += 4)
                {
                    (Ssse3.Shuffle(Vector128.LoadUnsafe(ref source, (nuint)(4 * x)), mask) | opaque).StoreUnsafe(ref target, (nuint)(4 * x));
                }

                break;
            }

            case PixelFormat.Bgra32:
                for (; x + 4 <= row.Length && (4 * x) + 16 <= output.Length; x += 4)
                {
                    (Vector128.LoadUnsafe(ref source, (nuint)(4 * x)) | opaque).StoreUnsafe(ref target, (nuint)(4 * x));
                }

                break;
        }

        return x;
    }
}
