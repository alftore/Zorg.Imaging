using System;
using System.Runtime.InteropServices;
using System.Linq;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Zorg.Imaging.Codecs.Png;

namespace Zorg.Imaging.Tests.Codecs.Png;

/// <summary>
/// Builds PNG files for tests from raw samples, independently of the library's encoder: its own filters, its own
/// Adam7 split, and the expected 8-bit RGBA worked out from the samples.
/// </summary>
internal static class PngTestFiles
{
    private static readonly int[][] Adam7 =
    [
        [0, 0, 8, 8], [4, 0, 8, 8], [0, 4, 4, 8], [2, 0, 4, 4], [0, 2, 2, 4], [1, 0, 2, 2], [0, 1, 1, 2],
    ];

    public static int Channels(int colourType) => colourType switch
    {
        2 => 3,
        4 => 2,
        6 => 4,
        _ => 1,
    };

    /// <summary>Random samples for every pixel, seeded, within the depth (and within the palette for colour type 3).</summary>
    public static int[] RandomSamples(int width, int height, int depth, int colourType, int seed, int paletteSize = 0)
    {
        var random = new Random(seed);
        int max = colourType == 3 ? paletteSize : 1 << depth;
        int[] samples = new int[width * height * Channels(colourType)];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = random.Next(max);
        }

