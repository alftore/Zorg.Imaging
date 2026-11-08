using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Codecs.Gif;

/// <summary>
/// Reads a GIF (GIF89a, and GIF87a) frame by frame onto its canvas, as a browser shows it: each frame drawn over what
/// the previous frames left, after the previous frame's disposal.
/// </summary>
/// <remarks>
/// <para>
/// The canvas starts transparent, and disposal to the background clears to transparent rather than to the background
/// colour, as browsers do. A logical screen of 0 x 0 takes the first frame's size, with the frames placed relative to
/// it. Parts of frames outside the canvas are clipped.
/// </para>
/// <para>
/// Hostile input: the canvas and each frame are checked against <see cref="ImageLimits.MaxPixels"/> and the frame count
/// against <see cref="ImageLimits.MaxFrames"/> before anything is allocated. A frame's compressed data is kept to what
/// its pixels could need; anything past that is skipped unread. Image data that runs short, or has a code that can't
/// be right, leaves the rest of the frame as it was, as browsers do.
/// </para>
/// </remarks>
internal sealed class GifReader
{
    private const byte Extension = 0x21;
    private const byte ImageDescriptor = 0x2C;
    private const byte Trailer = 0x3B;
    private const byte GraphicControl = 0xF9;
    private const byte Application = 0xFF;

    private readonly DataReader _reader;
    private readonly ImageLimits _limits;
    private readonly uint[] _globalPalette = new uint[256];
    private readonly uint[] _localPalette = new uint[256];
    private readonly byte[] _block = new byte[256];
    private bool _hasGlobalPalette;
    private int _originX;
    private int _originY;
    private bool _ended;

    // What the previous frame asked to be done with its rectangle before the next is drawn.
    private int _disposal;
    private (int X, int Y, int Width, int Height) _disposeArea;
    private uint[]? _saved;

    private GifReader(DataReader reader, ImageLimits limits)
    {
        _reader = reader;
        _limits = limits;
    }

    /// <summary>The canvas width.</summary>
    public int Width { get; private set; }

    /// <summary>The canvas height.</summary>
    public int Height { get; private set; }

    /// <summary>The canvas after the last frame read, ARGB, row by row. Empty until the first frame.</summary>
    public uint[] Canvas { get; private set; } = [];

    /// <summary>
    /// Whether a first frame that fills the canvas is kept as <see cref="FirstFrame"/> instead of drawn, for a decoder
    /// that wants only the first frame. Reading on past such a frame isn't supported.
    /// </summary>
    public bool KeepFirstFrame { get; init; }

    /// <summary>The first frame, when <see cref="KeepFirstFrame"/> is set and it fills the canvas.</summary>
    public Frame? FirstFrame { get; private set; }

    /// <summary>How many frames have been read.</summary>
    public int Frames { get; private set; }

    /// <summary>The last frame's delay, in hundredths of a second.</summary>
    public int Delay { get; private set; }

    /// <summary>The colour space from an ICC profile in an <c>ICCRGBG1012</c> extension before the first frame, or sRGB.</summary>
    public ColorSpace ColorSpace { get; private set; } = ColorSpace.Srgb;

