using System;
using System.Globalization;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Codecs.Jpeg;

/// <summary>A frame (ITU-T T.81, B.2.2): the image size and its components, with each component's coefficients.</summary>
internal sealed class JpegFrame
{
    private JpegFrame(int width, int height, bool progressive, JpegComponent[] components)
    {
        Width = width;
        Height = height;
        Progressive = progressive;
        Components = components;
        foreach (JpegComponent component in components)
        {
            MaxH = Math.Max(MaxH, component.H);
            MaxV = Math.Max(MaxV, component.V);
        }

        McusWide = DivideUp(width, 8 * MaxH);
        McusHigh = DivideUp(height, 8 * MaxV);
    }

    public int Width { get; }

    public int Height { get; }

    public bool Progressive { get; }

    public JpegComponent[] Components { get; }

    public int MaxH { get; }

    public int MaxV { get; }

    public int McusWide { get; }

    public int McusHigh { get; }

    /// <summary>Every block in every component, which bounds the useful size of the image data.</summary>
    public long TotalBlocks { get; private set; }

    /// <summary>Reads a frame header.</summary>
    /// <param name="data">The segment after its length.</param>
    /// <param name="progressive">Whether the marker was SOF2.</param>
    /// <param name="limits">The limits to check before anything is allocated.</param>
    /// <returns>The frame, with room for every coefficient.</returns>
    public static JpegFrame Parse(ReadOnlySpan<byte> data, bool progressive, ImageLimits limits)
    {
        if (data.Length < 6)
        {
            throw ImageErrors.Corrupt("The JPEG frame header is too short.");
        }

        int precision = data[0];
        int height = (data[1] << 8) | data[2];
        int width = (data[3] << 8) | data[4];
        int count = data[5];
        if (precision != 8)
        {
            throw new UnsupportedImageFormatException(
                string.Create(CultureInfo.InvariantCulture, $"{precision}-bit JPEG isn't supported; only 8-bit samples are."), ImageFormat.Jpeg);
        }

        if (height == 0)
        {
            throw new UnsupportedImageFormatException("The JPEG file declares its height after the image data (DNL marker), which isn't supported.", ImageFormat.Jpeg);
        }

        if (width == 0)
        {
            throw ImageErrors.Corrupt("The JPEG file has a width of 0.");
        }

        if (count == 4)
        {
            throw new UnsupportedImageFormatException("CMYK JPEG isn't supported yet.", ImageFormat.Jpeg);
        }

        if (count is not (1 or 3))
        {
            throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The JPEG file has {count} colour components; only 1 (grey) or 3 (colour) make sense."));
        }

        if (data.Length != 6 + (3 * count))
        {
            throw ImageErrors.Corrupt("The JPEG frame header's length doesn't match its component count.");
        }

        long pixels = (long)width * height;
        if (pixels > limits.MaxPixels)
        {
            throw ImageErrors.ExceedsLimits(string.Create(
                CultureInfo.InvariantCulture,
                $"The JPEG image is {width} x {height}, {pixels} pixels, more than the {limits.MaxPixels}-pixel limit (ImageLimits.MaxPixels)."));
        }

        var components = new JpegComponent[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> c = data.Slice(6 + (3 * i), 3);
            int h = c[1] >> 4;
            int v = c[1] & 15;
            if (h is < 1 or > 4 || v is < 1 or > 4 || c[2] > 3)
            {
                throw ImageErrors.Corrupt("A JPEG component has invalid sampling factors or quantisation table.");
            }

            for (int j = 0; j < i; j++)
            {
                if (components[j].Id == c[0])
                {
                    throw ImageErrors.Corrupt("Two JPEG components have the same identifier.");
                }
            }

            components[i] = new JpegComponent(c[0], h, v, c[2]);
        }

        var frame = new JpegFrame(width, height, progressive, components);
        foreach (JpegComponent component in components)
        {
            if (frame.MaxH % component.H != 0 || frame.MaxV % component.V != 0)
            {
                throw new UnsupportedImageFormatException("The JPEG file's chroma sampling isn't a whole fraction of the image size, which isn't supported.", ImageFormat.Jpeg);
            }

            component.Allocate(frame);
            frame.TotalBlocks += (long)component.BlocksWide * component.BlocksHigh;
        }

        return frame;
    }

    /// <summary>Ceiling division for positive values.</summary>
    public static int DivideUp(long value, long by) => (int)((value + by - 1) / by);
}

/// <summary>One colour component of a frame.</summary>
internal sealed class JpegComponent(int id, int h, int v, int quantisationTable)
{
    public int Id { get; } = id;

    public int H { get; } = h;

    public int V { get; } = v;

    public int QuantisationTable { get; } = quantisationTable;

    /// <summary>Blocks across and down, padded to whole MCUs.</summary>
    public int BlocksWide { get; private set; }

    public int BlocksHigh { get; private set; }

    /// <summary>Samples across and down at the component's own resolution (T.81, A.1.1).</summary>
    public int SamplesWide { get; private set; }

    public int SamplesHigh { get; private set; }

    /// <summary>Blocks that hold samples; a scan of this component alone covers just these.</summary>
    public int UsedWide => JpegFrame.DivideUp(SamplesWide, 8);

    public int UsedHigh => JpegFrame.DivideUp(SamplesHigh, 8);

    /// <summary>Every block's 64 coefficients in zigzag order, as decoded (not yet multiplied by the quantisation).</summary>
    public short[] Coefficients { get; private set; } = [];

    /// <summary>The quantisation table, copied when the component's first scan starts.</summary>
    public ushort[]? Quantisation { get; set; }

    public int DcPredictor { get; set; }

    public JpegHuffman? Dc { get; set; }

    public JpegHuffman? Ac { get; set; }

    public void Allocate(JpegFrame frame)
    {
        BlocksWide = frame.McusWide * H;
        BlocksHigh = frame.McusHigh * V;
        SamplesWide = JpegFrame.DivideUp((long)frame.Width * H, frame.MaxH);
        SamplesHigh = JpegFrame.DivideUp((long)frame.Height * V, frame.MaxV);
        long count = (long)BlocksWide * BlocksHigh * 64;
        if (count > Array.MaxLength)
        {
            throw ImageErrors.ExceedsLimits("The JPEG image is too large to hold in memory.");
        }

        Coefficients = new short[count];
    }

    public Span<short> Block(int x, int y) => Coefficients.AsSpan(((y * BlocksWide) + x) * 64, 64);
}
