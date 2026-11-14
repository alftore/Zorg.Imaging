using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Zorg.Imaging.Processing;

/// <summary>
/// <see cref="Image.Convert"/> and <see cref="Image.IsOpaque"/>: pixels from one layout to another, through 16-bit RGBA
/// for the general case, with vector paths for the common 8-bit pairs.
/// </summary>
internal static class PixelConverter
{
    /// <summary>Words scanned between checks, so a translucent image stops early.</summary>
    private const int ScanChunk = 4096;

    public static bool IsOpaque(Image image) => image.PixelFormat switch
    {
        PixelFormat.Rgba32 or PixelFormat.Bgra32 => AllOpaque32(MemoryMarshal.Cast<byte, uint>(image.GetPixelBytes())),
        PixelFormat.Rgba64 => AllOpaque64(MemoryMarshal.Cast<byte, ulong>(image.GetPixelBytes())),
        _ => KeepsAlive(image),
    };

    public static Image Convert(Image source, PixelFormat format)
    {
        if (format is < PixelFormat.Rgba32 or > PixelFormat.Rgba64)
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "The format must be one of the pixel layouts, not None.");
        }

        if (format == source.PixelFormat)
        {
            return source.Clone();
        }

        // RGBA and BGRA to RGB check alpha in the same pass; every other drop of alpha checks first.
        bool fused = format == PixelFormat.Rgb24 && source.PixelFormat is PixelFormat.Rgba32 or PixelFormat.Bgra32;
        if (!fused && HasAlpha(source.PixelFormat) && !HasAlpha(format) && !IsOpaque(source))
        {
            throw NotOpaque(format);
        }

        ReadOnlySpan<byte> from = source.GetPixelBytes(); // a disposed source throws before anything is rented
        var result = new Image(source.Width, source.Height, format) { ColorSpace = source.ColorSpace, SourceMetadata = source.SourceMetadata };
        Span<byte> to = result.GetPixelBytes();
        switch ((source.PixelFormat, format))
        {
            case (PixelFormat.Rgba32 or PixelFormat.Bgra32, PixelFormat.Rgb24):
                if (!FourToThree(from, to, swap: source.PixelFormat == PixelFormat.Bgra32))
                {
                    result.Dispose();
                    throw NotOpaque(format);
                }

                break;
            case (PixelFormat.Rgb24, PixelFormat.Rgba32):
                ThreeToFour(from, to, swap: false);
                break;
            case (PixelFormat.Rgb24, PixelFormat.Bgra32):
                ThreeToFour(from, to, swap: true);
                break;
            case (PixelFormat.Rgba32, PixelFormat.Bgra32) or (PixelFormat.Bgra32, PixelFormat.Rgba32):
                SwapRedBlue(from, to);
                break;
            default:
                General(source, result);
                break;
        }

        return result;
    }

    private static InvalidOperationException NotOpaque(PixelFormat format) =>
        new($"{format} has no alpha and the image has pixels that aren't opaque. Flatten draws it over a colour instead.");

    internal static bool HasAlpha(PixelFormat format) => format is PixelFormat.Rgba32 or PixelFormat.Bgra32 or PixelFormat.Rgba64;

    /// <summary>One 16-bit grey value from 16-bit RGB: Rec. 709 luma of the stored values, weights summing to 65536.</summary>
    internal static ushort Luma(uint r, uint g, uint b) => (ushort)(((13933 * r) + (46871 * g) + (4732 * b) + 32768) >> 16);

    private static bool KeepsAlive(Image image)
    {
        _ = image.GetPixelBytes(); // throws when disposed, like the formats that are read
        return true;
    }

    private static bool AllOpaque32(ReadOnlySpan<uint> words)
    {
        // AND the words together; the alpha byte (the top one in memory order R, G, B, A or B, G, R, A) stays 255 only
        // when every pixel's does.
        int i = 0;
        while (i < words.Length)
        {
            int end = Math.Min(words.Length, i + ScanChunk);
            uint all = uint.MaxValue;
            if (Vector256.IsHardwareAccelerated)
            {
                Vector256<uint> lanes = Vector256<uint>.AllBitsSet;
                for (; i + Vector256<uint>.Count <= end; i += Vector256<uint>.Count)
                {
                    lanes &= Vector256.Create(words.Slice(i, Vector256<uint>.Count));
                }

                for (int lane = 0; lane < Vector256<uint>.Count; lane++)
                {
                    all &= lanes.GetElement(lane);
                }
            }

            for (; i < end; i++)
            {
                all &= words[i];
            }

            if ((all >> 24) != 0xFF)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AllOpaque64(ReadOnlySpan<ulong> words)
    {
        int i = 0;
        while (i < words.Length)
        {
            int end = Math.Min(words.Length, i + ScanChunk);
            ulong all = ulong.MaxValue;
            if (Vector256.IsHardwareAccelerated)
            {
                Vector256<ulong> lanes = Vector256<ulong>.AllBitsSet;
                for (; i + Vector256<ulong>.Count <= end; i += Vector256<ulong>.Count)
                {
                    lanes &= Vector256.Create(words.Slice(i, Vector256<ulong>.Count));
                }

                for (int lane = 0; lane < Vector256<ulong>.Count; lane++)
                {
                    all &= lanes.GetElement(lane);
                }
            }

            for (; i < end; i++)
            {
                all &= words[i];
            }

            if ((all >> 48) != 0xFFFF)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>RGBA or BGRA to RGB: the alpha byte dropped, red and blue swapped for BGRA.</summary>
    /// <returns>Whether every alpha byte was 255.</returns>
    internal static bool FourToThree(ReadOnlySpan<byte> from, Span<byte> to, bool swap)
    {
        int count = from.Length / 4;
        if (to.Length < count * 3)
        {
            throw new ArgumentException("The output is too short.", nameof(to));
        }

        int x = 0;
        byte alpha = 255;
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<byte> all = Vector128<byte>.AllBitsSet;
            Vector128<byte> shuffle = swap
                ? Vector128.Create((byte)2, 1, 0, 6, 5, 4, 10, 9, 8, 14, 13, 12, 0x80, 0x80, 0x80, 0x80)
                : Vector128.Create((byte)0, 1, 2, 4, 5, 6, 8, 9, 10, 12, 13, 14, 0x80, 0x80, 0x80, 0x80);

            // Each step writes 16 bytes for 12, so stop while the 4 spare ones still land inside the output.
            // The loop bounds keep every 16-byte load and store inside the spans, checked once here.
            ref byte source = ref MemoryMarshal.GetReference(from);
            ref byte target = ref MemoryMarshal.GetReference(to);
            for (; x + 6 <= count; x += 4)
            {
                Vector128<byte> pixels = Vector128.LoadUnsafe(ref source, (nuint)(x * 4));
                all &= pixels;
                Vector128.Shuffle(pixels, shuffle).StoreUnsafe(ref target, (nuint)(x * 3));
            }

            alpha = (byte)(all.GetElement(3) & all.GetElement(7) & all.GetElement(11) & all.GetElement(15));
        }

        int r = swap ? 2 : 0, b = swap ? 0 : 2;
        for (int i = x * 4, o = x * 3; x < count; x++, i += 4, o += 3)
        {
            to[o] = from[i + r];
            to[o + 1] = from[i + 1];
            to[o + 2] = from[i + b];
            alpha &= from[i + 3];
        }

        return alpha == 255;
    }

    /// <summary>RGB to RGBA or BGRA with alpha 255.</summary>
    internal static void ThreeToFour(ReadOnlySpan<byte> from, Span<byte> to, bool swap)
    {
        int count = from.Length / 3;
        if (to.Length < count * 4)
        {
            throw new ArgumentException("The output is too short.", nameof(to));
        }

        int x = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<byte> shuffle = swap
                ? Vector128.Create((byte)2, 1, 0, 0x80, 5, 4, 3, 0x80, 8, 7, 6, 0x80, 11, 10, 9, 0x80)
                : Vector128.Create((byte)0, 1, 2, 0x80, 3, 4, 5, 0x80, 6, 7, 8, 0x80, 9, 10, 11, 0x80);
            Vector128<byte> alpha = Vector128.Create(0xFF000000u).AsByte();

            // Each step reads 16 bytes for 12, so stop while the 4 spare ones are still inside the input.
            ref byte source = ref MemoryMarshal.GetReference(from);
            ref byte target = ref MemoryMarshal.GetReference(to);
            for (; x + 6 <= count; x += 4)
            {
                (Vector128.Shuffle(Vector128.LoadUnsafe(ref source, (nuint)(x * 3)), shuffle) | alpha).StoreUnsafe(ref target, (nuint)(x * 4));
            }
        }

        int r = swap ? 2 : 0, b = swap ? 0 : 2;
        for (int i = x * 3, o = x * 4; x < count; x++, i += 3, o += 4)
        {
            to[o + r] = from[i];
            to[o + 1] = from[i + 1];
            to[o + b] = from[i + 2];
            to[o + 3] = 255;
        }
    }

    /// <summary>RGBA to BGRA or back.</summary>
    internal static void SwapRedBlue(ReadOnlySpan<byte> from, Span<byte> to)
    {
        int count = from.Length / 4;
        if (to.Length < count * 4)
        {
            throw new ArgumentException("The output is too short.", nameof(to));
        }

        int x = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<byte> shuffle = Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
            ref byte source = ref MemoryMarshal.GetReference(from);
            ref byte target = ref MemoryMarshal.GetReference(to);
            for (; x + 4 <= count; x += 4)
            {
                Vector128.Shuffle(Vector128.LoadUnsafe(ref source, (nuint)(x * 4)), shuffle).StoreUnsafe(ref target, (nuint)(x * 4));
            }
        }

        for (int i = x * 4; x < count; x++, i += 4)
        {
            to[i] = from[i + 2];
            to[i + 1] = from[i + 1];
            to[i + 2] = from[i];
            to[i + 3] = from[i + 3];
        }
    }

    /// <summary>Any pair, one row at a time through 16-bit RGBA, in bands across cores.</summary>
    internal static void General(Image source, Image result)
    {
        int width = source.Width, height = source.Height;
        int bands = Parallelism.Bands((long)width * height, height);
        Parallelism.For(bands, band =>
        {
            int top = (int)((long)height * band / bands), bottom = (int)((long)height * (band + 1) / bands);
            ushort[] rgba = ArrayPool<ushort>.Shared.Rent(width * 4);
            try
            {
                Span<ushort> row = rgba.AsSpan(0, width * 4);
                for (int y = top; y < bottom; y++)
                {
                    Read(source.GetRowBytes(y), source.PixelFormat, row);
                    Write(row, result.GetRowBytes(y), result.PixelFormat);
                }
            }
            finally
            {
                ArrayPool<ushort>.Shared.Return(rgba);
            }
        });
    }

    private static void Read(ReadOnlySpan<byte> from, PixelFormat format, Span<ushort> rgba)
    {
        int count = rgba.Length / 4;
        switch (format)
        {
            case PixelFormat.L8:
                for (int x = 0; x < count; x++)
                {
                    ushort v = (ushort)(from[x] * 257);
                    rgba[x * 4] = v;
                    rgba[(x * 4) + 1] = v;
                    rgba[(x * 4) + 2] = v;
                    rgba[(x * 4) + 3] = ushort.MaxValue;
                }

                break;
            case PixelFormat.L16:
                ReadOnlySpan<ushort> grey = MemoryMarshal.Cast<byte, ushort>(from);
                for (int x = 0; x < count; x++)
                {
                    rgba[x * 4] = grey[x];
                    rgba[(x * 4) + 1] = grey[x];
                    rgba[(x * 4) + 2] = grey[x];
                    rgba[(x * 4) + 3] = ushort.MaxValue;
                }

                break;
            case PixelFormat.Rgb24:
                for (int x = 0; x < count; x++)
                {
                    rgba[x * 4] = (ushort)(from[x * 3] * 257);
                    rgba[(x * 4) + 1] = (ushort)(from[(x * 3) + 1] * 257);
                    rgba[(x * 4) + 2] = (ushort)(from[(x * 3) + 2] * 257);
                    rgba[(x * 4) + 3] = ushort.MaxValue;
                }

                break;
            case PixelFormat.Rgba32:
            case PixelFormat.Bgra32:
                int r = format == PixelFormat.Bgra32 ? 2 : 0, b = 2 - r;
                for (int x = 0; x < count; x++)
                {
                    rgba[x * 4] = (ushort)(from[(x * 4) + r] * 257);
                    rgba[(x * 4) + 1] = (ushort)(from[(x * 4) + 1] * 257);
                    rgba[(x * 4) + 2] = (ushort)(from[(x * 4) + b] * 257);
                    rgba[(x * 4) + 3] = (ushort)(from[(x * 4) + 3] * 257);
                }

                break;
            case PixelFormat.Rgb48:
                ReadOnlySpan<ushort> rgb = MemoryMarshal.Cast<byte, ushort>(from);
                for (int x = 0; x < count; x++)
                {
                    rgba[x * 4] = rgb[x * 3];
                    rgba[(x * 4) + 1] = rgb[(x * 3) + 1];
                    rgba[(x * 4) + 2] = rgb[(x * 3) + 2];
                    rgba[(x * 4) + 3] = ushort.MaxValue;
                }

                break;
            default: // Rgba64
                MemoryMarshal.Cast<byte, ushort>(from).CopyTo(rgba);
                break;
        }
    }

    private static void Write(ReadOnlySpan<ushort> rgba, Span<byte> to, PixelFormat format)
    {
        int count = rgba.Length / 4;
        switch (format)
        {
            case PixelFormat.L8:
                for (int x = 0; x < count; x++)
                {
                    to[x] = To8(Luma(rgba[x * 4], rgba[(x * 4) + 1], rgba[(x * 4) + 2]));
                }

                break;
            case PixelFormat.L16:
                Span<ushort> grey = MemoryMarshal.Cast<byte, ushort>(to);
                for (int x = 0; x < count; x++)
                {
                    grey[x] = Luma(rgba[x * 4], rgba[(x * 4) + 1], rgba[(x * 4) + 2]);
                }

                break;
            case PixelFormat.Rgb24:
                for (int x = 0; x < count; x++)
                {
                    to[x * 3] = To8(rgba[x * 4]);
                    to[(x * 3) + 1] = To8(rgba[(x * 4) + 1]);
                    to[(x * 3) + 2] = To8(rgba[(x * 4) + 2]);
                }

                break;
            case PixelFormat.Rgba32:
            case PixelFormat.Bgra32:
                int r = format == PixelFormat.Bgra32 ? 2 : 0, b = 2 - r;
                for (int x = 0; x < count; x++)
                {
                    to[(x * 4) + r] = To8(rgba[x * 4]);
                    to[(x * 4) + 1] = To8(rgba[(x * 4) + 1]);
                    to[(x * 4) + b] = To8(rgba[(x * 4) + 2]);
                    to[(x * 4) + 3] = To8(rgba[(x * 4) + 3]);
                }

                break;
            case PixelFormat.Rgb48:
                Span<ushort> rgb = MemoryMarshal.Cast<byte, ushort>(to);
                for (int x = 0; x < count; x++)
                {
                    rgb[x * 3] = rgba[x * 4];
                    rgb[(x * 3) + 1] = rgba[(x * 4) + 1];
                    rgb[(x * 3) + 2] = rgba[(x * 4) + 2];
                }

                break;
            default: // Rgba64
                rgba.CopyTo(MemoryMarshal.Cast<byte, ushort>(to));
                break;
        }
    }

    /// <summary>The nearest 8-bit value; exact for a value that came from 8 bits as <c>v * 257</c>.</summary>
    private static byte To8(ushort value) => (byte)((value + 128) / 257);
}