        return samples;
    }

    /// <summary>The IDAT payload rows for the samples: filtered rows of each pass, filter types cycling 0 to 4.</summary>
    public static byte[] ImageData(int width, int height, int depth, int colourType, int[] samples, bool interlaced)
    {
        int channels = Channels(colourType);
        int bitsPerPixel = channels * depth;
        int filterBpp = Math.Max(1, bitsPerPixel / 8);
        using var raw = new MemoryStream();
        int filterCycle = 0;
        IEnumerable<int[]> passes = interlaced ? Adam7 : [[0, 0, 1, 1]];
        foreach (int[] pass in passes)
        {
            int passWidth = width > pass[0] ? (width - pass[0] + pass[2] - 1) / pass[2] : 0;
            int passHeight = height > pass[1] ? (height - pass[1] + pass[3] - 1) / pass[3] : 0;
            if (passWidth == 0 || passHeight == 0)
            {
                continue;
            }

            int rowBytes = ((passWidth * bitsPerPixel) + 7) / 8;
            byte[] previous = new byte[rowBytes];
            for (int r = 0; r < passHeight; r++)
            {
                int y = pass[1] + (r * pass[3]);
                byte[] row = new byte[rowBytes];
                for (int i = 0; i < passWidth; i++)
                {
                    int x = pass[0] + (i * pass[2]);
                    for (int c = 0; c < channels; c++)
                    {
                        PutSample(row, (i * channels) + c, depth, samples[(((y * width) + x) * channels) + c]);
                    }
                }

                int filter = filterCycle++ % 5;
                raw.WriteByte((byte)filter);
                raw.Write(ApplyFilter(filter, row, previous, filterBpp));
                previous = row;
            }
        }

        return raw.ToArray();
    }

    /// <summary>A complete PNG: IHDR, the extra chunks before the image data, one IDAT, IEND.</summary>
    public static byte[] Build(int width, int height, int depth, int colourType, int[] samples, bool interlaced = false, params (string Type, byte[] Data)[] before) =>
        Assemble(Ihdr(width, height, depth, colourType, interlaced), before, [Compress(ImageData(width, height, depth, colourType, samples, interlaced))]);

    public static byte[] Ihdr(int width, int height, int depth, int colourType, bool interlaced = false)
    {
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = (byte)depth;
        ihdr[9] = (byte)colourType;
        ihdr[12] = interlaced ? (byte)1 : (byte)0;
        return ihdr;
    }

    /// <summary>Signature, IHDR, the chunks before, IDAT chunks with the given payloads, IEND.</summary>
    public static byte[] Assemble(byte[] ihdr, (string Type, byte[] Data)[] before, byte[][] idats, params (string Type, byte[] Data)[] after)
    {
        using var file = new MemoryStream();
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        file.Write(Chunk("IHDR", ihdr));
        foreach ((string type, byte[] data) in before)
        {
            file.Write(Chunk(type, data));
        }

        foreach (byte[] idat in idats)
        {
            file.Write(Chunk("IDAT", idat));
        }

        foreach ((string type, byte[] data) in after)
        {
            file.Write(Chunk(type, data));
        }

        file.Write(Chunk("IEND", []));
        return file.ToArray();
    }

    public static byte[] Chunk(string type, byte[] data)
    {
        byte[] chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32.Compute(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    public static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>The 8-bit RGBA each pixel should decode to.</summary>
    public static byte[] ExpectedRgba(int width, int height, int depth, int colourType, int[] samples, byte[]? palette = null, byte[]? trns = null)
    {
        int[] channels = ExpectedChannels(width, height, depth, colourType, samples, palette, trns, sixteen: false);
        return [.. channels.Select(c => (byte)c)];
    }

    /// <summary>The 16-bit RGBA each pixel should decode to, as the bytes of an Rgba64 image.</summary>
    public static byte[] ExpectedRgba64(int width, int height, int depth, int colourType, int[] samples, byte[]? palette = null, byte[]? trns = null)
    {
        int[] channels = ExpectedChannels(width, height, depth, colourType, samples, palette, trns, sixteen: true);
        return MemoryMarshal.AsBytes(channels.Select(c => (ushort)c).ToArray().AsSpan()).ToArray();
    }

    /// <summary>Keeps the first <paramref name="channels"/> of every four channels of <paramref name="bytesPerChannel"/> bytes.</summary>
    public static byte[] Narrow(byte[] rgba, int channels, int bytesPerChannel = 1)
    {
        int pixel = 4 * bytesPerChannel;
        int keep = channels * bytesPerChannel;
        byte[] narrow = new byte[rgba.Length / pixel * keep];
        for (int p = 0; p < rgba.Length / pixel; p++)
        {
            rgba.AsSpan(p * pixel, keep).CopyTo(narrow.AsSpan(p * keep));
        }

        return narrow;
    }

    /// <summary>The 16-bit value: <c>v * 65535 / max</c>, exact for every PNG depth.</summary>
    public static int Scale16(int value, int depth) => (int)(value * 65535L / ((1 << depth) - 1));

    private static int[] ExpectedChannels(int width, int height, int depth, int colourType, int[] samples, byte[]? palette, byte[]? trns, bool sixteen)
    {
        int Fit(int value, int bits) => sixteen ? Scale16(value, bits) : Scale(value, bits);
        int opaque = sixteen ? 65535 : 255;
        int channels = Channels(colourType);
        int[] rgba = new int[width * height * 4];
        for (int p = 0; p < width * height; p++)
        {
            ReadOnlySpan<int> s = samples.AsSpan(p * channels, channels);
            int r, g, b, a;
            switch (colourType)
            {
                case 0:
                    r = g = b = Fit(s[0], depth);
                    a = trns is not null && s[0] == BinaryPrimitives.ReadUInt16BigEndian(trns) ? 0 : opaque;
                    break;
                case 2:
                    (r, g, b) = (Fit(s[0], depth), Fit(s[1], depth), Fit(s[2], depth));
                    a = trns is not null
                        && s[0] == BinaryPrimitives.ReadUInt16BigEndian(trns)
                        && s[1] == BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(2))
                        && s[2] == BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(4)) ? 0 : opaque;
                    break;
                case 3:
                    bool inPalette = s[0] * 3 < palette!.Length;
                    r = inPalette ? Fit(palette[s[0] * 3], 8) : 0;
                    g = inPalette ? Fit(palette[(s[0] * 3) + 1], 8) : 0;
                    b = inPalette ? Fit(palette[(s[0] * 3) + 2], 8) : 0;
                    a = trns is not null && s[0] < trns.Length ? Fit(trns[s[0]], 8) : opaque;
                    break;
                case 4:
                    r = g = b = Fit(s[0], depth);
                    a = Fit(s[1], depth);
                    break;
                default:
                    (r, g, b, a) = (Fit(s[0], depth), Fit(s[1], depth), Fit(s[2], depth), Fit(s[3], depth));
                    break;
            }

            rgba[p * 4] = r;
            rgba[(p * 4) + 1] = g;
            rgba[(p * 4) + 2] = b;
            rgba[(p * 4) + 3] = a;
        }

        return rgba;
    }

    /// <summary>The nearest 8-bit value: <c>round(v * 255 / max)</c>.</summary>
    public static byte Scale(int value, int depth)
    {
        int max = (1 << depth) - 1;
        return (byte)Math.Round(value * 255.0 / max, MidpointRounding.AwayFromZero);
    }

    /// <summary>An EXIF block (big-endian TIFF) holding only an Orientation tag.</summary>
    public static byte[] ExifWithOrientation(int orientation)
    {
        byte[] exif = new byte[8 + 2 + 12 + 4];
        "MM"u8.CopyTo(exif);
        BinaryPrimitives.WriteUInt16BigEndian(exif.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32BigEndian(exif.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16BigEndian(exif.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(exif.AsSpan(10), 0x0112);
        BinaryPrimitives.WriteUInt16BigEndian(exif.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32BigEndian(exif.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16BigEndian(exif.AsSpan(18), (ushort)orientation);
        return exif;
    }

    /// <summary>The offsets of every chunk: (offset of the length field, type).</summary>
    public static List<(int Offset, string Type)> Chunks(byte[] png)
    {
        var chunks = new List<(int, string)>();
        int at = 8;
        while (at + 8 <= png.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            chunks.Add((at, Encoding.ASCII.GetString(png, at + 4, 4)));
            at += 12 + length;
        }

        return chunks;
    }

    public static byte[] Pixels(Image image) => image.GetPixelBytes().ToArray();

    private static void PutSample(byte[] row, int index, int depth, int value)
    {
        switch (depth)
        {
            case 8:
                row[index] = (byte)value;
                break;
            case 16:
                BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(index * 2), (ushort)value);
                break;
            default:
                int bit = index * depth;
                row[bit / 8] |= (byte)(value << (8 - depth - (bit % 8)));
                break;
        }
    }

    private static byte[] ApplyFilter(int filter, byte[] row, byte[] previous, int bpp)
    {
        byte[] output = new byte[row.Length];
        for (int i = 0; i < row.Length; i++)
        {
            int a = i >= bpp ? row[i - bpp] : 0;
            int b = previous[i];
            int c = i >= bpp ? previous[i - bpp] : 0;
            int predicted = filter switch
            {
                0 => 0,
                1 => a,
                2 => b,
                3 => (a + b) / 2,
                _ => PaethReference(a, b, c),
            };
            output[i] = (byte)(row[i] - predicted);
        }

        return output;
    }

    private static int PaethReference(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
