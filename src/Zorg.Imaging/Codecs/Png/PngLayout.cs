using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Zorg.Imaging.Codecs.Png;

/// <summary>
/// How an image is laid out in a PNG file: colour type, bit depth, palette and transparency, and how a row of the image
/// becomes a row of the file.
/// </summary>
/// <remarks>
/// <see cref="Choose"/> picks the smallest layout that holds the image exactly, as oxipng does: RGBA whose alpha is all
/// opaque becomes RGB, colour with R = G = B becomes grey, up to 256 colours become a palette, and 16-bit samples that
/// are all <c>x * 257</c> become 8-bit; grey and palette indices are packed into 1, 2 or 4 bits when they fit. A
/// single fully transparent colour becomes a <c>tRNS</c> key instead of an alpha channel. When the image has a colour
/// space other than sRGB, grey stays grey and colour stays colour, since a profile is for one or the other.
/// </remarks>
internal sealed class PngLayout
{
    private const int TableSize = 1024;

    private readonly Mode _mode;
    private readonly bool _clean;
    private readonly uint[]? _keys;
    private readonly short[]? _slots;

    private PngLayout(byte colourType, byte bitDepth, Mode mode, bool clean, byte[]? palette = null, byte[]? transparency = null, uint[]? keys = null, short[]? slots = null)
    {
        ColourType = colourType;
        BitDepth = bitDepth;
        _mode = mode;
        _clean = clean;
        Palette = palette;
        Transparency = transparency;
        _keys = keys;
        _slots = slots;
    }

    private enum Mode
    {
        /// <summary>The image's own layout: a copy, a BGRA swap, or 16-bit samples made big-endian.</summary>
        Direct,

        /// <summary>8-bit samples (or 16-bit ones that are all <c>x * 257</c>) to any 8-bit or smaller layout.</summary>
        Narrow,

        /// <summary>16-bit samples to fewer 16-bit channels.</summary>
        Wide,
    }

    /// <summary>The PNG colour type: 0 grey, 2 RGB, 3 palette, 4 grey and alpha, 6 RGBA.</summary>
    public byte ColourType { get; }

    /// <summary>Bits per sample (per index for a palette).</summary>
    public byte BitDepth { get; }

    /// <summary>The <c>PLTE</c> chunk's data, or null.</summary>
    public byte[]? Palette { get; }

    /// <summary>The <c>tRNS</c> chunk's data, or null.</summary>
    public byte[]? Transparency { get; }

    /// <summary>Whether rows are packed below a byte a sample, where filters seldom help.</summary>
    public bool IsPaletteOrPacked => ColourType == 3 || BitDepth < 8;

    /// <summary>Bits a pixel.</summary>
    public int BitsPerPixel => BitDepth * ColourType switch { 0 or 3 => 1, 4 => 2, 2 => 3, _ => 4 };

    /// <summary>How far back the Sub, Average and Paeth filters look: a pixel's bytes, at least one.</summary>
    public int FilterStep => Math.Max(1, BitsPerPixel / 8);

    /// <summary>Bytes in a row of the file, without the filter byte.</summary>
    public int RowBytes(int width) => (int)(((long)width * BitsPerPixel + 7) / 8);

    /// <summary>The image's own layout, unreduced: grey, RGB or RGBA at 8 or 16 bits.</summary>
    public static PngLayout Of(Image image, bool cleanTransparent)
    {
        bool wide = image.PixelFormat is PixelFormat.L16 or PixelFormat.Rgb48 or PixelFormat.Rgba64;
        byte colourType = image.PixelFormat switch
        {
            PixelFormat.L8 or PixelFormat.L16 => 0,
            PixelFormat.Rgb24 or PixelFormat.Rgb48 => 2,
            _ => 6,
        };
        return new PngLayout(colourType, wide ? (byte)16 : (byte)8, Mode.Direct, cleanTransparent && colourType == 6);
    }

