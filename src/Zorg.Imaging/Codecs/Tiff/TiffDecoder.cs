using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Codecs.Jpeg;
using Zorg.Imaging.Formats;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Codecs.Tiff;

/// <summary>Decodes the first image of a baseline TIFF.</summary>
/// <remarks>
/// <para>
/// Strips or tiles, samples stored together or in planes; uncompressed, PackBits, LZW or Deflate, with or without the
/// horizontal predictor; grey (either polarity, 1 to 16 bits), RGB, palette and CMYK (through its ICC profile when it
/// has one, else the device formula) of 8 or 16 bits; associated or unassociated alpha. The orientation tag is applied
/// unless <see cref="DecodeOptions.ApplyOrientation"/> is off, and an RGB or grey ICC profile becomes the colour
/// space. Further pages are ignored.
/// </para>
/// <para>
/// A TIFF keeps its directory anywhere, often at the end, so a stream is read whole first, but never past what the
/// largest image the limits allow could need. Each chunk is decompressed only up to the bytes its pixels need, so a
/// compression bomb costs no more than the image, and short or damaged chunks leave the rest of their pixels black.
/// </para>
/// </remarks>
internal static class TiffDecoder
{
    public static async ValueTask<Image> DecodeAsync(DataReader reader, DecodeOptions options, CancellationToken cancellationToken)
    {
        reader.FormatName = "TIFF";
        ReadOnlyMemory<byte> file = await ReadAllAsync(reader, options.Limits, cancellationToken).ConfigureAwait(false);
        TiffDirectory directory = TiffDirectory.Read(file.Span, options.Limits);
        Image image = Decode(file, directory, options);
        if (options.ApplyOrientation && directory.Orientation is > 1 and <= 8)
        {
            try
            {
                Image upright = Processing.Orientation.Apply(image, directory.Orientation);
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

    // The whole file: as it is from memory, else read from the stream up to what the largest allowed image needs.
    private static async ValueTask<ReadOnlyMemory<byte>> ReadAllAsync(DataReader reader, ImageLimits limits, CancellationToken cancellationToken)
    {
        long cap = (limits.MaxPixels * 8) + limits.MaxHeaderBytes + (1L << 20);
        int most = (int)Math.Min(cap, Array.MaxLength);
        byte[] data = new byte[1 << 16];
        int length = 0;
        while (true)
        {
            if (length == data.Length)
            {
                if (length >= most)
                {
                    throw ImageErrors.ExceedsLimits("The TIFF file is larger than its image could need under the limits (ImageLimits.MaxPixels).");
                }

                Array.Resize(ref data, (int)Math.Min((long)data.Length * 2, most));
            }

            int read = await reader.ReadSomeAsync(data.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        return data.AsMemory(0, length);
    }

    private static Image Decode(ReadOnlyMemory<byte> file, TiffDirectory d, DecodeOptions options)
    {
        int width = d.Width;
        int height = d.Height;
        int samples = d.SamplesPerPixel;
        bool sixteen = d.Bits == 16;
        int planes = d.Planar == 2 ? samples : 1;
        int perPixel = d.Planar == 2 ? 1 : samples;

        // Every sample at 8 or 16 bits, interleaved, row by row: sub-byte grey and indices are widened to a byte.
        int sampleBytes = sixteen ? 2 : 1;
        long total = (long)width * height * samples * sampleBytes;
        byte[] pixels = new byte[total];
        int chunkWidth = d.Tiled ? d.TileWidth : width;
        int chunkHeight = d.Tiled ? d.TileHeight : d.RowsPerStrip;
        long rowBytes = (((long)chunkWidth * perPixel * d.Bits) + 7) / 8;
        if (rowBytes * chunkHeight > Array.MaxLength)
        {
            throw ImageErrors.ExceedsLimits("A TIFF strip or tile is larger than 2 GB.");
        }
        int across = d.ChunksAcross;
        int down = d.ChunksDown;
        Parallelism.For(Parallelism.Bands(total, down * across * planes), band =>
        {
            int bands = Parallelism.Bands(total, down * across * planes);
            int count = down * across * planes;
            byte[] chunk = new byte[rowBytes * chunkHeight];
            for (int index = (int)((long)count * band / bands); index < (int)((long)count * (band + 1) / bands); index++)
            {
                int plane = index / (down * across);
                int within = index % (down * across);
                int top = within / across * chunkHeight;
                int left = within % across * chunkWidth;
                int rows = d.Tiled ? chunkHeight : Math.Min(chunkHeight, height - top);
                Span<byte> target = chunk.AsSpan(0, (int)(rowBytes * rows));
                target.Clear();
                long offset = d.Offsets[index];
                long length = d.ByteCounts[index];
                if (offset > 0 && offset < file.Length)
                {
                    ReadOnlySpan<byte> source = file.Span.Slice((int)offset, (int)Math.Min(length, file.Length - offset));
                    Decompress(d.Compression, source, target);
                }

                if (d.Predictor == 2)
                {
                    Undifference(target, (int)rowBytes, rows, perPixel, sixteen, d.BigEndian);
                }
                else if (sixteen && d.BigEndian)
                {
                    Swap(target);
                }

                Place(target, (int)rowBytes, rows, chunkWidth, left, top, plane, d, pixels);
            }
        });

        return ToImage(pixels, d, options);
    }

    private static void Decompress(int compression, ReadOnlySpan<byte> source, Span<byte> target)
    {
        switch (compression)
        {
            case TiffDirectory.None:
                source[..Math.Min(source.Length, target.Length)].CopyTo(target);
                break;
            case TiffDirectory.PackBits:
                UnpackBits(source, target);
                break;
            case TiffDirectory.Lzw:
                TiffLzw.Decode(source, target);
                break;
            default:
                Inflate(source, target);
                break;
        }
    }

    // PackBits (TIFF 6.0, section 9): a count byte, then that many plus one literal bytes, or one byte repeated.
    private static void UnpackBits(ReadOnlySpan<byte> source, Span<byte> target)
    {
        int at = 0;
        int written = 0;
        while (at < source.Length && written < target.Length)
        {
            int n = (sbyte)source[at++];
            if (n >= 0)
            {
                int take = Math.Min(Math.Min(n + 1, source.Length - at), target.Length - written);
                source.Slice(at, take).CopyTo(target[written..]);
                at += n + 1;
                written += take;
            }
            else if (n != -128 && at < source.Length)
            {
                int take = Math.Min(1 - n, target.Length - written);
                target.Slice(written, take).Fill(source[at++]);
                written += take;
            }
        }
    }

    // A zlib stream, read only as far as the chunk's pixels go; damage ends the chunk there.
    private static void Inflate(ReadOnlySpan<byte> source, Span<byte> target)
    {
        using var input = new MemoryStream(source.ToArray(), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        int written = 0;
        try
        {
            while (written < target.Length)
            {
                int read = zlib.Read(target[written..]);
                if (read == 0)
                {
                    break;
                }

                written += read;
            }
        }
        catch (InvalidDataException)
        {
            // What came before the damage stays.
        }
    }

    // The horizontal predictor (TIFF 6.0, section 14): each sample is the difference from the same sample of the pixel
    // before it in the row. 16-bit samples are put in machine order first.
    private static void Undifference(Span<byte> chunk, int rowBytes, int rows, int perPixel, bool sixteen, bool bigEndian)
    {
        for (int r = 0; r < rows; r++)
        {
            Span<byte> row = chunk.Slice(r * rowBytes, rowBytes);
            if (sixteen)
            {
                if (bigEndian)
                {
                    Swap(row);
                }

                Span<ushort> values = MemoryMarshal.Cast<byte, ushort>(row[..(row.Length & ~1)]);
                for (int i = perPixel; i < values.Length; i++)
                {
                    values[i] += values[i - perPixel];
                }
            }
            else
            {
                for (int i = perPixel; i < row.Length; i++)
                {
                    row[i] += row[i - perPixel];
                }
            }
        }
    }

    private static void Swap(Span<byte> data)
    {
        Span<ushort> values = MemoryMarshal.Cast<byte, ushort>(data[..(data.Length & ~1)]);
        BinaryPrimitives.ReverseEndianness(values, values);
    }

    // Copies a chunk's rows into the image's interleaved samples, clipped to the image; sub-byte samples are widened.
    private static void Place(ReadOnlySpan<byte> chunk, int rowBytes, int rows, int chunkWidth, int left, int top, int plane, TiffDirectory d, byte[] pixels)
    {
        int width = d.Width;
        int samples = d.SamplesPerPixel;
        int sampleBytes = d.Bits == 16 ? 2 : 1;
        int columns = Math.Min(chunkWidth, width - left);
        for (int r = 0; r < rows && top + r < d.Height; r++)
        {
            ReadOnlySpan<byte> row = chunk.Slice(r * rowBytes, rowBytes);
            Span<byte> target = pixels.AsSpan((int)((((long)(top + r) * width) + left) * samples * sampleBytes));
            if (d.Bits < 8)
            {
                int bits = d.Bits;
                int mask = (1 << bits) - 1;
                for (int x = 0; x < columns; x++)
                {
                    int bit = x * bits;
                    target[x] = (byte)((row[bit >> 3] >> (8 - bits - (bit & 7))) & mask);
                }
            }
            else if (d.Planar == 1)
            {
                row[..(columns * samples * sampleBytes)].CopyTo(target);
            }
            else
            {
                for (int x = 0; x < columns; x++)
                {
                    for (int b = 0; b < sampleBytes; b++)
                    {
                        target[(((x * samples) + plane) * sampleBytes) + b] = row[(x * sampleBytes) + b];
                    }
                }
            }
        }
    }

    private static Image ToImage(byte[] pixels, TiffDirectory d, DecodeOptions options)
    {
        int width = d.Width;
        int height = d.Height;
        int samples = d.SamplesPerPixel;
        bool sixteen = d.Bits == 16;
        int colour = d.Photometric switch { TiffDirectory.Rgb => 3, TiffDirectory.Separated => 4, _ => 1 };
        int alphaKind = samples > colour && d.ExtraSamples.Length > 0 ? d.ExtraSamples[0] : 0;
        bool alpha = alphaKind is 1 or 2;
        bool grey = d.Photometric is TiffDirectory.WhiteIsZero or TiffDirectory.BlackIsZero;

        ColorSpace colorSpace = ColorSpace.Srgb;
        if (d.IccProfile is { } profile && d.Photometric != TiffDirectory.Separated)
        {
            string? problem = ColorSpace.CheckIccProfile(profile);
            colorSpace = problem is null ? ColorSpace.FromCheckedIccProfile(profile) : throw ImageErrors.Corrupt(problem);
        }

        PixelFormat natural = (grey, alpha, sixteen) switch
        {
            (true, false, false) => PixelFormat.L8,
            (true, false, true) => PixelFormat.L16,
            (_, false, false) => PixelFormat.Rgb24,
            (_, false, true) => d.Photometric == TiffDirectory.Palette || d.Photometric == TiffDirectory.Separated ? PixelFormat.Rgb24 : PixelFormat.Rgb48,
            (_, true, false) => PixelFormat.Rgba32,
            _ => d.Photometric == TiffDirectory.Separated ? PixelFormat.Rgba32 : PixelFormat.Rgba64,
        };
        if (d.Photometric == TiffDirectory.Palette)
        {
            natural = alpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24;
        }

        // Read every pixel as 16-bit RGBA, straight alpha, then write it in the format asked for.
        var result = new Image(width, height, natural) { ColorSpace = colorSpace };
        try
        {
            JpegCmyk? cmyk = d.Photometric == TiffDirectory.Separated ? JpegCmyk.For(-1, d.IccProfile ?? []) : null;
            int maximum = sixteen ? 65535 : d.Bits < 8 ? (1 << d.Bits) - 1 : 255;
            int sampleBytes = sixteen ? 2 : 1;
            var row = new Rgba64[width];
            byte[][] inks = cmyk is null ? [] : [new byte[width], new byte[width], new byte[width], new byte[width]];
            byte[] converted = cmyk is null ? [] : new byte[width * 3];
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<byte> source = pixels.AsSpan((int)((long)y * width * samples * sampleBytes), width * samples * sampleBytes);
                if (cmyk is not null)
                {
                    for (int x = 0; x < width; x++)
                    {
                        for (int s = 0; s < 4; s++)
                        {
                            inks[s][x] = (byte)(sixteen ? (Sample(source, samples, sixteen, x, s) + 128) / 257 : Sample(source, samples, sixteen, x, s));
                        }
                    }

                    cmyk.WriteRow(inks, converted, PixelFormat.Rgb24, width);
                }

                for (int x = 0; x < width; x++)
                {
                    ushort a = alpha ? Scale(maximum, Sample(source, samples, sixteen, x, colour)) : (ushort)65535;
                    ushort r, g, b;
                    if (cmyk is not null)
                    {
                        (r, g, b) = ((ushort)(converted[3 * x] * 257), (ushort)(converted[(3 * x) + 1] * 257), (ushort)(converted[(3 * x) + 2] * 257));
                    }
                    else if (d.Photometric == TiffDirectory.Palette)
                    {
                        int i = Sample(source, samples, sixteen, x, 0);
                        int n = 1 << d.Bits;
                        (r, g, b) = (d.ColorMap[i], d.ColorMap[n + i], d.ColorMap[(2 * n) + i]);
                    }
                    else if (grey)
                    {
                        ushort v = Scale(maximum, Sample(source, samples, sixteen, x, 0));
                        r = g = b = d.Photometric == TiffDirectory.WhiteIsZero ? (ushort)(65535 - v) : v;
                    }
                    else
                    {
                        (r, g, b) = (Scale(maximum, Sample(source, samples, sixteen, x, 0)), Scale(maximum, Sample(source, samples, sixteen, x, 1)), Scale(maximum, Sample(source, samples, sixteen, x, 2)));
                    }

                    if (alphaKind == 1 && a is > 0 and < 65535)
                    {
                        // Associated alpha: the colour was premultiplied.
                        r = (ushort)Math.Min(65535, ((r * 65535L) + (a / 2)) / a);
                        g = (ushort)Math.Min(65535, ((g * 65535L) + (a / 2)) / a);
                        b = (ushort)Math.Min(65535, ((b * 65535L) + (a / 2)) / a);
                    }

                    row[x] = new Rgba64(r, g, b, a);
                }

                Write(result, y, row);
            }
        }
        catch
        {
            result.Dispose();
            throw;
        }

        if (options.PixelFormat != PixelFormat.None && options.PixelFormat != natural)
        {
            using (result)
            {
                return Convert(result, options.PixelFormat);
            }
        }

        return result;
    }

    private static int Sample(ReadOnlySpan<byte> row, int samples, bool sixteen, int x, int s) =>
        sixteen ? MemoryMarshal.Read<ushort>(row[(((x * samples) + s) * 2)..]) : row[(x * samples) + s];

    private static ushort Scale(int maximum, int v) => (ushort)((((long)v * 65535) + (maximum / 2)) / maximum);

    // One row of 16-bit RGBA into the image's format.
    private static void Write(Image image, int y, ReadOnlySpan<Rgba64> row)
    {
        Span<byte> bytes = image.GetRowBytes(y);
        Span<ushort> wide = MemoryMarshal.Cast<byte, ushort>(bytes);
        static byte To8(ushort v) => (byte)((v + 128) / 257);
        for (int x = 0; x < row.Length; x++)
        {
            Rgba64 p = row[x];
            switch (image.PixelFormat)
            {
                case PixelFormat.L8:
                    bytes[x] = To8(p.R);
                    break;
                case PixelFormat.L16:
                    wide[x] = p.R;
                    break;
                case PixelFormat.Rgb24:
                    (bytes[3 * x], bytes[(3 * x) + 1], bytes[(3 * x) + 2]) = (To8(p.R), To8(p.G), To8(p.B));
                    break;
                case PixelFormat.Rgba32:
                    (bytes[4 * x], bytes[(4 * x) + 1], bytes[(4 * x) + 2], bytes[(4 * x) + 3]) = (To8(p.R), To8(p.G), To8(p.B), To8(p.A));
                    break;
                case PixelFormat.Rgb48:
                    (wide[3 * x], wide[(3 * x) + 1], wide[(3 * x) + 2]) = (p.R, p.G, p.B);
                    break;
                default:
                    (wide[4 * x], wide[(4 * x) + 1], wide[(4 * x) + 2], wide[(4 * x) + 3]) = (p.R, p.G, p.B, p.A);
                    break;
            }
        }
    }

    // Rgba32 or Bgra32 on request, through ARGB.
    private static Image Convert(Image image, PixelFormat format)
    {
        (uint[] argb, bool alpha) = Argb.FromImage(image);
        Image converted = Argb.ToImage(argb, image.Width, image.Height, alpha, format);
        converted.ColorSpace = image.ColorSpace;
        return converted;
    }
}
