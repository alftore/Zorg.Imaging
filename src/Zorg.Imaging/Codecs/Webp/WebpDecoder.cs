using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Formats;
using Zorg.Imaging.Metadata;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Codecs.Webp;

/// <summary>Decodes a WebP file: the RIFF container (RFC 9649, section 2) and its lossy (VP8) or lossless (VP8L) image.</summary>
/// <remarks>
/// <para>
/// The simple form (one VP8 or VP8L chunk) and the extended form (VP8X): lossy images with or without an ALPH chunk,
/// an ICC profile as the colour space, EXIF orientation applied as for JPEG, and an animation's first frame on a
/// transparent canvas.
/// </para>
/// <para>
/// Hostile input: the canvas and image sizes are checked against <see cref="ImageLimits.MaxPixels"/> before anything
/// is allocated, an image chunk may be no larger than its pixels could need, and an ICC profile at most 4 MiB.
/// </para>
/// </remarks>
internal static class WebpDecoder
{
    private const byte IccFlag = 0x20;
    private const byte ExifFlag = 0x08;
    private const byte AnimationFlag = 0x02;
    private const byte XmpFlag = 0x04;

    public static async ValueTask<Image> DecodeAsync(DataReader reader, DecodeOptions options, CancellationToken cancellationToken)
    {
        reader.FormatName = "WebP";
        byte[] head = new byte[12];
        await reader.ReadExactlyAsync(head, cancellationToken).ConfigureAwait(false);
        if (!head.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !head.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            throw ImageErrors.Corrupt("The WebP file doesn't start with a RIFF WEBP header.");
        }

        long end = 8L + BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
        var file = new State(reader, end, options);
        (string fourCc, uint size) = await file.NextChunkAsync(cancellationToken).ConfigureAwait(false);
        Picture picture = fourCc switch
        {
            "VP8L" => await file.ReadLosslessAsync(size, expectedWidth: 0, expectedHeight: 0, cancellationToken).ConfigureAwait(false),
            "VP8 " => await file.ReadLossyAsync(size, alpha: null, expectedWidth: 0, expectedHeight: 0, cancellationToken).ConfigureAwait(false),
            "VP8X" => await file.ReadExtendedAsync(size, cancellationToken).ConfigureAwait(false),
            _ => throw ImageErrors.Corrupt("The WebP file's first chunk isn't VP8, VP8L or VP8X."),
        };

        reader.Finish();
        PixelFormat format = options.PixelFormat != PixelFormat.None ? options.PixelFormat : picture.HasAlpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24;
        Image image = Argb.ToImage(picture.Pixels, picture.Width, picture.Height, picture.HasAlpha, format);
        image.ColorSpace = file.ColorSpace;
        image.Metadata = Metadata.ImageMetadata.From(file.Exif, file.Xmp);
        if (options.ApplyOrientation && file.Orientation != 1)
        {
            try
            {
                Image upright = Orientation.Apply(image, file.Orientation);
                image.Dispose();
                image = upright;
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        return image;
    }

    private static void CheckSize(int width, int height, ImageLimits limits)
    {
        long pixels = (long)width * height;
        if (pixels > limits.MaxPixels)
        {
            throw ImageErrors.ExceedsLimits(string.Create(
                CultureInfo.InvariantCulture,
                $"The WebP image is {width} x {height}, {pixels} pixels, more than the {limits.MaxPixels}-pixel limit (ImageLimits.MaxPixels)."));
        }
    }

    /// <summary>Decoded pixels, ARGB, and whether alpha is meant.</summary>
    private sealed record Picture(int Width, int Height, uint[] Pixels, bool HasAlpha);

    /// <summary>Reading through the chunks, with what they say.</summary>
    private sealed class State(DataReader reader, long end, DecodeOptions options)
    {
        public ColorSpace ColorSpace { get; private set; } = ColorSpace.Srgb;

        public int Orientation { get; private set; } = 1;

        /// <summary>The EXIF block, a TIFF structure, when the chunk is no larger than the header limit.</summary>
        public byte[]? Exif { get; private set; }

        /// <summary>The XMP packet, when no larger than the header limit.</summary>
        public byte[]? Xmp { get; private set; }

        public async ValueTask<(string FourCc, uint Size)> NextChunkAsync(CancellationToken cancellationToken)
        {
            if (reader.Position + 8 > end)
            {
                throw ImageErrors.Truncated("The WebP file ends before its image.");
            }

            byte[] header = new byte[8];
            await reader.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            if (reader.Position + size > end)
            {
                throw ImageErrors.Corrupt("A WebP chunk reaches past the end of the file.");
            }

            return (System.Text.Encoding.ASCII.GetString(header, 0, 4), size);
        }

        public async ValueTask SkipChunkAsync(uint size, CancellationToken cancellationToken) =>
            await reader.SkipAsync(size + (size & 1), cancellationToken).ConfigureAwait(false);

        public async ValueTask<Picture> ReadLosslessAsync(uint size, int expectedWidth, int expectedHeight, CancellationToken cancellationToken)
        {
            if (size < 5)
            {
                throw ImageErrors.Corrupt("The WebP lossless chunk is too short.");
            }

            ReadOnlyMemory<byte> peek = await reader.PeekAsync(5, cancellationToken).ConfigureAwait(false);
            (int width, int height, bool alpha) = Vp8lDecoder.ReadHeader(peek.Span);
            if (expectedWidth != 0 && (width != expectedWidth || height != expectedHeight))
            {
                throw ImageErrors.Corrupt("The WebP lossless image's size doesn't match the canvas or frame.");
            }

            CheckSize(width, height, options.Limits);

            // A lossless stream can't usefully be larger than a few bytes a pixel, whatever its chunk says.
            long cap = ((long)width * height * 8) + (1 << 16);
            if (size > cap)
            {
                throw ImageErrors.Corrupt("The WebP lossless chunk is larger than its image could need.");
            }

            byte[] data = new byte[size];
            await reader.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
            if ((size & 1) != 0 && reader.Position < end)
            {
                await reader.SkipAsync(1, cancellationToken).ConfigureAwait(false);
            }

            // The header's alpha bit is only a hint (the specification's word): libwebp keeps whatever alpha the pixels
            // have, so a file that says "no alpha" but has transparent pixels decodes with them.
            uint[] pixels = Vp8lDecoder.Decode(data, width, height);
            return new Picture(width, height, pixels, alpha || !AllOpaque(pixels));
        }

        private static bool AllOpaque(ReadOnlySpan<uint> pixels)
        {
            foreach (uint pixel in pixels)
            {
                if (pixel < 0xFF000000u)
                {
                    return false;
                }
            }

            return true;
        }

        public async ValueTask<Picture> ReadLossyAsync(uint size, byte[]? alpha, int expectedWidth, int expectedHeight, CancellationToken cancellationToken)
        {
            ReadOnlyMemory<byte> peek = await reader.PeekAsync(10, cancellationToken).ConfigureAwait(false);
            (int width, int height) = Vp8Decoder.ReadSize(peek.Span[..(int)Math.Min(size, (uint)peek.Length)]);
            if (expectedWidth != 0 && (width != expectedWidth || height != expectedHeight))
            {
                throw ImageErrors.Corrupt("The WebP lossy image's size doesn't match the canvas or frame.");
            }

            CheckSize(width, height, options.Limits);
            byte[] data = await ReadPayloadAsync(size, width, height, cancellationToken).ConfigureAwait(false);
            return new Picture(width, height, WebpLossy.Decode(data, alpha, width, height), HasAlpha: alpha is not null);
        }

        private async ValueTask<byte[]> ReadAlphaAsync(uint size, int width, int height, CancellationToken cancellationToken) =>
            await ReadPayloadAsync(size, width, height, cancellationToken).ConfigureAwait(false);

        // A chunk's data, refused when it's larger than its image could usefully need; then its pad byte.
        private async ValueTask<byte[]> ReadPayloadAsync(uint size, int width, int height, CancellationToken cancellationToken)
        {
            long cap = ((long)width * height * 8) + (1 << 16);
            if (size > cap)
            {
                throw ImageErrors.Corrupt("A WebP image chunk is larger than its image could need.");
            }

            byte[] data = new byte[size];
            await reader.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
            if ((size & 1) != 0 && reader.Position < end)
            {
                await reader.SkipAsync(1, cancellationToken).ConfigureAwait(false);
            }

            return data;
        }

        public async ValueTask<Picture> ReadExtendedAsync(uint size, CancellationToken cancellationToken)
        {
            if (size < 10)
            {
                throw ImageErrors.Corrupt("The WebP VP8X chunk is too short.");
            }

            byte[] vp8x = new byte[size];
            await reader.ReadExactlyAsync(vp8x, cancellationToken).ConfigureAwait(false);
            byte flags = vp8x[0];
            int width = (vp8x[4] | (vp8x[5] << 8) | (vp8x[6] << 16)) + 1;
            int height = (vp8x[7] | (vp8x[8] << 8) | (vp8x[9] << 16)) + 1;
            CheckSize(width, height, options.Limits);

            Picture? picture = null;
            byte[]? alpha = null;
            bool wantExif = (flags & ExifFlag) != 0;
            bool wantXmp = (flags & XmpFlag) != 0;
            while (reader.Position + 8 <= end && (picture is null || wantExif || wantXmp))
            {
                (string fourCc, uint chunkSize) = await NextChunkAsync(cancellationToken).ConfigureAwait(false);
                switch (fourCc)
                {
                    case "ICCP" when (flags & IccFlag) != 0 && picture is null:
                        await ReadProfileAsync(chunkSize, cancellationToken).ConfigureAwait(false);
                        break;

                    case "EXIF" when wantExif:
                        wantExif = false;
                        int take = (int)(chunkSize <= options.Limits.MaxHeaderBytes ? chunkSize : Math.Min(chunkSize, ExifOrientation.MaxBytesNeeded));
                        byte[] exif = new byte[take];
                        await reader.ReadExactlyAsync(exif, cancellationToken).ConfigureAwait(false);
                        Orientation = ExifOrientation.Read(exif);
                        if (take == chunkSize)
                        {
                            // Some writers put JPEG's "Exif\0\0" before the TIFF header; it isn't part of the block.
                            Exif = exif.AsSpan().StartsWith("Exif\0\0"u8) ? exif[6..] : exif;
                        }

                        // The rest of the chunk, and its pad byte when its size is odd.
                        await reader.SkipAsync(chunkSize - take + (chunkSize & 1), cancellationToken).ConfigureAwait(false);
                        break;

                    case "XMP " when wantXmp:
                        wantXmp = false;
                        if (chunkSize <= options.Limits.MaxHeaderBytes)
                        {
                            byte[] xmp = new byte[chunkSize];
                            await reader.ReadExactlyAsync(xmp, cancellationToken).ConfigureAwait(false);
                            Xmp = xmp;
                            await reader.SkipAsync(chunkSize & 1, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await SkipChunkAsync(chunkSize, cancellationToken).ConfigureAwait(false);
                        }

                        break;

                    case "VP8L" when picture is null:
                        picture = await ReadLosslessAsync(chunkSize, width, height, cancellationToken).ConfigureAwait(false);
                        break;

                    case "ALPH" when picture is null:
                        alpha = await ReadAlphaAsync(chunkSize, width, height, cancellationToken).ConfigureAwait(false);
                        break;

                    case "VP8 " when picture is null:
                        picture = await ReadLossyAsync(chunkSize, alpha, width, height, cancellationToken).ConfigureAwait(false);
                        break;

                    case "ANMF" when picture is null && (flags & AnimationFlag) != 0:
                        picture = await ReadFirstFrameAsync(chunkSize, width, height, cancellationToken).ConfigureAwait(false);
                        break;

                    default:
                        await SkipChunkAsync(chunkSize, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }

            return picture ?? throw ImageErrors.Corrupt("The WebP file has no image.");
        }

        private async ValueTask ReadProfileAsync(uint size, CancellationToken cancellationToken)
        {
            if (size > ColorSpace.MaxIccProfileBytes)
            {
                throw ImageErrors.ExceedsLimits(string.Create(CultureInfo.InvariantCulture, $"The WebP file's ICC profile is {size} bytes, more than {ColorSpace.MaxIccProfileBytes}."));
            }

            byte[] profile = new byte[size];
            await reader.ReadExactlyAsync(profile, cancellationToken).ConfigureAwait(false);
            if ((size & 1) != 0)
            {
                await reader.SkipAsync(1, cancellationToken).ConfigureAwait(false);
            }

            string? problem = ColorSpace.CheckIccProfile(profile);
            ColorSpace = problem is null ? ColorSpace.FromCheckedIccProfile(profile) : throw ImageErrors.Corrupt(problem);
        }

        // The first frame of an animation, placed on a transparent canvas: what a viewer shows before playing.
        private async ValueTask<Picture> ReadFirstFrameAsync(uint size, int canvasWidth, int canvasHeight, CancellationToken cancellationToken)
        {
            if (size < 16 + 8)
            {
                throw ImageErrors.Corrupt("The WebP animation frame is too short.");
            }

            byte[] header = new byte[16];
            await reader.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            int x = 2 * (header[0] | (header[1] << 8) | (header[2] << 16));
            int y = 2 * (header[3] | (header[4] << 8) | (header[5] << 16));
            int width = (header[6] | (header[7] << 8) | (header[8] << 16)) + 1;
            int height = (header[9] | (header[10] << 8) | (header[11] << 16)) + 1;
            if ((long)x + width > canvasWidth || (long)y + height > canvasHeight)
            {
                throw ImageErrors.Corrupt("The WebP animation frame reaches outside the canvas.");
            }

            long frameEnd = reader.Position + size - 16;
            Picture? frame = null;
            byte[]? alpha = null;
            while (frame is null && reader.Position + 8 <= frameEnd)
            {
                (string fourCc, uint chunkSize) = await NextChunkAsync(cancellationToken).ConfigureAwait(false);
                switch (fourCc)
                {
                    case "VP8L":
                        frame = await ReadLosslessAsync(chunkSize, width, height, cancellationToken).ConfigureAwait(false);
                        break;
                    case "ALPH":
                        alpha = await ReadAlphaAsync(chunkSize, width, height, cancellationToken).ConfigureAwait(false);
                        break;
                    case "VP8 ":
                        frame = await ReadLossyAsync(chunkSize, alpha, width, height, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        await SkipChunkAsync(chunkSize, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }

            if (frame is null)
            {
                throw ImageErrors.Corrupt("The WebP animation frame has no image.");
            }

            if (reader.Position < frameEnd)
            {
                await reader.SkipAsync(frameEnd - reader.Position, cancellationToken).ConfigureAwait(false);
            }

            if ((size & 1) != 0)
            {
                await reader.SkipAsync(1, cancellationToken).ConfigureAwait(false);
            }

            uint[] canvas = new uint[(long)canvasWidth * canvasHeight];
            for (int row = 0; row < height; row++)
            {
                frame.Pixels.AsSpan(row * width, width).CopyTo(canvas.AsSpan(((y + row) * canvasWidth) + x));
            }

            return new Picture(canvasWidth, canvasHeight, canvas, HasAlpha: true);
        }
    }
}