    /// <summary>The smallest layout that holds the image exactly.</summary>
    /// <param name="image">The image.</param>
    /// <param name="cleanTransparent">
    /// Whether fully transparent pixels may be written as transparent black, which lets them share a palette entry or a
    /// <c>tRNS</c> key.
    /// </param>
    /// <param name="allowPalette">Whether a palette may be chosen; <see langword="false"/> gives the best layout without one.</param>
    public static PngLayout Choose(Image image, bool cleanTransparent, bool allowPalette = true)
    {
        bool hasAlpha = image.PixelFormat is PixelFormat.Rgba32 or PixelFormat.Bgra32 or PixelFormat.Rgba64;
        bool greySource = image.PixelFormat is PixelFormat.L8 or PixelFormat.L16;
        bool free = image.ColorSpace.IsSrgb;
        bool allowGrey = greySource || free;
        bool allowColour = !greySource || free;
        bool clean = cleanTransparent && hasAlpha;

        if (image.PixelFormat is PixelFormat.L16 or PixelFormat.Rgb48 or PixelFormat.Rgba64 && !FitsInEightBits(image, clean))
        {
            return ChooseWide(image, allowGrey, clean);
        }

        var scan = new Scan(hasAlpha, clean, countColours: allowColour);
        Span<uint> row = new uint[image.Width];
        for (int y = 0; y < image.Height && !scan.Settled; y++)
        {
            ReadNarrow(image, y, row);
            scan.Add(row);
        }

        // A tRNS key works only when every transparent pixel has the key's colour and no opaque pixel does.
        bool keyed = !scan.Opaque && scan.BinaryAlpha && scan.KeyUnique && !KeyUsedOpaque(image, scan, row);

        long width = image.Width;
        long Bytes(int bits) => image.Height * (1 + ((width * bits) + 7) / 8);
        long best = long.MaxValue;
        PngLayout? chosen = null;

        void Consider(long size, Func<PngLayout> make)
        {
            if (size < best)
            {
                best = size;
                chosen = make();
            }
        }

        if (allowGrey && scan.Grey)
        {
            if (scan.Opaque)
            {
                Consider(Bytes(scan.GreyBits), () => new PngLayout(0, (byte)scan.GreyBits, Mode.Narrow, clean));
            }
            else
            {
                if (keyed)
                {
                    Consider(Bytes(scan.GreyBits) + 2, () => new PngLayout(0, (byte)scan.GreyBits, Mode.Narrow, clean, transparency: GreyKey(scan.Key & 0xFF, scan.GreyBits)));
                }

                Consider(Bytes(16), () => new PngLayout(4, 8, Mode.Narrow, clean));
            }
        }

        if (allowColour)
        {
            if (scan.Opaque)
            {
                Consider(Bytes(24), () => new PngLayout(2, 8, Mode.Narrow, clean));
            }
            else
            {
                if (keyed)
                {
                    Consider(Bytes(24) + 6, () => new PngLayout(2, 8, Mode.Narrow, clean, transparency: RgbKey(scan.Key)));
                }

                Consider(Bytes(32), () => new PngLayout(6, 8, Mode.Narrow, clean));
            }

            if (allowPalette && scan.Colours <= 256)
            {
                int count = scan.Colours;
                int bits = count <= 2 ? 1 : count <= 4 ? 2 : count <= 16 ? 4 : 8;
                Consider(Bytes(bits) + (count * 4), () => MakePalette(scan, (byte)bits, clean));
            }
        }

        if (image.PixelFormat is not (PixelFormat.L16 or PixelFormat.Rgb48 or PixelFormat.Rgba64) && chosen!.SameAs(image))
        {
            // Nothing to reduce: the direct path is the fast one.
            return Of(image, cleanTransparent);
        }

        return chosen!;
    }

    /// <summary>Writes row <paramref name="y"/> of the image in this layout.</summary>
    /// <param name="image">The image.</param>
    /// <param name="y">The row.</param>
    /// <param name="raw">Where the row goes: <see cref="RowBytes"/> bytes.</param>
    /// <param name="scratch">Room for a row of pixels, for the narrow mode.</param>
    public void WriteRow(Image image, int y, Span<byte> raw, Span<uint> scratch)
    {
        switch (_mode)
        {
            case Mode.Direct:
                WriteDirect(image, y, raw);
                break;
            case Mode.Wide:
                WriteWide(image, y, raw);
                break;
            default:
                ReadNarrow(image, y, scratch);
                if (_clean)
                {
                    Clean(scratch);
                }

                WriteNarrow(scratch, raw);
                break;
        }
    }

