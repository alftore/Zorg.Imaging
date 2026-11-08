using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Metadata;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Codecs.Webp;

/// <summary>Writes a WebP file (RFC 9649): a RIFF container around a VP8L stream or a VP8 frame, with VP8X, ICCP and ALPH as needed.</summary>
internal static class WebpEncoder
{
    public static async ValueTask EncodeAsync(Image image, Stream stream, WebpEncoderOptions options, bool sync, CancellationToken cancellationToken)
    {
        int most = options.Lossless ? Vp8lEncoder.MaxSide : MaxLossySide;
        if (image.Width > most || image.Height > most)
        {
            throw new NotSupportedException(string.Create(
                CultureInfo.InvariantCulture,
                $"{(options.Lossless ? "Lossless" : "Lossy")} WebP can't hold a {image.Width} x {image.Height} image: each side must be at most {most} pixels."));
        }

        ReadOnlyMemory<byte> profile = IccProfileWriter.For(image.ColorSpace, "WebP");
        byte[] file;
        using (Parallelism.Use(options.MaxDegreeOfParallelism))
        {
            (uint[] argb, bool alpha) = ToArgb(image);
            if (options.Lossless)
            {
                byte[] vp8l = Vp8lEncoder.Encode(argb, image.Width, image.Height, alpha, options.Effort);
                file = Container("VP8L"u8, vp8l, [], image.Width, image.Height, alpha, profile.Span);
            }
            else
            {
                (byte[] y, byte[] u, byte[] v) = ToYuv(argb, image.Width, image.Height);
                byte[] vp8 = Vp8Encoder.Encode(y, u, v, image.Width, image.Height, Vp8Encoder.QuantiserFor(options.Quality), options.Effort);
                byte[] alphaChunk = alpha ? AlphaChunk(argb, image.Width, image.Height, options.Effort) : [];
                file = Container("VP8 "u8, vp8, alphaChunk, image.Width, image.Height, alpha, profile.Span);
            }
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

    /// <summary>The pixels as ARGB words (alpha in the top byte), 16-bit samples rounded to 8, and whether any isn't opaque.</summary>
    internal static (uint[] Argb, bool Alpha) ToArgb(Image image)
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

    /// <summary>The largest side a VP8 frame header can hold.</summary>
    private const int MaxLossySide = 16383;

    // RGB to Y'CbCr with BT.601's studio-range coefficients, as VP8 decoders convert back (RFC 6386 leaves the colour
    // space to the container; WebP uses this one). Chroma is the 2 x 2 average, at half size. The planes are padded to
    // whole macroblocks by repeating the last row and column, so the padding costs few bits.
    internal static (byte[] Y, byte[] U, byte[] V) ToYuv(uint[] argb, int width, int height)
    {
        int mbWide = (width + 15) / 16;
        int mbHigh = (height + 15) / 16;
        int strideY = mbWide * 16;
        int strideUv = mbWide * 8;
        byte[] y = new byte[strideY * mbHigh * 16];
        byte[] u = new byte[strideUv * mbHigh * 8];
        byte[] v = new byte[strideUv * mbHigh * 8];
        for (int row = 0; row < mbHigh * 16; row++)
        {
            int sy = Math.Min(row, height - 1);
            for (int col = 0; col < strideY; col++)
            {
                uint p = argb[(sy * width) + Math.Min(col, width - 1)];
                int r = (int)((p >> 16) & 0xFF);
                int g = (int)((p >> 8) & 0xFF);
                int b = (int)(p & 0xFF);
                y[(row * strideY) + col] = (byte)(((16839 * r) + (33059 * g) + (6420 * b) + (16 << 16) + (1 << 15)) >> 16);
            }
        }

        for (int row = 0; row < mbHigh * 8; row++)
        {
            for (int col = 0; col < strideUv; col++)
            {
                int r = 0;
                int g = 0;
                int b = 0;
                for (int dy = 0; dy < 2; dy++)
                {
                    int sy = Math.Min((row * 2) + dy, height - 1);
                    for (int dx = 0; dx < 2; dx++)
                    {
                        uint p = argb[(sy * width) + Math.Min((col * 2) + dx, width - 1)];
                        r += (int)((p >> 16) & 0xFF);
                        g += (int)((p >> 8) & 0xFF);
                        b += (int)(p & 0xFF);
                    }
                }

                u[(row * strideUv) + col] = (byte)Math.Clamp(((-9719 * r) - (19081 * g) + (28800 * b) + (128 << 18) + (1 << 17)) >> 18, 0, 255);
                v[(row * strideUv) + col] = (byte)Math.Clamp(((28800 * r) - (24116 * g) - (4684 * b) + (128 << 18) + (1 << 17)) >> 18, 0, 255);
            }
        }

        return (y, u, v);
    }

    // An ALPH chunk's payload: a header byte (no preprocessing, no filter, lossless compression) and the alpha as the
    // green channel of a VP8L image stream without its 5-byte header.
    private static byte[] AlphaChunk(uint[] argb, int width, int height, int effort)
    {
        uint[] alpha = new uint[argb.Length];
        for (int i = 0; i < argb.Length; i++)
        {
            alpha[i] = 0xFF000000u | ((argb[i] >> 24) << 8);
        }

        byte[] stream = Vp8lEncoder.Encode(alpha, width, height, alpha: false, effort);
        byte[] chunk = new byte[stream.Length - 4];
        chunk[0] = 1;
        stream.AsSpan(5).CopyTo(chunk.AsSpan(1));
        return chunk;
    }

    // RIFF, then VP8X (when there is a profile, or alpha in a lossy file), ICCP and ALPH as needed, then the image chunk.
    // Chunks are padded to an even length.
    private static byte[] Container(ReadOnlySpan<byte> fourCc, byte[] image, byte[] alphaChunk, int width, int height, bool alpha, ReadOnlySpan<byte> profile)
    {
        bool extended = !profile.IsEmpty || alphaChunk.Length > 0;
        int size = 12 + 8 + image.Length + (image.Length & 1);
        if (extended)
        {
            size += 18;
            size += profile.IsEmpty ? 0 : 8 + profile.Length + (profile.Length & 1);
            size += alphaChunk.Length == 0 ? 0 : 8 + alphaChunk.Length + (alphaChunk.Length & 1);
        }

        byte[] file = new byte[size];
        Span<byte> span = file;
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], file.Length - 8);
        "WEBP"u8.CopyTo(span[8..]);
        int at = 12;
        if (extended)
        {
            "VP8X"u8.CopyTo(span[at..]);
            BinaryPrimitives.WriteInt32LittleEndian(span[(at + 4)..], 10);
            span[at + 8] = (byte)((profile.IsEmpty ? 0 : 0x20) | (alpha ? 0x10 : 0));
            WriteUInt24(span[(at + 12)..], width - 1);
            WriteUInt24(span[(at + 15)..], height - 1);
            at += 18;
            if (!profile.IsEmpty)
            {
                at = Chunk(span, at, "ICCP"u8, profile);
            }

            if (alphaChunk.Length > 0)
            {
                at = Chunk(span, at, "ALPH"u8, alphaChunk);
            }
        }

        Chunk(span, at, fourCc, image);
        return file;
    }

    private static int Chunk(Span<byte> span, int at, ReadOnlySpan<byte> fourCc, ReadOnlySpan<byte> payload)
    {
        fourCc.CopyTo(span[at..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[(at + 4)..], payload.Length);
        payload.CopyTo(span[(at + 8)..]);
        return at + 8 + payload.Length + (payload.Length & 1);
    }

    private static void WriteUInt24(Span<byte> span, int value)
    {
        span[0] = (byte)value;
        span[1] = (byte)(value >> 8);
        span[2] = (byte)(value >> 16);
    }
}
