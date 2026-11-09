using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
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
        (byte[]? exif, byte[]? xmp) = MetadataWriter.Prepare(image, options.Metadata);
        byte[] file;
        using (Parallelism.Use(options.MaxDegreeOfParallelism))
        {
            (uint[] argb, bool alpha) = Argb.FromImage(image);
            (byte[] data, byte[] alphaChunk) = EncodePicture(argb, image.Width, image.Height, alpha, options);
            file = Container(options.Lossless ? "VP8L"u8 : "VP8 "u8, data, alphaChunk, image.Width, image.Height, alpha, profile.Span, exif, xmp);
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

    /// <summary>The largest side a VP8 frame header can hold.</summary>
    internal const int MaxLossySide = 16383;

    /// <summary>
    /// One picture's image chunk payload, VP8L or VP8 as the options say, and for a lossy picture with alpha the ALPH
    /// chunk's payload (else empty).
    /// </summary>
    internal static (byte[] Data, byte[] Alpha) EncodePicture(uint[] argb, int width, int height, bool alpha, WebpEncoderOptions options)
    {
        if (options.Lossless)
        {
            return (Vp8lEncoder.Encode(argb, width, height, alpha, options.Effort), []);
        }

        // The picture and its alpha are coded apart, side by side when there are cores for both.
        byte[] vp8 = [];
        byte[] alphaChunk = [];
        int bands = alpha ? Parallelism.Bands(argb.Length, 2) : 1;
        Parallelism.For(bands, band =>
        {
            if (band == 0)
            {
                (byte[] y, byte[] u, byte[] v) = ToYuv(argb, width, height);
                vp8 = Vp8Encoder.Encode(y, u, v, width, height, Vp8Encoder.QuantiserFor(options.Quality), options.Effort);
            }

            if (alpha && band == bands - 1)
            {
                alphaChunk = AlphaChunk(argb, width, height, options.Effort);
            }
        });

        return (vp8, alphaChunk);
    }

    /// <summary>
    /// An ANMF chunk's payload (RFC 9649, 2.7.1.1): the frame's place (even), size, duration in milliseconds, no
    /// blending, disposal to the background or none, then its ALPH and image chunks.
    /// </summary>
    internal static byte[] AnimationFrame(int x, int y, int width, int height, int duration, bool dispose, bool lossless, byte[] data, byte[] alphaChunk)
    {
        int size = 16 + 8 + data.Length + (data.Length & 1) + (alphaChunk.Length == 0 ? 0 : 8 + alphaChunk.Length + (alphaChunk.Length & 1));
        byte[] frame = new byte[size];
        Span<byte> span = frame;
        WriteUInt24(span, x / 2);
        WriteUInt24(span[3..], y / 2);
        WriteUInt24(span[6..], width - 1);
        WriteUInt24(span[9..], height - 1);
        WriteUInt24(span[12..], duration);
        span[15] = (byte)(0x02 | (dispose ? 0x01 : 0));
        int at = 16;
        if (alphaChunk.Length > 0)
        {
            at = Chunk(span, at, "ALPH"u8, alphaChunk);
        }

        Chunk(span, at, lossless ? "VP8L"u8 : "VP8 "u8, data);
        return frame;
    }

    /// <summary>An animated WebP: RIFF, VP8X with the animation flag, ICCP when given, ANIM, then the ANMF chunks.</summary>
    internal static void AnimationContainer(ArrayBufferWriter<byte> output, int width, int height, bool alpha, ReadOnlySpan<byte> profile, int loops, ReadOnlySpan<byte> frames)
    {
        long size = 4 + 18 + (profile.IsEmpty ? 0 : 8 + profile.Length + (profile.Length & 1)) + 14 + frames.Length;
        if (size > uint.MaxValue - 8)
        {
            throw new NotSupportedException("The animation is too large for a WebP file (4 GiB).");
        }

        Span<byte> head = stackalloc byte[12 + 18];
        "RIFF"u8.CopyTo(head);
        BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)size);
        "WEBP"u8.CopyTo(head[8..]);
        "VP8X"u8.CopyTo(head[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(head[16..], 10);
        head[20] = (byte)((profile.IsEmpty ? 0 : 0x20) | (alpha ? 0x10 : 0) | 0x02);
        head[21] = head[22] = head[23] = 0;
        WriteUInt24(head[24..], width - 1);
        WriteUInt24(head[27..], height - 1);
        output.Write(head);
        if (!profile.IsEmpty)
        {
            byte[] chunk = new byte[8 + profile.Length + (profile.Length & 1)];
            Chunk(chunk, 0, "ICCP"u8, profile);
            output.Write(chunk);
        }

        // ANIM: a transparent background (a hint only) and the loop count.
        Span<byte> anim = stackalloc byte[14];
        "ANIM"u8.CopyTo(anim);
        BinaryPrimitives.WriteInt32LittleEndian(anim[4..], 6);
        BinaryPrimitives.WriteUInt32LittleEndian(anim[8..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(anim[12..], (ushort)loops);
        output.Write(anim);
        output.Write(frames);
    }

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

        // In bands of macroblock rows on all cores: each output row reads only its own source rows.
        int bands = Parallelism.Bands((long)strideY * mbHigh * 16, mbHigh);
        Parallelism.For(bands, band =>
        {
            int first = mbHigh * band / bands;
            int last = mbHigh * (band + 1) / bands;
            for (int row = first * 16; row < last * 16; row++)
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

            for (int row = first * 8; row < last * 8; row++)
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
        });

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
    private static byte[] Container(
        ReadOnlySpan<byte> fourCc, byte[] image, byte[] alphaChunk, int width, int height, bool alpha, ReadOnlySpan<byte> profile, byte[]? exif = null, byte[]? xmp = null)
    {
        bool extended = !profile.IsEmpty || alphaChunk.Length > 0 || exif is not null || xmp is not null;
        int size = 12 + 8 + image.Length + (image.Length & 1);
        if (extended)
        {
            size += 18;
            size += profile.IsEmpty ? 0 : 8 + profile.Length + (profile.Length & 1);
            size += alphaChunk.Length == 0 ? 0 : 8 + alphaChunk.Length + (alphaChunk.Length & 1);
            size += exif is null ? 0 : 8 + exif.Length + (exif.Length & 1);
            size += xmp is null ? 0 : 8 + xmp.Length + (xmp.Length & 1);
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
            span[at + 8] = (byte)((profile.IsEmpty ? 0 : 0x20) | (alpha ? 0x10 : 0) | (exif is null ? 0 : 0x08) | (xmp is null ? 0 : 0x04));
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

        at = Chunk(span, at, fourCc, image);

        // EXIF and XMP after the image data, as RFC 9649 orders the chunks.
        if (exif is not null)
        {
            at = Chunk(span, at, "EXIF"u8, exif);
        }

        if (xmp is not null)
        {
            Chunk(span, at, "XMP "u8, xmp);
        }

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