    private bool SameAs(Image image) => (ColourType, BitDepth, image.PixelFormat) switch
    {
        (0, 8, PixelFormat.L8) or (2, 8, PixelFormat.Rgb24) or (6, 8, PixelFormat.Rgba32 or PixelFormat.Bgra32) => Transparency is null,
        _ => false,
    };

    private void WriteDirect(Image image, int y, Span<byte> raw)
    {
        ReadOnlySpan<byte> pixels = image.GetRowBytes(y);
        if (BitDepth == 16)
        {
            // PNG samples are big-endian; the image's are in the machine's order.
            ReadOnlySpan<ushort> samples = MemoryMarshal.Cast<byte, ushort>(pixels);
            for (int i = 0; i < samples.Length; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(raw[(i * 2)..], samples[i]);
            }

            if (_clean)
            {
                for (int i = 0; i < raw.Length; i += 8)
                {
                    if (raw[i + 6] == 0 && raw[i + 7] == 0)
                    {
                        raw.Slice(i, 6).Clear();
                    }
                }
            }

            return;
        }

        if (image.PixelFormat == PixelFormat.Bgra32)
        {
            for (int i = 0; i < raw.Length; i += 4)
            {
                raw[i] = pixels[i + 2];
                raw[i + 1] = pixels[i + 1];
                raw[i + 2] = pixels[i];
                raw[i + 3] = pixels[i + 3];
            }
        }
        else
        {
            pixels.CopyTo(raw);
        }

        if (_clean)
        {
            for (int i = 0; i < raw.Length; i += 4)
            {
                if (raw[i + 3] == 0)
                {
                    raw.Slice(i, 3).Clear();
                }
            }
        }
    }

    private void WriteWide(Image image, int y, Span<byte> raw)
    {
        ReadOnlySpan<ushort> samples = MemoryMarshal.Cast<byte, ushort>(image.GetRowBytes(y));
        int channels = image.PixelFormat == PixelFormat.Rgba64 ? 4 : 3;
        int at = 0;
        for (int i = 0; i < samples.Length; i += channels)
        {
            bool hidden = _clean && channels == 4 && samples[i + 3] == 0;
            ushort r = hidden ? (ushort)0 : samples[i];
            switch (ColourType)
            {
                case 0:
                    BinaryPrimitives.WriteUInt16BigEndian(raw[at..], r);
                    at += 2;
                    break;
                case 4:
                    BinaryPrimitives.WriteUInt16BigEndian(raw[at..], r);
                    BinaryPrimitives.WriteUInt16BigEndian(raw[(at + 2)..], samples[i + 3]);
                    at += 4;
                    break;
                default:
                    BinaryPrimitives.WriteUInt16BigEndian(raw[at..], r);
                    BinaryPrimitives.WriteUInt16BigEndian(raw[(at + 2)..], hidden ? (ushort)0 : samples[i + 1]);
                    BinaryPrimitives.WriteUInt16BigEndian(raw[(at + 4)..], hidden ? (ushort)0 : samples[i + 2]);
                    at += 6;
                    break;
            }
        }
    }

