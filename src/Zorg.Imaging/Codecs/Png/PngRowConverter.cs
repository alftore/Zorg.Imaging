using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Zorg.Imaging.Codecs.Png;

/// <summary>Turns a PNG row's raw samples into pixels of the decoded layout.</summary>
/// <remarks>
/// Samples go through 16-bit RGBA: 8-bit values times 257, lower depths scaled up exactly (a 1-bit 1 becomes 65535).
/// For an 8-bit target, 16-bit samples round as <c>(v + 128) / 257</c>, the nearest value, which is what libpng's
/// scale-16 and ImageMagick give; 8-bit and lower depths come back exactly. A <c>tRNS</c> colour is matched against the
/// raw sample at full depth, before scaling.
/// </remarks>
internal sealed class PngRowConverter
{
    private readonly PngHeader _header;
    private readonly ushort[] _palette = [];
    private readonly int _transparentGrey = -1;
    private readonly int _transparentRed = -1;
    private readonly int _transparentGreen = -1;
    private readonly int _transparentBlue = -1;
    private ushort[] _rgba = [];

    /// <summary>Prepares a converter.</summary>
    /// <param name="header">The image header.</param>
    /// <param name="palette">The PLTE data, used only for palette images.</param>
    /// <param name="transparency">The tRNS data, or <see langword="null"/>.</param>
    /// <param name="requested">The layout asked for, or <see cref="PixelFormat.None"/> for the file's own.</param>
    public PngRowConverter(PngHeader header, byte[]? palette, byte[]? transparency, PixelFormat requested)
    {
        _header = header;
        Target = requested != PixelFormat.None ? requested : NaturalFormat(header, transparency is not null);

        if (header.ColourType == 3)
        {
            // Every index gets an entry: past the end of the palette is opaque black, as browsers show it.
            _palette = new ushort[256 * 4];
            for (int i = 0; i < 256; i++)
            {
                _palette[(i * 4) + 3] = 65535;
            }

            for (int i = 0; i < palette!.Length / 3; i++)
            {
                _palette[i * 4] = (ushort)(palette[i * 3] * 257);
                _palette[(i * 4) + 1] = (ushort)(palette[(i * 3) + 1] * 257);
                _palette[(i * 4) + 2] = (ushort)(palette[(i * 3) + 2] * 257);
            }

            for (int i = 0; transparency is not null && i < transparency.Length; i++)
            {
                _palette[(i * 4) + 3] = (ushort)(transparency[i] * 257);
            }
        }
        else if (transparency is not null && header.ColourType == 0)
        {
            _transparentGrey = BinaryPrimitives.ReadUInt16BigEndian(transparency);
        }
        else if (transparency is not null && header.ColourType == 2)
        {
            _transparentRed = BinaryPrimitives.ReadUInt16BigEndian(transparency);
            _transparentGreen = BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2));
            _transparentBlue = BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4));
        }
    }

    /// <summary>The layout the rows are converted to.</summary>
    public PixelFormat Target { get; }

    /// <summary>The layout that holds the file without loss.</summary>
    /// <param name="header">The image header.</param>
    /// <param name="transparency">Whether there's a <c>tRNS</c> chunk.</param>
    /// <returns>The layout.</returns>
    public static PixelFormat NaturalFormat(PngHeader header, bool transparency)
    {
        bool alpha = header.ColourType is 4 or 6 || transparency;
        bool grey = header.ColourType is 0 or 4;
        bool wide = header.BitDepth == 16;
        return alpha ? (wide ? PixelFormat.Rgba64 : PixelFormat.Rgba32)
            : grey ? (wide ? PixelFormat.L16 : PixelFormat.L8)
            : wide ? PixelFormat.Rgb48 : PixelFormat.Rgb24;
    }

    /// <summary>Converts one row.</summary>
    /// <param name="raw">The unfiltered row.</param>
    /// <param name="destination">The pixels, <paramref name="width"/> times the target's bytes per pixel.</param>
    /// <param name="width">The pixels in the row.</param>
    public void Convert(ReadOnlySpan<byte> raw, Span<byte> destination, int width)
    {
        // The common layouts copy straight across.
        if (_header.BitDepth == 8 && _transparentGrey < 0 && _transparentRed < 0
            && ((_header.ColourType == 0 && Target == PixelFormat.L8)
                || (_header.ColourType == 2 && Target == PixelFormat.Rgb24)
                || (_header.ColourType == 6 && Target == PixelFormat.Rgba32)))
        {
            raw[..destination.Length].CopyTo(destination);
            return;
        }

        if (_rgba.Length < width * 4)
        {
            _rgba = new ushort[width * 4];
        }

        Span<ushort> rgba = _rgba.AsSpan(0, width * 4);
        ToRgba(raw, rgba, width);
        Pack(rgba, destination, Target);
    }

    private static void Pack(ReadOnlySpan<ushort> rgba, Span<byte> destination, PixelFormat target)
    {
        int pixels = rgba.Length / 4;
        switch (target)
        {
            case PixelFormat.Rgba64:
                MemoryMarshal.AsBytes(rgba).CopyTo(destination);
                break;

            case PixelFormat.Rgb48:
                // Chosen only when the file has no transparency, so alpha is always opaque here.
                Span<ushort> rgb48 = MemoryMarshal.Cast<byte, ushort>(destination);
                for (int i = 0; i < pixels; i++)
                {
                    rgba.Slice(i * 4, 3).CopyTo(rgb48[(i * 3)..]);
                }

                break;

            case PixelFormat.L16:
                // Grey without transparency: red holds the grey value.
                Span<ushort> l16 = MemoryMarshal.Cast<byte, ushort>(destination);
                for (int i = 0; i < pixels; i++)
                {
                    l16[i] = rgba[i * 4];
                }

                break;

            case PixelFormat.Rgba32:
                for (int i = 0; i < rgba.Length; i++)
                {
                    destination[i] = To8(rgba[i]);
                }

                break;

            case PixelFormat.Bgra32:
                for (int i = 0; i < pixels; i++)
                {
                    destination[i * 4] = To8(rgba[(i * 4) + 2]);
                    destination[(i * 4) + 1] = To8(rgba[(i * 4) + 1]);
                    destination[(i * 4) + 2] = To8(rgba[i * 4]);
                    destination[(i * 4) + 3] = To8(rgba[(i * 4) + 3]);
                }

                break;

            case PixelFormat.Rgb24:
                for (int i = 0; i < pixels; i++)
                {
                    destination[i * 3] = To8(rgba[i * 4]);
                    destination[(i * 3) + 1] = To8(rgba[(i * 4) + 1]);
                    destination[(i * 3) + 2] = To8(rgba[(i * 4) + 2]);
                }

                break;

            default:
                // L8.
                for (int i = 0; i < pixels; i++)
                {
                    destination[i] = To8(rgba[i * 4]);
                }

                break;
        }
    }

    private void ToRgba(ReadOnlySpan<byte> raw, Span<ushort> rgba, int width)
    {
        int depth = _header.BitDepth;
        switch (_header.ColourType)
        {
            case 0:
                for (int i = 0; i < width; i++)
                {
                    int v = Sample(raw, i, depth);
                    ushort g = Scale(v, depth);
                    Set(rgba, i, g, g, g, v == _transparentGrey ? (ushort)0 : (ushort)65535);
                }

                break;

            case 2:
                for (int i = 0; i < width; i++)
                {
                    int r = Sample(raw, i * 3, depth);
                    int g = Sample(raw, (i * 3) + 1, depth);
                    int b = Sample(raw, (i * 3) + 2, depth);
                    bool clear = r == _transparentRed && g == _transparentGreen && b == _transparentBlue;
                    Set(rgba, i, Scale(r, depth), Scale(g, depth), Scale(b, depth), clear ? (ushort)0 : (ushort)65535);
                }

                break;

            case 3:
                for (int i = 0; i < width; i++)
                {
                    _palette.AsSpan(Sample(raw, i, depth) * 4, 4).CopyTo(rgba[(i * 4)..]);
                }

                break;

            case 4:
                for (int i = 0; i < width; i++)
                {
                    ushort g = Scale(Sample(raw, i * 2, depth), depth);
                    Set(rgba, i, g, g, g, Scale(Sample(raw, (i * 2) + 1, depth), depth));
                }

                break;

            default:
                for (int i = 0; i < width * 4; i++)
                {
                    rgba[i] = Scale(Sample(raw, i, depth), depth);
                }

                break;
        }
    }

    private static void Set(Span<ushort> rgba, int pixel, ushort r, ushort g, ushort b, ushort a)
    {
        int at = pixel * 4;
        rgba[at] = r;
        rgba[at + 1] = g;
        rgba[at + 2] = b;
        rgba[at + 3] = a;
    }

    // The n-th sample of the row at the given depth, most significant bits first for depths below 8.
    private static int Sample(ReadOnlySpan<byte> raw, int n, int depth) => depth switch
    {
        8 => raw[n],
        16 => BinaryPrimitives.ReadUInt16BigEndian(raw[(n * 2)..]),
        _ => (raw[n * depth / 8] >> (8 - depth - (n * depth % 8))) & ((1 << depth) - 1),
    };

    // To 16 bits: exact for every depth, since 65535 is a multiple of 1, 3, 15 and 255.
    private static ushort Scale(int value, int depth) => depth == 16 ? (ushort)value : (ushort)(value * (65535 / ((1 << depth) - 1)));

    private static byte To8(ushort value) => (byte)((value + 128) / 257);
}
