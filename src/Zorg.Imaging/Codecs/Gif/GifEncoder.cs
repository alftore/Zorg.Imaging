using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Metadata;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Codecs.Gif;

/// <summary>Writes a still GIF89a: one frame with a global colour table, transparency and an ICC profile as needed.</summary>
internal static class GifEncoder
{
    /// <summary>The largest side a GIF can hold.</summary>
    public const int MaxSide = 65535;

    public static async ValueTask EncodeAsync(Image image, Stream stream, GifEncoderOptions options, bool sync, CancellationToken cancellationToken)
    {
        if (image.Width > MaxSide || image.Height > MaxSide)
        {
            throw new NotSupportedException(string.Create(
                CultureInfo.InvariantCulture,
                $"GIF can't hold a {image.Width} x {image.Height} image: each side must be at most {MaxSide} pixels."));
        }

        ReadOnlyMemory<byte> profile = IccProfileWriter.For(image.ColorSpace, "GIF");
        byte[] file;
        using (Parallelism.Use(options.MaxDegreeOfParallelism))
        {
            (uint[] argb, _) = Argb.FromImage(image);
            Palettizer.Result result = Palettizer.Run(argb, image.Width, image.Height, options.MaxColors, options.Dithering);
            file = Write(image.Width, image.Height, result, profile.Span);
        }

        if (sync)
        {
            stream.Write(file);
        }
        else
        {
            await stream.WriteAsync(file, cancellationToken).ConfigureAwait(false);
        }
    }

    private static byte[] Write(int width, int height, Palettizer.Result result, ReadOnlySpan<byte> profile)
    {
        int bits = 1;
        while (1 << bits < result.Palette.Length)
        {
            bits++;
        }

        var output = new ArrayBufferWriter<byte>(1024 + (width * height / 2));
        Put(output, "GIF89a"u8);
        Put(output, [(byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), (byte)(0x80 | 0x70 | (bits - 1)), 0, 0]);
        Span<byte> table = output.GetSpan(3 << bits)[..(3 << bits)];
        table.Clear();
        for (int i = 0; i < result.Palette.Length; i++)
        {
            uint p = result.Palette[i];
            table[3 * i] = (byte)(p >> 16);
            table[(3 * i) + 1] = (byte)(p >> 8);
            table[(3 * i) + 2] = (byte)p;
        }

        output.Advance(3 << bits);

        if (!profile.IsEmpty)
        {
            Put(output, [0x21, 0xFF, 11]);
            Put(output, "ICCRGBG1012"u8);
            SubBlocks(output, profile);
        }

        if (result.Transparent >= 0)
        {
            Put(output, [0x21, 0xF9, 4, 1, 0, 0, (byte)result.Transparent, 0]);
        }

        Put(output, [0x2C, 0, 0, 0, 0, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0]);
        int codeSize = Math.Max(2, bits);
        Put(output, [(byte)codeSize]);
        GifLzw.Encode(result.Indices, codeSize, output);
        Put(output, [0x3B]);
        return output.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The start of an animated GIF: the header, a logical screen with no global colour table, the ICC profile when
    /// given, and the NETSCAPE2.0 loop extension unless the animation plays once.
    /// </summary>
    /// <param name="output">Where the bytes go.</param>
    /// <param name="width">The canvas width.</param>
    /// <param name="height">The canvas height.</param>
    /// <param name="profile">The ICC profile, or empty.</param>
    /// <param name="repeats">How many times to repeat after the first play, 0 for ever, or -1 to play once.</param>
    internal static void WriteAnimationHeader(ArrayBufferWriter<byte> output, int width, int height, ReadOnlySpan<byte> profile, int repeats)
    {
        Put(output, "GIF89a"u8);
        Put(output, [(byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0x70, 0, 0]);
        if (!profile.IsEmpty)
        {
            Put(output, [0x21, 0xFF, 11]);
            Put(output, "ICCRGBG1012"u8);
            SubBlocks(output, profile);
        }

        if (repeats >= 0)
        {
            Put(output, [0x21, 0xFF, 11]);
            Put(output, "NETSCAPE2.0"u8);
            Put(output, [3, 1, (byte)repeats, (byte)(repeats >> 8), 0]);
        }
    }

    /// <summary>
    /// One frame of an animation: a graphic control extension with its delay (hundredths of a second), disposal method
    /// and transparent index, then the image with its own colour table.
    /// </summary>
    internal static void WriteAnimationFrame(ArrayBufferWriter<byte> output, Palettizer.Result result, int left, int top, int width, int height, int delay, int disposal)
    {
        int bits = 1;
        while (1 << bits < result.Palette.Length)
        {
            bits++;
        }

        byte packed = (byte)((disposal << 2) | (result.Transparent >= 0 ? 1 : 0));
        Put(output, [0x21, 0xF9, 4, packed, (byte)delay, (byte)(delay >> 8), (byte)Math.Max(result.Transparent, 0), 0]);
        Put(output, [0x2C, (byte)left, (byte)(left >> 8), (byte)top, (byte)(top >> 8), (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), (byte)(0x80 | (bits - 1))]);
        Span<byte> table = output.GetSpan(3 << bits)[..(3 << bits)];
        table.Clear();
        for (int i = 0; i < result.Palette.Length; i++)
        {
            uint p = result.Palette[i];
            table[3 * i] = (byte)(p >> 16);
            table[(3 * i) + 1] = (byte)(p >> 8);
            table[(3 * i) + 2] = (byte)p;
        }

        output.Advance(3 << bits);
        int codeSize = Math.Max(2, bits);
        Put(output, [(byte)codeSize]);
        GifLzw.Encode(result.Indices, codeSize, output);
    }

    private static void Put(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> bytes) => output.Write(bytes);

    private static void SubBlocks(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> data)
    {
        for (int at = 0; at < data.Length; at += 255)
        {
            int size = Math.Min(255, data.Length - at);
            Span<byte> block = output.GetSpan(size + 1);
            block[0] = (byte)size;
            data.Slice(at, size).CopyTo(block[1..]);
            output.Advance(size + 1);
        }

        Put(output, [0]);
    }
}