    /// <summary>Reads the header and the global colour table. The reader is before the signature.</summary>
    public static async ValueTask<GifReader> OpenAsync(DataReader reader, ImageLimits limits, CancellationToken cancellationToken, bool keepFirstFrame = false)
    {
        reader.FormatName = "GIF";
        var gif = new GifReader(reader, limits) { KeepFirstFrame = keepFirstFrame };
        byte[] head = new byte[13];
        await reader.ReadExactlyAsync(head, cancellationToken).ConfigureAwait(false);
        if (!head.AsSpan(0, 3).SequenceEqual("GIF"u8))
        {
            throw ImageErrors.Corrupt("The GIF file doesn't start with a GIF signature.");
        }

        gif.Width = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6));
        gif.Height = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(8));
        byte packed = head[10];
        if ((packed & 0x80) != 0)
        {
            await gif.ReadPaletteAsync(gif._globalPalette, packed, cancellationToken).ConfigureAwait(false);
            gif._hasGlobalPalette = true;
        }

        return gif;
    }

    /// <summary>Reads the next frame and draws it on the canvas.</summary>
    /// <returns>Whether there was a frame: <see langword="false"/> at the trailer or the end of the data.</returns>
    public async ValueTask<bool> NextFrameAsync(CancellationToken cancellationToken)
    {
        if (_ended)
        {
            return false;
        }

        int transparent = -1;
        int disposal = 0;
        int delay = 0;
        byte[] one = new byte[1];
        while (true)
        {
            // A file that ends where the trailer should be is complete enough: plenty of GIFs in the wild do.
            if (Frames > 0 && (await _reader.PeekAsync(1, cancellationToken).ConfigureAwait(false)).IsEmpty)
            {
                _ended = true;
                return false;
            }

            await _reader.ReadExactlyAsync(one, cancellationToken).ConfigureAwait(false);
            switch (one[0])
            {
                case Trailer:
                    _ended = true;
                    return false;

                case Extension:
                    await _reader.ReadExactlyAsync(one, cancellationToken).ConfigureAwait(false);
                    byte label = one[0];
                    int size = await ReadBlockAsync(cancellationToken).ConfigureAwait(false);
                    if (label == GraphicControl && size >= 4)
                    {
                        disposal = (_block[0] >> 2) & 7;
                        delay = BinaryPrimitives.ReadUInt16LittleEndian(_block.AsSpan(1));
                        transparent = (_block[0] & 1) != 0 ? _block[3] : -1;
                    }
                    else if (label == Application && size == 11 && Frames == 0 && _block.AsSpan(0, 11).SequenceEqual("ICCRGBG1012"u8))
                    {
                        await ReadProfileAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    }

                    if (size > 0)
                    {
                        await SkipBlocksAsync(cancellationToken).ConfigureAwait(false);
                    }

                    break;

                case ImageDescriptor:
                    await ReadFrameAsync(transparent, disposal, cancellationToken).ConfigureAwait(false);
                    Delay = delay;
                    return true;

                default:
                    throw ImageErrors.Corrupt("The GIF file has a block that isn't an extension, an image or the trailer.");
            }
        }
    }

    private async ValueTask ReadFrameAsync(int transparent, int disposal, CancellationToken cancellationToken)
    {
        byte[] descriptor = new byte[9];
        await _reader.ReadExactlyAsync(descriptor, cancellationToken).ConfigureAwait(false);
        int left = BinaryPrimitives.ReadUInt16LittleEndian(descriptor);
        int top = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(2));
        int width = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(4));
        int height = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(6));
        byte packed = descriptor[8];

        if (++Frames > _limits.MaxFrames)
        {
            throw ImageErrors.ExceedsLimits(string.Create(
                CultureInfo.InvariantCulture,
                $"The GIF file has more than {_limits.MaxFrames} frames (ImageLimits.MaxFrames)."));
        }

        if (Frames == 1)
        {
            if (Width == 0 || Height == 0)
            {
                (Width, Height, _originX, _originY) = (width, height, left, top);
            }

            CheckSize("canvas", Width, Height);
        }

        // A first frame that fills the canvas exactly can go straight to the caller, without a canvas.
        bool direct = Frames == 1 && KeepFirstFrame && left == _originX && top == _originY && width == Width && height == Height;
        if (!direct && Canvas.Length == 0)
        {
            Canvas = new uint[Width * Height];
        }

        CheckSize("frame", width, height);
        uint[] palette = _globalPalette;
        if ((packed & 0x80) != 0)
        {
            await ReadPaletteAsync(_localPalette, packed, cancellationToken).ConfigureAwait(false);
            palette = _localPalette;
        }
        else if (!_hasGlobalPalette)
        {
            // No colour table at all: the spec leaves it to the decoder. Black, as the tables start.
            Array.Clear(_localPalette);
            for (int i = 0; i < 256; i++)
            {
                _localPalette[i] = 0xFF000000u;
            }

            palette = _localPalette;
        }

        byte[] one = new byte[1];
        await _reader.ReadExactlyAsync(one, cancellationToken).ConfigureAwait(false);
        int minimumCodeSize = one[0];
        if (minimumCodeSize is < 1 or > 8)
        {
            throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"A GIF frame's LZW code size is {minimumCodeSize}; it must be 1 to 8."));
        }

        // Every code gives at least one index, and a code is at most 12 bits; clear codes come at most once per new code
        // in the worst real encoders. Twice the pixels, plus a little, is more than any honest frame needs.
        int pixels = width * height;
        int cap = (int)Math.Min(int.MaxValue - 4096, (2L * pixels) + 4096);
        byte[] data = ArrayPool<byte>.Shared.Rent(Math.Min(cap, 64 * 1024));
        byte[] indices = ArrayPool<byte>.Shared.Rent(Math.Max(pixels, 1));
        try
        {
            int length = 0;
            while (true)
            {
                int size = await ReadBlockAsync(cancellationToken).ConfigureAwait(false);
                if (size == 0)
                {
                    break;
                }

                if (length + size > cap)
                {
                    await SkipBlocksAsync(cancellationToken).ConfigureAwait(false);
                    break;
                }

                if (length + size > data.Length)
                {
                    byte[] larger = ArrayPool<byte>.Shared.Rent((int)Math.Min(cap, (long)data.Length * 2));
                    data.AsSpan(0, length).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(data);
                    data = larger;
                }

                _block.AsSpan(0, size).CopyTo(data.AsSpan(length));
                length += size;
            }

            int decoded = GifLzw.Decode(data.AsSpan(0, length), minimumCodeSize, indices.AsSpan(0, pixels));
            if (direct)
            {
                uint[] colours = (uint[])palette.Clone();
                if (transparent >= 0)
                {
                    colours[transparent] = 0;
                }

                FirstFrame = new Frame(indices, decoded, colours, (packed & 0x40) != 0);
                indices = [];
                _disposal = disposal;
                return;
            }

            (int X, int Y, int Width, int Height) area = (left - _originX, top - _originY, width, height);
            Dispose();
            Save(disposal == 3 ? area : default);
            (_disposal, _disposeArea) = (disposal, area);
            Draw(indices.AsSpan(0, pixels), decoded, palette, transparent, left - _originX, top - _originY, width, height, interlaced: (packed & 0x40) != 0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
            if (indices.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(indices);
            }
        }
    }

    private void CheckSize(string what, int width, int height)
    {
        long pixels = (long)width * height;
        if (pixels > _limits.MaxPixels)
        {
            throw ImageErrors.ExceedsLimits(string.Create(
                CultureInfo.InvariantCulture,
                $"The GIF {what} is {width} x {height}, {pixels} pixels, more than the {_limits.MaxPixels}-pixel limit (ImageLimits.MaxPixels)."));
        }
    }

    // Applies the previous frame's disposal to its own rectangle: 2 clears it, 3 puts back what it covered. Others,
    // including the undefined 4 to 7, leave it.
    private void Dispose()
    {
        if (Frames == 1)
        {
            return;
        }

        (int x, int y, int width, int height) = Clip(_disposeArea);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_disposal == 2)
        {
            for (int row = y; row < y + height; row++)
            {
                Canvas.AsSpan((row * Width) + x, width).Clear();
            }
        }
        else if (_disposal == 3 && _saved is not null)
        {
            for (int row = 0; row < height; row++)
            {
                _saved.AsSpan(row * width, width).CopyTo(Canvas.AsSpan(((y + row) * Width) + x));
            }
        }
    }

    // Keeps what a frame that will be disposed of by "restore to previous" is about to cover.
    private void Save((int X, int Y, int Width, int Height) area)
    {
        _saved = null;
        (int x, int y, int width, int height) = Clip(area);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _saved = new uint[width * height];
        for (int row = 0; row < height; row++)
        {
            Canvas.AsSpan(((y + row) * Width) + x, width).CopyTo(_saved.AsSpan(row * width));
        }
    }

    private (int X, int Y, int Width, int Height) Clip((int X, int Y, int Width, int Height) area)
    {
        int x0 = Math.Max(area.X, 0);
        int y0 = Math.Max(area.Y, 0);
        int x1 = Math.Min(area.X + area.Width, Width);
        int y1 = Math.Min(area.Y + area.Height, Height);
        return (x0, y0, x1 - x0, y1 - y0);
    }

    private void Draw(ReadOnlySpan<byte> indices, int decoded, uint[] palette, int transparent, int left, int top, int width, int height, bool interlaced)
    {
        (int x0, int y0, int clippedWidth, int clippedHeight) = Clip((left, top, width, height));
        if (clippedWidth <= 0 || clippedHeight <= 0)
        {
            return;
        }

        int skip = x0 - left;
        for (int stored = 0; stored < height; stored++)
        {
            int row = interlaced ? InterlacedRow(stored, height) : stored;
            int y = top + row;
            if (y < y0 || y >= y0 + clippedHeight)
            {
                continue;
            }

            int start = (stored * width) + skip;
            int available = Math.Min(clippedWidth, decoded - start);
            if (available <= 0)
            {
                continue;
            }

            ReadOnlySpan<byte> source = indices.Slice(start, available);
            Span<uint> target = Canvas.AsSpan((y * Width) + x0, available);
            if (transparent < 0)
            {
                for (int i = 0; i < source.Length; i++)
                {
                    target[i] = palette[source[i]];
                }
            }
            else
            {
                for (int i = 0; i < source.Length; i++)
                {
                    byte index = source[i];
                    if (index != transparent)
                    {
                        target[i] = palette[index];
                    }
                }
            }
        }
    }

    // The image row that the n-th stored row of an interlaced frame belongs to: every 8th row from 0, every 8th from 4,
    // every 4th from 2, then every 2nd from 1.
    internal static int InterlacedRow(int stored, int height)
    {
        int pass1 = (height + 7) / 8;
        if (stored < pass1)
        {
            return stored * 8;
        }

        stored -= pass1;
        int pass2 = (height + 3) / 8;
        if (stored < pass2)
        {
            return 4 + (stored * 8);
        }

        stored -= pass2;
        int pass3 = (height + 1) / 4;
        if (stored < pass3)
        {
            return 2 + (stored * 4);
        }

        stored -= pass3;
        return 1 + (stored * 2);
    }

    private async ValueTask ReadPaletteAsync(uint[] palette, byte packed, CancellationToken cancellationToken)
    {
        int count = 2 << (packed & 7);
        byte[] rgb = new byte[count * 3];
        await _reader.ReadExactlyAsync(rgb, cancellationToken).ConfigureAwait(false);
        for (int i = 0; i < 256; i++)
        {
            palette[i] = i < count ? 0xFF000000u | ((uint)rgb[3 * i] << 16) | ((uint)rgb[(3 * i) + 1] << 8) | rgb[(3 * i) + 2] : 0xFF000000u;
        }
    }

    // Reads one sub-block into _block and returns its length; 0 is the terminator.
    private async ValueTask<int> ReadBlockAsync(CancellationToken cancellationToken)
    {
        await _reader.ReadExactlyAsync(_block.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        int size = _block[0];
        if (size > 0)
        {
            await _reader.ReadExactlyAsync(_block.AsMemory(0, size), cancellationToken).ConfigureAwait(false);
        }

        return size;
    }

    private async ValueTask SkipBlocksAsync(CancellationToken cancellationToken)
    {
        while (await ReadBlockAsync(cancellationToken).ConfigureAwait(false) != 0)
        {
        }
    }

    private async ValueTask ReadProfileAsync(CancellationToken cancellationToken)
    {
        byte[] profile = new byte[1024];
        int length = 0;
        while (true)
        {
            int size = await ReadBlockAsync(cancellationToken).ConfigureAwait(false);
            if (size == 0)
            {
                break;
            }

            if (length + size > ColorSpace.MaxIccProfileBytes)
            {
                throw ImageErrors.ExceedsLimits(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The GIF file's ICC profile is more than {ColorSpace.MaxIccProfileBytes} bytes."));
            }

            if (length + size > profile.Length)
            {
                Array.Resize(ref profile, Math.Min(profile.Length * 2, ColorSpace.MaxIccProfileBytes));
            }

            _block.AsSpan(0, size).CopyTo(profile.AsSpan(length));
            length += size;
        }

        byte[] bytes = profile[..length];
        string? problem = ColorSpace.CheckIccProfile(bytes);
        ColorSpace = problem is null ? ColorSpace.FromCheckedIccProfile(bytes) : throw ImageErrors.Corrupt(problem);
    }

    /// <summary>
    /// A frame's colour indices, row by row as stored (interlaced or not), from a pooled array the taker returns; how
    /// many were decoded; and its colours as ARGB, the transparent one 0.
    /// </summary>
    internal sealed record Frame(byte[] Indices, int Decoded, uint[] Colours, bool Interlaced);
}