    private void WriteNarrow(ReadOnlySpan<uint> pixels, Span<byte> raw)
    {
        switch (ColourType)
        {
            case 0 when BitDepth == 8:
                for (int i = 0; i < pixels.Length; i++)
                {
                    raw[i] = (byte)pixels[i];
                }

                break;
            case 0:
                Pack(pixels, raw, BitDepth, p => (int)(p & 0xFF) >> (8 - BitDepth));
                break;
            case 3:
            {
                uint last = ~pixels[0];
                int index = 0;
                if (BitDepth == 8)
                {
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        if (pixels[i] != last)
                        {
                            last = pixels[i];
                            index = IndexOf(last);
                        }

                        raw[i] = (byte)index;
                    }
                }
                else
                {
                    Pack(pixels, raw, BitDepth, p =>
                    {
                        if (p != last)
                        {
                            last = p;
                            index = IndexOf(p);
                        }

                        return index;
                    });
                }

                break;
            }

            case 4:
                for (int i = 0; i < pixels.Length; i++)
                {
                    raw[i * 2] = (byte)pixels[i];
                    raw[(i * 2) + 1] = (byte)(pixels[i] >> 24);
                }

                break;
            case 2:
                for (int i = 0; i < pixels.Length; i++)
                {
                    uint p = pixels[i];
                    raw[i * 3] = (byte)p;
                    raw[(i * 3) + 1] = (byte)(p >> 8);
                    raw[(i * 3) + 2] = (byte)(p >> 16);
                }

                break;
            default:
                for (int i = 0; i < pixels.Length; i++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(raw[(i * 4)..], pixels[i]);
                }

                break;
        }
    }

    private static void Pack(ReadOnlySpan<uint> pixels, Span<byte> raw, int bits, Func<uint, int> value)
    {
        raw.Clear();
        int perByte = 8 / bits;
        for (int i = 0; i < pixels.Length; i++)
        {
            int shift = 8 - (bits * ((i % perByte) + 1));
            raw[i / perByte] |= (byte)(value(pixels[i]) << shift);
        }
    }

    private int IndexOf(uint colour)
    {
        int slot = Slot(_keys!, _slots!, colour);
        return _slots![slot];
    }

    private static void Clean(Span<uint> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            if (pixels[i] >> 24 == 0)
            {
                pixels[i] = 0;
            }
        }
    }

    /// <summary>A row as RGBA, R in the low byte; 16-bit samples by their high byte.</summary>
    private static void ReadNarrow(Image image, int y, Span<uint> row)
    {
        ReadOnlySpan<byte> pixels = image.GetRowBytes(y);
        switch (image.PixelFormat)
        {
            case PixelFormat.L8:
                for (int i = 0; i < row.Length; i++)
                {
                    row[i] = (pixels[i] * 0x010101u) | 0xFF000000;
                }

                break;
            case PixelFormat.Rgb24:
                for (int i = 0; i < row.Length; i++)
                {
                    row[i] = pixels[i * 3] | ((uint)pixels[(i * 3) + 1] << 8) | ((uint)pixels[(i * 3) + 2] << 16) | 0xFF000000;
                }

                break;
            case PixelFormat.Rgba32:
                for (int i = 0; i < row.Length; i++)
                {
                    row[i] = BinaryPrimitives.ReadUInt32LittleEndian(pixels[(i * 4)..]);
                }

                break;
            case PixelFormat.Bgra32:
                for (int i = 0; i < row.Length; i++)
                {
                    row[i] = pixels[(i * 4) + 2] | ((uint)pixels[(i * 4) + 1] << 8) | ((uint)pixels[i * 4] << 16) | ((uint)pixels[(i * 4) + 3] << 24);
                }

                break;
            default:
            {
                ReadOnlySpan<ushort> samples = MemoryMarshal.Cast<byte, ushort>(pixels);
                int channels = image.PixelFormat switch { PixelFormat.L16 => 1, PixelFormat.Rgb48 => 3, _ => 4 };
                for (int i = 0; i < row.Length; i++)
                {
                    int at = i * channels;
                    uint r = (uint)samples[at] >> 8;
                    row[i] = channels switch
                    {
                        1 => (r * 0x010101u) | 0xFF000000,
                        3 => r | ((uint)(samples[at + 1] >> 8) << 8) | ((uint)(samples[at + 2] >> 8) << 16) | 0xFF000000,
                        _ => r | ((uint)(samples[at + 1] >> 8) << 8) | ((uint)(samples[at + 2] >> 8) << 16) | ((uint)(samples[at + 3] >> 8) << 24),
                    };
                }

                break;
            }
        }
    }

    // Whether every 16-bit sample is x * 257 (high byte equal to low), so 8 bits hold it; hidden colour aside when cleaning.
    private static bool FitsInEightBits(Image image, bool clean)
    {
        int channels = image.PixelFormat switch { PixelFormat.L16 => 1, PixelFormat.Rgb48 => 3, _ => 4 };
        for (int y = 0; y < image.Height; y++)
        {
            ReadOnlySpan<ushort> samples = MemoryMarshal.Cast<byte, ushort>(image.GetRowBytes(y));
            for (int i = 0; i < samples.Length; i += channels)
            {
                bool hidden = clean && samples[i + 3] == 0;
                for (int c = 0; c < channels; c++)
                {
                    ushort s = samples[i + c];
                    if ((s >> 8) != (s & 0xFF) && !(hidden && c < 3))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static PngLayout ChooseWide(Image image, bool allowGrey, bool clean)
    {
        bool hasAlpha = image.PixelFormat == PixelFormat.Rgba64;
        bool grey = allowGrey && image.PixelFormat != PixelFormat.L16;
        bool opaque = true;
        int channels = image.PixelFormat switch { PixelFormat.L16 => 1, PixelFormat.Rgb48 => 3, _ => 4 };
        for (int y = 0; y < image.Height && (grey || (hasAlpha && opaque)); y++)
        {
            ReadOnlySpan<ushort> samples = MemoryMarshal.Cast<byte, ushort>(image.GetRowBytes(y));
            for (int i = 0; i < samples.Length; i += channels)
            {
                bool hidden = clean && samples[i + 3] == 0;
                if (grey && !hidden && (samples[i] != samples[i + 1] || samples[i] != samples[i + 2]))
                {
                    grey = false;
                }

                if (hasAlpha && samples[i + 3] != 0xFFFF)
                {
                    opaque = false;
                }
            }
        }

        if (image.PixelFormat == PixelFormat.L16)
        {
            return Of(image, false);
        }

        byte colourType = (grey, opaque || !hasAlpha) switch
        {
            (true, true) => 0,
            (true, false) => 4,
            (false, true) => 2,
            _ => 6,
        };
        return colourType == 6 ? Of(image, clean) : new PngLayout(colourType, 16, Mode.Wide, clean);
    }

    private static bool KeyUsedOpaque(Image image, Scan scan, Span<uint> row)
    {
        if (scan.Opaque || !scan.BinaryAlpha || !scan.KeyUnique)
        {
            return false;
        }

        uint opaqueKey = scan.Key | 0xFF000000;
        if (scan.Colours <= 256)
        {
            return scan.Slots[Slot(scan.Keys, scan.Slots, opaqueKey)] >= 0;
        }

        for (int y = 0; y < image.Height; y++)
        {
            ReadNarrow(image, y, row);
            if (row.Contains(opaqueKey))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] GreyKey(uint value, int bits)
    {
        uint scaled = value >> (8 - bits);
        return [0, (byte)scaled];
    }

    private static byte[] RgbKey(uint key) => [0, (byte)key, 0, (byte)(key >> 8), 0, (byte)(key >> 16)];

    private static PngLayout MakePalette(Scan scan, byte bits, bool clean)
    {
        // Translucent entries first, so tRNS is short; then by how often each is used, then by value, for a stable order.
        int count = scan.Colours;
        var entries = new (uint Colour, int Uses)[count];
        int n = 0;
        for (int i = 0; i < TableSize; i++)
        {
            if (scan.Slots[i] >= 0)
            {
                entries[n++] = (scan.Keys[i], scan.Uses[scan.Slots[i]]);
            }
        }

        Array.Sort(entries, (a, b) =>
        {
            bool aOpaque = a.Colour >> 24 == 0xFF;
            bool bOpaque = b.Colour >> 24 == 0xFF;
            if (aOpaque != bOpaque)
            {
                return aOpaque ? 1 : -1;
            }

            int byUse = b.Uses.CompareTo(a.Uses);
            return byUse != 0 ? byUse : a.Colour.CompareTo(b.Colour);
        });

        byte[] palette = new byte[count * 3];
        int translucent = 0;
        uint[] keys = new uint[TableSize];
        short[] slots = new short[TableSize];
        slots.AsSpan().Fill(-1);
        for (int i = 0; i < count; i++)
        {
            uint c = entries[i].Colour;
            palette[i * 3] = (byte)c;
            palette[(i * 3) + 1] = (byte)(c >> 8);
            palette[(i * 3) + 2] = (byte)(c >> 16);
            if (c >> 24 != 0xFF)
            {
                translucent = i + 1;
            }

            int slot = Slot(keys, slots, c);
            keys[slot] = c;
            slots[slot] = (short)i;
        }

        byte[]? transparency = null;
        if (translucent > 0)
        {
            transparency = new byte[translucent];
            for (int i = 0; i < translucent; i++)
            {
                transparency[i] = (byte)(entries[i].Colour >> 24);
            }
        }

        return new PngLayout(3, bits, Mode.Narrow, clean, palette, transparency, keys, slots);
    }

    // Open addressing: the slot holding the colour, or the empty slot where it would go.
    private static int Slot(uint[] keys, short[] slots, uint colour)
    {
        int slot = (int)((colour * 0x9E3779B1u) >> 22);
        while (slots[slot] >= 0 && keys[slot] != colour)
        {
            slot = (slot + 1) & (TableSize - 1);
        }

        return slot;
    }

    /// <summary>What a pass over the pixels has found so far.</summary>
    private sealed class Scan
    {
        private readonly bool _hasAlpha;
        private readonly bool _clean;
        private bool _counting;
        private bool _haveKey;
        private uint _last;
        private int _lastSlot = -1;

        public Scan(bool hasAlpha, bool clean, bool countColours)
        {
            _hasAlpha = hasAlpha;
            _clean = clean;
            _counting = countColours;
            Opaque = true;
            BinaryAlpha = true;
            Grey = true;
            KeyUnique = true;
            GreyBits = 1;
            Slots.AsSpan().Fill(-1);
            Colours = countColours ? 0 : int.MaxValue;
        }

        public bool Grey { get; private set; }

        public int GreyBits { get; private set; }

        public bool Opaque { get; private set; }

        public bool BinaryAlpha { get; private set; }

        public bool KeyUnique { get; private set; }

        /// <summary>The colour (RGB, alpha zero) of the transparent pixels, when they share one.</summary>
        public uint Key { get; private set; }

        /// <summary>Distinct colours, or more than 256 once there are too many to count.</summary>
        public int Colours { get; private set; }

        public uint[] Keys { get; } = new uint[TableSize];

        public short[] Slots { get; } = new short[TableSize];

        public int[] Uses { get; } = new int[257];

        /// <summary>Whether nothing more can change the layout.</summary>
        public bool Settled => !Grey && !_counting && (!_hasAlpha || !BinaryAlpha || !KeyUnique);

        public void Add(Span<uint> row)
        {
            for (int i = 0; i < row.Length; i++)
            {
                uint p = row[i];
                if (_hasAlpha)
                {
                    uint a = p >> 24;
                    if (a != 0xFF)
                    {
                        Opaque = false;
                        if (a != 0)
                        {
                            BinaryAlpha = false;
                        }
                        else
                        {
                            if (_clean)
                            {
                                p = row[i] = 0;
                            }

                            if (!_haveKey)
                            {
                                Key = p;
                                _haveKey = true;
                            }
                            else if (p != Key)
                            {
                                KeyUnique = false;
                            }
                        }
                    }
                }

                if (Grey)
                {
                    uint r = p & 0xFF;
                    if (r != ((p >> 8) & 0xFF) || r != ((p >> 16) & 0xFF))
                    {
                        Grey = false;
                    }
                    else if (GreyBits < 8)
                    {
                        int need = r % 255 == 0 ? 1 : r % 85 == 0 ? 2 : r % 17 == 0 ? 4 : 8;
                        GreyBits = Math.Max(GreyBits, need);
                    }
                }

                if (_counting)
                {
                    if (p != _last || _lastSlot < 0)
                    {
                        int slot = Slot(Keys, Slots, p);
                        if (Slots[slot] < 0)
                        {
                            if (Colours == 256)
                            {
                                _counting = false;
                                Colours = int.MaxValue;
                                continue;
                            }

                            Keys[slot] = p;
                            Slots[slot] = (short)Colours++;
                        }

                        _last = p;
                        _lastSlot = Slots[slot];
                    }

                    Uses[_lastSlot]++;
                }
            }
        }
    }
}
