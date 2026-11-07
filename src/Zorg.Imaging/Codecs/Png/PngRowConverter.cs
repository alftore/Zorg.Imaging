using System;
using System.Buffers.Binary;

namespace Zorg.Imaging.Codecs.Png;

/// <summary>Turns a PNG row's raw samples into pixels of the decoded layout.</summary>
/// <remarks>
/// 16-bit samples round to 8 bits as <c>(v + 128) / 257</c>, the nearest value, which is what libpng's scale-16 and
/// ImageMagick give. Grey below 8 bits is scaled up exactly (a 1-bit 1 becomes 255). A <c>tRNS</c> colour is matched
/// against the raw sample at full depth, before scaling.
/// </remarks>
internal sealed class PngRowConverter
{
    private readonly PngHeader _header;
    private readonly byte[] _palette = [];
    private readonly int _transparentGrey = -1;
    private readonly int _transparentRed = -1;
    private readonly int _transparentGreen = -1;
    private readonly int _transparentBlue = -1;
    private byte[] _rgba = [];

    /// <summary>Prepares a converter.</summary>
    /// <param name="header">The image header.</param>
    /// <param name="palette">The PLTE data, used only for palette images.</param>
    /// <param name="transparency">The tRNS data, or <see langword="null"/>.</param>
    /// <param name="requested">The layout asked for, or <see cref="PixelFormat.None"/> for the file's own.</param>
    public PngRowConverter(PngHeader header, byte[]? palette, byte[]? transparency, PixelFormat requested)
    {
        _header = header;
        bool alpha = header.ColourType is 4 or 6 || transparency is not null;
        bool grey = header.ColourType is 0 or 4;
        Target = requested != PixelFormat.None ? requested
            : alpha ? PixelFormat.Rgba32
            : grey ? PixelFormat.L8
            : PixelFormat.Rgb24;

        if (header.ColourType == 3)
        {
            // Every index gets an entry: past the end of the palette is opaque black, as browsers show it.
            _palette = new byte[256 * 4];
            for (int i = 0; i < 256; i++)
            {
                _palette[(i * 4) + 3] = 255;
            }

            for (int i = 0; i < palette!.Length / 3; i++)
            {
                palette.AsSpan(i * 3, 3).CopyTo(_palette.AsSpan(i * 4));
            }

            for (int i = 0; transparency is not null && i < transparency.Length; i++)
            {
                _palette[(i * 4) + 3] = transparency[i];
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

        if (Target == PixelFormat.Rgba32)
        {
            ToRgba(raw, destination, width);
            return;
        }

        if (_rgba.Length < width * 4)
        {
            _rgba = new byte[width * 4];
        }

        Span<byte> rgba = _rgba.AsSpan(0, width * 4);
        ToRgba(raw, rgba, width);
        Pack(rgba, destination, Target);
    }

    private static void Pack(ReadOnlySpan<byte> rgba, Span<byte> destination, PixelFormat target)
    {
        int pixels = rgba.Length / 4;
        switch (target)
        {
            case PixelFormat.Bgra32:
                for (int i = 0; i < pixels; i++)
                {
                    destination[i * 4] = rgba[(i * 4) + 2];
                    destination[(i * 4) + 1] = rgba[(i * 4) + 1];
                    destination[(i * 4) + 2] = rgba[i * 4];
                    destination[(i * 4) + 3] = rgba[(i * 4) + 3];
                }

                break;

            case PixelFormat.Rgb24:
                // Chosen only when the file has no transparency, so alpha is always 255 here.
                for (int i = 0; i < pixels; i++)
                {
                    rgba.Slice(i * 4, 3).CopyTo(destination[(i * 3)..]);
                }

                break;

            default:
                // L8: chosen only for grey without transparency, so red holds the grey value.
                for (int i = 0; i < pixels; i++)
                {
                    destination[i] = rgba[i * 4];
                }

                break;
        }
    }

    private void ToRgba(ReadOnlySpan<byte> raw, Span<byte> rgba, int width)
    {
        int depth = _header.BitDepth;
        switch (_header.ColourType)
        {
            case 0:
                for (int i = 0; i < width; i++)
                {
                    int v = Sample(raw, i, depth);
                    byte g = Scale(v, depth);
                    Set(rgba, i, g, g, g, v == _transparentGrey ? (byte)0 : (byte)255);
                }

                break;

            case 2:
                for (int i = 0; i < width; i++)
                {
                    int r = Sample(raw, i * 3, depth);
                    int g = Sample(raw, (i * 3) + 1, depth);
                    int b = Sample(raw, (i * 3) + 2, depth);
                    bool clear = r == _transparentRed && g == _transparentGreen && b == _transparentBlue;
                    Set(rgba, i, Scale(r, depth), Scale(g, depth), Scale(b, depth), clear ? (byte)0 : (byte)255);
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
                    byte g = Scale(Sample(raw, i * 2, depth), depth);
                    Set(rgba, i, g, g, g, Scale(Sample(raw, (i * 2) + 1, depth), depth));
                }

                break;

            default:
                for (int i = 0; i < width; i++)
                {
                    Set(
                        rgba,
                        i,
                        Scale(Sample(raw, i * 4, depth), depth),
                        Scale(Sample(raw, (i * 4) + 1, depth), depth),
                        Scale(Sample(raw, (i * 4) + 2, depth), depth),
                        Scale(Sample(raw, (i * 4) + 3, depth), depth));
                }

                break;
        }
    }

    private static void Set(Span<byte> rgba, int pixel, byte r, byte g, byte b, byte a)
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

    private static byte Scale(int value, int depth) => depth switch
    {
        8 => (byte)value,
        16 => (byte)((value + 128) / 257),
        _ => (byte)(value * 255 / ((1 << depth) - 1)),
    };
}
