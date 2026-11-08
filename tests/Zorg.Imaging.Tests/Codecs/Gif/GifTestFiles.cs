using System;
using System.Buffers;
using System.Collections.Generic;
using Zorg.Imaging.Codecs.Gif;

namespace Zorg.Imaging.Tests.Codecs.Gif;

/// <summary>Builds GIF files block by block for the decoder tests.</summary>
internal static class GifTestFiles
{
    /// <summary>A file from its blocks, after the signature.</summary>
    public static byte[] File(params byte[][] blocks)
    {
        var data = new List<byte>("GIF89a"u8.ToArray());
        foreach (byte[] block in blocks)
        {
            data.AddRange(block);
        }

        return [.. data];
    }

    /// <summary>The logical screen descriptor, with a global colour table when one is given (2 to 256 RGB entries).</summary>
    public static byte[] Screen(int width, int height, byte[]? palette = null)
    {
        var data = new List<byte> { (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8) };
        if (palette is null)
        {
            data.AddRange([0, 0, 0]);
        }
        else
        {
            int bits = TableBits(palette.Length / 3);
            data.AddRange([(byte)(0x80 | 0x70 | (bits - 1)), 0, 0]);
            data.AddRange(Padded(palette, bits));
        }

        return [.. data];
    }

    /// <summary>A graphic control extension.</summary>
    public static byte[] Control(int disposal = 0, int transparent = -1, int delay = 0) =>
        [0x21, 0xF9, 4, (byte)((disposal << 2) | (transparent >= 0 ? 1 : 0)), (byte)delay, (byte)(delay >> 8), (byte)Math.Max(transparent, 0), 0];

    /// <summary>An image: descriptor, optional local colour table, and its indices LZW-coded in sub-blocks.</summary>
    public static byte[] Frame(int left, int top, int width, int height, byte[] indices, byte[]? palette = null, bool interlaced = false, int codeSize = 8)
    {
        var data = new List<byte> { 0x2C, (byte)left, (byte)(left >> 8), (byte)top, (byte)(top >> 8), (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8) };
        byte packed = interlaced ? (byte)0x40 : (byte)0;
        if (palette is not null)
        {
            int bits = TableBits(palette.Length / 3);
            data.Add((byte)(packed | 0x80 | (bits - 1)));
            data.AddRange(Padded(palette, bits));
        }
        else
        {
            data.Add(packed);
        }

        byte[] stored = indices;
        if (interlaced)
        {
            stored = new byte[indices.Length];
            for (int s = 0; s < height; s++)
            {
                indices.AsSpan(GifReader.InterlacedRow(s, height) * width, width).CopyTo(stored.AsSpan(s * width));
            }
        }

        var lzw = new ArrayBufferWriter<byte>();
        GifLzw.Encode(stored, codeSize, lzw);
        data.Add((byte)codeSize);
        data.AddRange(lzw.WrittenSpan.ToArray());
        return [.. data];
    }

    /// <summary>Sub-blocks joined, without their lengths: up to the empty block.</summary>
    public static byte[] Joined(ReadOnlySpan<byte> blocks)
    {
        var result = new List<byte>();
        for (int at = 0; blocks[at] != 0; at += 1 + blocks[at])
        {
            result.AddRange(blocks.Slice(at + 1, blocks[at]).ToArray());
        }

        return [.. result];
    }

    /// <summary>Data as sub-blocks of up to 255 bytes, then the terminator.</summary>
    public static byte[] SubBlocks(ReadOnlySpan<byte> data)
    {
        var result = new List<byte>();
        for (int at = 0; at < data.Length; at += 255)
        {
            int size = Math.Min(255, data.Length - at);
            result.Add((byte)size);
            result.AddRange(data.Slice(at, size).ToArray());
        }

        result.Add(0);
        return [.. result];
    }

    public static byte[] Trailer => [0x3B];

    private static int TableBits(int entries)
    {
        int bits = 1;
        while (1 << bits < entries)
        {
            bits++;
        }

        return bits;
    }

    private static byte[] Padded(byte[] palette, int bits)
    {
        byte[] table = new byte[3 << bits];
        palette.CopyTo(table, 0);
        return table;
    }
}
