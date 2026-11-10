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
        int most = options.CodesLossless ? Vp8lEncoder.MaxSide : MaxLossySide;
        if (image.Width > most || image.Height > most)
        {
            throw new NotSupportedException(string.Create(
                CultureInfo.InvariantCulture,
                $"{(options.CodesLossless ? "Lossless" : "Lossy")} WebP can't hold a {image.Width} x {image.Height} image: each side must be at most {most} pixels."));
        }

        ReadOnlyMemory<byte> profile = IccProfileWriter.For(image.ColorSpace, "WebP");
        (byte[]? exif, byte[]? xmp) = MetadataWriter.Prepare(image, options.Metadata);
        byte[] file;
        using (Parallelism.Use(options.MaxDegreeOfParallelism))
        {
            (uint[] argb, bool alpha) = Argb.FromImage(image);
            (byte[] data, byte[] alphaChunk, bool lossless) = EncodePicture(argb, image.Width, image.Height, alpha, options);
            file = Container(lossless ? "VP8L"u8 : "VP8 "u8, data, alphaChunk, image.Width, image.Height, alpha, profile.Span, exif, xmp);
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
    /// One picture's image chunk payload, VP8L or VP8 as the options say (or VP8L where lossless is no larger than lossy:
    /// see <see cref="LosslessMayWin"/>), for a lossy picture with alpha the ALPH chunk's payload (else empty), and
    /// whether the payload is VP8L.
    /// </summary>
    internal static (byte[] Data, byte[] Alpha, bool Lossless) EncodePicture(uint[] argb, int width, int height, bool alpha, WebpEncoderOptions options)
    {
        if (alpha && !options.Exact)
        {
            HideTransparent(argb, width, height, options.CodesLossless);
        }

        if (options.CodesLossless)
        {
            return (Vp8lEncoder.Encode(argb, width, height, alpha, options.Effort, chooseOne: false, options.NearBits), [], true);
        }

        // Opaque graphics of flat colour can code smaller lossless than lossy, every pixel exact: a quick lossless coding
        // at effort 1 is tried beside the lossy one, and kept if it is no larger. On 40 game screenshots, 7 were kept
        // lossless and the set was 9% smaller, for 15% more time on one core; nothing else in the test corpus changed.
        if (!alpha && options.Effort >= LosslessTrialEffort && LosslessMayWin(argb, width))
        {
            byte[] lossy = [];
            byte[] trial = [];
            int sides = Parallelism.Bands(argb.Length, 2);
            Parallelism.For(sides, side =>
            {
                if (side == 0)
                {
                    (byte[] y, byte[] u, byte[] v) = ToYuv(argb, width, height, options.Effort);
                    lossy = Vp8Encoder.Encode(y, u, v, width, height, Vp8Encoder.QuantiserFor(options.Quality), options.Effort);
                }

                if (side == sides - 1)
                {
                    trial = Vp8lEncoder.Encode(argb, width, height, alpha: false, 1);
                }
            });

            if (trial.Length > lossy.Length)
            {
                return (lossy, [], false);
            }

            // From effort 5 the lossless coding is done again at the effort asked for: 6 to 17% smaller than the trial,
            // for about as much time again.
            byte[] lossless = options.Effort >= 5 ? Vp8lEncoder.Encode(argb, width, height, alpha: false, options.Effort) : trial;
            return (lossless.Length <= trial.Length ? lossless : trial, [], true);
        }

        // The picture and its alpha are coded apart, side by side when there are cores for both.
        byte[] vp8 = [];
        byte[] alphaChunk = [];
        int bands = alpha ? Parallelism.Bands(argb.Length, 2) : 1;
        Parallelism.For(bands, band =>
        {
            if (band == 0)
            {
                (byte[] y, byte[] u, byte[] v) = ToYuv(argb, width, height, options.Effort);
                vp8 = Vp8Encoder.Encode(y, u, v, width, height, Vp8Encoder.QuantiserFor(options.Quality), options.Effort);
            }

            if (alpha && band == bands - 1)
            {
                alphaChunk = AlphaChunk(argb, width, height, AlphaEffort(options.Effort));
            }
        });

        return (vp8, alphaChunk, false);
    }

    /// <summary>The effort from which an opaque lossy picture may be coded lossless where that is no larger.</summary>
    internal const int LosslessTrialEffort = 2;

    /// <summary>
    /// Whether a picture may code smaller lossless than lossy: when at least 85% of its pixels repeat the pixel to their
    /// left or above, as screenshots, pixel art and flat graphics do. Of opaque pictures in the QOI suite above that
    /// share, 7 in 10 were smaller lossless than lossy at quality 80 (a web page 59 KB against 103, a game 5 KB against
    /// 19); photos and textures never reach it, so they pay only this count.
    /// </summary>
    internal static bool LosslessMayWin(uint[] argb, int width)
    {
        long need = (argb.Length * 85L) / 100;
        long missing = argb.Length - need;
        long misses = 0;
        for (int i = 1; i < argb.Length; i++)
        {
            uint p = argb[i];
            if (p != argb[i - 1] && (i < width || p != argb[i - width]) && ++misses > missing)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Replaces the colour of fully transparent pixels, in place, with what codes smallest; visible pixels and every
    /// alpha value stay as they are. Lossless: transparent black, so transparent areas become runs of one value.
    /// Lossy: each transparent pixel first takes the average of its nearer neighbours, filled outwards from what shows a
    /// ring at a time, so the colour continues smoothly past the edge and no new edge rings into what shows; then a fully
    /// transparent 8 x 8 block takes one flat colour, the same along a run of such blocks. Against leaving the colour as
    /// it was in blocks with anything visible, 512-pixel icons came out 5 to 15% smaller and cut-outs of plants about
    /// 20% smaller, at the same quality of what shows.
    /// </summary>
    // Fills the colour of fully transparent pixels from what shows, a ring at a time: each pixel next to a filled one
    // takes the mean colour of its filled 4-neighbours, the ring's pixels all from the rings before it. Breadth first, so
    // each pixel is visited once.
    private static void Inpaint(uint[] argb, int width)
    {
        var filled = new bool[argb.Length];
        var queued = new bool[argb.Length];
        var ring = new System.Collections.Generic.List<int>();
        for (int i = 0; i < argb.Length; i++)
        {
            filled[i] = argb[i] >= 0x01000000u;
        }

        for (int i = 0; i < argb.Length; i++)
        {
            if (!filled[i] && HasFilledNeighbour(i))
            {
                ring.Add(i);
                queued[i] = true;
            }
        }

        var colours = new System.Collections.Generic.List<uint>();
        var next = new System.Collections.Generic.List<int>();
        while (ring.Count > 0)
        {
            colours.Clear();
            foreach (int i in ring)
            {
                int x = i % width;
                int r = 0, g = 0, b = 0, n = 0;
                void Add(int j)
                {
                    if (filled[j])
                    {
                        uint p = argb[j];
                        r += (int)((p >> 16) & 0xFF);
                        g += (int)((p >> 8) & 0xFF);
                        b += (int)(p & 0xFF);
                        n++;
                    }
                }

                if (x > 0)
                {
                    Add(i - 1);
                }

                if (x + 1 < width)
                {
                    Add(i + 1);
                }

                if (i >= width)
                {
                    Add(i - width);
                }

                if (i + width < argb.Length)
                {
                    Add(i + width);
                }

                colours.Add(((uint)(r / n) << 16) | ((uint)(g / n) << 8) | (uint)(b / n));
            }

            next.Clear();
            for (int k = 0; k < ring.Count; k++)
            {
                int i = ring[k];
                argb[i] = colours[k];
                filled[i] = true;
            }

            foreach (int i in ring)
            {
                int x = i % width;
                Queue(x > 0 ? i - 1 : -1);
                Queue(x + 1 < width ? i + 1 : -1);
                Queue(i - width);
                Queue(i + width < argb.Length ? i + width : -1);
            }

            (ring, next) = (next, ring);
        }

        bool HasFilledNeighbour(int i)
        {
            int x = i % width;
            return (x > 0 && filled[i - 1]) || (x + 1 < width && filled[i + 1]) || (i >= width && filled[i - width]) || (i + width < argb.Length && filled[i + width]);
        }

        void Queue(int j)
        {
            if (j >= 0 && !filled[j] && !queued[j])
            {
                queued[j] = true;
                next.Add(j);
            }
        }
    }

    internal static void HideTransparent(uint[] argb, int width, int height, bool lossless)
    {
        if (lossless)
        {
            for (int i = 0; i < argb.Length; i++)
            {
                if (argb[i] < 0x01000000u)
                {
                    argb[i] = 0;
                }
            }

            return;
        }

        Inpaint(argb, width);
        for (int by = 0; by < height; by += 8)
        {
            int rows = Math.Min(8, height - by);
            uint? flat = null;
            for (int bx = 0; bx < width; bx += 8)
            {
                int columns = Math.Min(8, width - bx);
                bool shown = false;
                for (int y = 0; y < rows && !shown; y++)
                {
                    shown = argb.AsSpan(((by + y) * width) + bx, columns).ContainsAnyInRange(0x01000000u, uint.MaxValue);
                }

                if (shown)
                {
                    flat = null;
                    continue;
                }

                // A run of transparent blocks keeps the colour its first block had at its first pixel.
                flat ??= argb[(by * width) + bx] & 0xFFFFFFu;
                uint colour = flat.Value;
                for (int y = 0; y < rows; y++)
                {
                    argb.AsSpan(((by + y) * width) + bx, columns).Fill(colour);
                }
            }
        }
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

    /// <summary>Effort from which lossy pictures take <see cref="SharpYuv"/>'s conversion.</summary>
    internal const int SharpEffort = 2;

    /// <summary>How many rounds <see cref="SharpYuv"/> takes: a third adds 0.05 dB for 7% more time.</summary>
    internal const int SharpRounds = 2;

    // RGB to Y'CbCr with BT.601's studio-range coefficients, as VP8 decoders convert back (RFC 6386 leaves the colour
    // space to the container; WebP uses this one), the chroma at half size. From effort 2 the chroma is chosen for what
    // a decoder will show (SharpYuv): at equal file size 0.3 to 0.6 dB on photos and 1.5 dB on game screenshots, up to
    // 9 dB on coloured text, for 5 to 15% more time on all cores. Below, the plain 2 x 2 average.
    internal static (byte[] Y, byte[] U, byte[] V) ToYuv(uint[] argb, int width, int height, int effort) =>
        effort >= SharpEffort ? SharpYuv.Convert(argb, width, height, SharpRounds) : PlainYuv(argb, width, height);

    // The plain conversion: chroma the 2 x 2 average. The planes are padded to whole macroblocks by repeating the last
    // row and column, so the padding costs few bits.
    internal static (byte[] Y, byte[] U, byte[] V) PlainYuv(uint[] argb, int width, int height)
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

    // The lossless effort for a lossy picture's alpha: two below the picture's, at least 1, as libwebp also codes alpha
    // below the picture. At the default 4, an 800 x 600 cut-out's alpha takes a quarter less time for 11% more bytes, a
    // third fewer than cwebp's; effort 0 would leave the alpha nearly uncompressed.
    internal static int AlphaEffort(int effort) => Math.Max(effort - 2, 1);

    // An ALPH chunk's payload: a header byte (no preprocessing, no filter, lossless compression) and the alpha as the
    // green channel of a VP8L image stream without its 5-byte header.
    private static byte[] AlphaChunk(uint[] argb, int width, int height, int effort)
    {
        uint[] alpha = new uint[argb.Length];
        for (int i = 0; i < argb.Length; i++)
        {
            alpha[i] = 0xFF000000u | ((argb[i] >> 24) << 8);
        }

        byte[] stream = Vp8lEncoder.Encode(alpha, width, height, alpha: false, effort, chooseOne: true, nearBits: 0);
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
