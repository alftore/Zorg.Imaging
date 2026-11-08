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

/// <summary>Writes a WebP file (RFC 9649): a RIFF container around a VP8L stream, with VP8X and ICCP for a profile.</summary>
internal static class WebpEncoder
{
    public static async ValueTask EncodeAsync(Image image, Stream stream, WebpEncoderOptions options, bool sync, CancellationToken cancellationToken)
    {
        if (!options.Lossless)
        {
            throw new NotSupportedException("This version writes lossless WebP only; set WebpEncoderOptions.Lossless to true. Lossy WebP encoding is planned.");
        }

        if (image.Width > Vp8lEncoder.MaxSide || image.Height > Vp8lEncoder.MaxSide)
        {
            throw new NotSupportedException(string.Create(
                CultureInfo.InvariantCulture,
                $"Lossless WebP can't hold a {image.Width} x {image.Height} image: each side must be at most {Vp8lEncoder.MaxSide} pixels."));
        }

        ReadOnlyMemory<byte> profile = IccProfileWriter.For(image.ColorSpace, "WebP");
        byte[] file;
        using (Parallelism.Use(options.MaxDegreeOfParallelism))
        {
            (uint[] argb, bool alpha) = ToArgb(image);
            byte[] vp8l = Vp8lEncoder.Encode(argb, image.Width, image.Height, alpha, options.Effort);
            file = Container(vp8l, image.Width, image.Height, alpha, profile.Span);
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

    // RIFF, then VP8X and ICCP when there is a profile, then the VP8L chunk. Chunks are padded to an even length.
    private static byte[] Container(byte[] vp8l, int width, int height, bool alpha, ReadOnlySpan<byte> profile)
    {
        int vp8lChunk = 8 + vp8l.Length + (vp8l.Length & 1);
        int extended = profile.IsEmpty ? 0 : 18 + 8 + profile.Length + (profile.Length & 1);
        byte[] file = new byte[12 + extended + vp8lChunk];
        Span<byte> span = file;
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], file.Length - 8);
        "WEBP"u8.CopyTo(span[8..]);
        int at = 12;
        if (!profile.IsEmpty)
        {
            "VP8X"u8.CopyTo(span[at..]);
            BinaryPrimitives.WriteInt32LittleEndian(span[(at + 4)..], 10);
            span[at + 8] = (byte)(0x20 | (alpha ? 0x10 : 0));
            WriteUInt24(span[(at + 12)..], width - 1);
            WriteUInt24(span[(at + 15)..], height - 1);
            at += 18;
            "ICCP"u8.CopyTo(span[at..]);
            BinaryPrimitives.WriteInt32LittleEndian(span[(at + 4)..], profile.Length);
            profile.CopyTo(span[(at + 8)..]);
            at += 8 + profile.Length + (profile.Length & 1);
        }

        "VP8L"u8.CopyTo(span[at..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[(at + 4)..], vp8l.Length);
        vp8l.CopyTo(span[(at + 8)..]);
        return file;
    }

    private static void WriteUInt24(Span<byte> span, int value)
    {
        span[0] = (byte)value;
        span[1] = (byte)(value >> 8);
        span[2] = (byte)(value >> 16);
    }
}
