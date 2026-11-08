using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Formats;
using Zorg.Imaging.Metadata;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Codecs.Png;

/// <summary>Decodes a PNG file (W3C PNG Specification, Third Edition), or an APNG's first frame.</summary>
/// <remarks>
/// <para>
/// An APNG's first frame is its default image (IDAT) when an fcTL chunk comes before the image data. Otherwise the
/// default image is only for viewers without APNG support, and isn't part of the animation: it's checked and passed
/// over, and the first fcTL and fdAT frame is decoded instead, on a transparent canvas the size of IHDR, as browsers
/// show it.
/// </para>
/// <para>
/// Hostile input: the header is checked against <see cref="ImageLimits.MaxPixels"/> before anything is allocated, and
/// inflating stops once the image's rows are full, so a zlib bomb costs no more than the image itself. Ancillary
/// chunks are skipped without being read.
/// </para>
/// <para>
/// Strict: a bad CRC on a chunk the decoder reads, a bad filter type, image data that ends early, a missing IEND, a
/// broken run of IDAT chunks, or an unknown critical chunk all throw.
/// </para>
/// </remarks>
internal static class PngDecoder
{
    private const uint Ihdr = 0x49484452;
    private const uint Plte = 0x504C5445;
    private const uint Idat = 0x49444154;
    private const uint Iend = 0x49454E44;
    private const uint Trns = 0x74524E53;
    private const uint Exif = 0x65584966;
    private const uint CgBI = 0x43674249;
    private const uint Cicp = 0x63494350;
    private const uint Iccp = 0x69434350;
    private const uint SrgbChunk = 0x73524742;
    private const uint Gama = 0x67414D41;
    private const uint Chrm = 0x6348524D;
    private const uint Actl = 0x6163544C;
    private const uint Fctl = 0x6663544C;
    private const uint Fdat = 0x66644154;

    private const int IhdrLength = 13;
    private const int CrcLength = 4;

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Adam7: x start, y start, x step, y step for each of the seven passes.
    private static ReadOnlySpan<byte> Adam7 =>
    [
        0, 0, 8, 8,
        4, 0, 8, 8,
        0, 4, 4, 8,
        2, 0, 4, 4,
        0, 2, 2, 4,
        1, 0, 2, 2,
        0, 1, 1, 2,
    ];

    /// <summary>Decodes the file. The reader is at its start, before the signature.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="options">The options.</param>
    /// <param name="sync">
    /// Whether to read synchronously. Only for a reader over memory, where every read completes at once; then this
    /// method completes synchronously too.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The image, which the caller disposes.</returns>
    public static async ValueTask<Image> DecodeAsync(DataReader reader, DecodeOptions options, bool sync, CancellationToken cancellationToken)
    {
        reader.FormatName = "PNG";
        byte[] head = new byte[8];
        await reader.ReadExactlyAsync(head, cancellationToken).ConfigureAwait(false);
        if (!head.AsSpan().SequenceEqual(Signature))
        {
            throw ImageErrors.Corrupt("The data doesn't start with the PNG signature.");
        }

        (long length, uint type) = await ReadChunkHeaderAsync(reader, head, cancellationToken).ConfigureAwait(false);
        if (type == CgBI)
        {
            throw new UnsupportedImageFormatException("The file is Apple's CgBI variant of PNG, which isn't supported.", ImageFormat.Png);
        }

        if (type != Ihdr || length != IhdrLength)
        {
            throw ImageErrors.Corrupt("The PNG file doesn't start with a 13-byte IHDR chunk.");
        }

        byte[] ihdr = await ReadChunkDataAsync(reader, type, IhdrLength, cancellationToken).ConfigureAwait(false);
        PngHeader header = PngHeader.Parse(ihdr);
        header.CheckLimits(options.Limits, options.PixelFormat);

        byte[]? palette = null;
        byte[]? transparency = null;
        int orientation = 1;
        bool sawExif = false;
        bool animated = false;
        bool defaultIsFrame = false;
        var colour = new ColourChunks();

        while (true)
        {
            (length, type) = await ReadChunkHeaderAsync(reader, head, cancellationToken).ConfigureAwait(false);
            if (type == Idat)
            {
                break;
            }

            switch (type)
            {
                case Iend:
                    throw ImageErrors.Corrupt("The PNG file has no image data: its IEND chunk comes before any IDAT chunk.");

                case Plte:
                    palette = await ReadPaletteAsync(reader, header, palette, length, cancellationToken).ConfigureAwait(false);
                    break;

                case Trns when header.ColourType is 4 or 6:
                    // A tRNS chunk is invalid with an alpha channel; libpng ignores it too.
                    await reader.SkipAsync(length + CrcLength, cancellationToken).ConfigureAwait(false);
                    break;

                case Trns:
                    transparency = await ReadTransparencyAsync(reader, header, palette, transparency, length, cancellationToken).ConfigureAwait(false);
                    break;

                case Exif when !sawExif:
                    // The same rule as Identify: only an eXIf before the image data counts.
                    sawExif = true;
                    int take = (int)Math.Min(length, ExifOrientation.MaxBytesNeeded);
                    byte[] exif = new byte[take];
                    await reader.ReadExactlyAsync(exif, cancellationToken).ConfigureAwait(false);
                    orientation = ExifOrientation.Read(exif);
                    await reader.SkipAsync(length - take + CrcLength, cancellationToken).ConfigureAwait(false);
                    break;

                case Actl:
                    animated = true;
                    await reader.SkipAsync(length + CrcLength, cancellationToken).ConfigureAwait(false);
                    break;

                case Fctl:
                    defaultIsFrame = true;
                    await reader.SkipAsync(length + CrcLength, cancellationToken).ConfigureAwait(false);
                    break;

                case Cicp or Iccp or SrgbChunk or Gama or Chrm:
                    await colour.ReadAsync(reader, type, length, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    RefuseUnknownCritical(type);
                    await reader.SkipAsync(length + CrcLength, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        if (header.ColourType == 3 && palette is null)
        {
            throw ImageErrors.Corrupt("The PNG file is a palette image with no PLTE chunk.");
        }

        if (animated && !defaultIsFrame)
        {
            return await DecodeFirstFrameAsync(reader, length, header, palette, transparency, colour.Resolve(), orientation, options, sync, head, cancellationToken).ConfigureAwait(false);
        }

        var converter = new PngRowConverter(header, palette, transparency, options.PixelFormat);
        Image image = header.CreateImage(converter.Target);
        image.ColorSpace = colour.Resolve();
        try
        {
            var idat = new IdatStream(reader, length, sync);
            await DecodeImageDataAsync(idat, header, converter, image, sync, cancellationToken).ConfigureAwait(false);
            await ReadToEndAsync(reader, idat, head, cancellationToken).ConfigureAwait(false);
            reader.Finish();

            if (options.ApplyOrientation && orientation != 1)
            {
                Image upright = Orientation.Apply(image, orientation);
                image.Dispose();
                image = upright;
            }

            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    // An APNG whose default image isn't part of the animation: pass over the IDAT run, then decode the first fcTL frame
    // from its fdAT chunks onto a transparent canvas.
    private static async ValueTask<Image> DecodeFirstFrameAsync(
        DataReader reader,
        long idatLength,
        PngHeader header,
        byte[]? palette,
        byte[]? transparency,
        ColorSpace colorSpace,
        int orientation,
        DecodeOptions options,
        bool sync,
        byte[] head,
        CancellationToken cancellationToken)
    {
        long length;
        uint type;
        using (var hidden = new IdatStream(reader, idatLength, sync))
        {
            (length, type) = await hidden.SkipRestAsync(cancellationToken).ConfigureAwait(false);
        }

        while (type != Fctl)
        {
            if (type is Iend or Idat or Fdat or Plte or Ihdr)
            {
                throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The APNG file has a {ChunkName(type)} chunk where its first frame's fcTL chunk should be."));
            }

            RefuseUnknownCritical(type);
            await reader.SkipAsync(length + CrcLength, cancellationToken).ConfigureAwait(false);
            (length, type) = await ReadChunkHeaderAsync(reader, head, cancellationToken).ConfigureAwait(false);
        }

        if (length != 26)
        {
            throw ImageErrors.Corrupt("The APNG file's fcTL chunk isn't 26 bytes long.");
        }

        byte[] fctl = await ReadChunkDataAsync(reader, Fctl, length, cancellationToken).ConfigureAwait(false);
        uint width = BinaryPrimitives.ReadUInt32BigEndian(fctl.AsSpan(4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(fctl.AsSpan(8));
        uint left = BinaryPrimitives.ReadUInt32BigEndian(fctl.AsSpan(12));
        uint top = BinaryPrimitives.ReadUInt32BigEndian(fctl.AsSpan(16));
        if (width == 0 || height == 0 || (long)left + width > header.Width || (long)top + height > header.Height)
        {
            throw ImageErrors.Corrupt(string.Create(
                CultureInfo.InvariantCulture,
                $"The APNG file's first frame, {width} x {height} at ({left}, {top}), doesn't fit its {header.Width} x {header.Height} canvas."));
        }

        (length, type) = await ReadChunkHeaderAsync(reader, head, cancellationToken).ConfigureAwait(false);
        if (type != Fdat)
        {
            throw ImageErrors.Corrupt("The APNG file's first frame has no fdAT chunk after its fcTL chunk.");
        }

        // A frame that fills the canvas is the image; a smaller one is placed on a transparent canvas, so the image
        // needs alpha.
        PngHeader frameHeader = header with { Width = (int)width, Height = (int)height };
        bool fills = width == header.Width && height == header.Height;
        PixelFormat requested = options.PixelFormat;
        if (!fills && requested == PixelFormat.None)
        {
            requested = header.BitDepth == 16 ? PixelFormat.Rgba64 : PixelFormat.Rgba32;
        }

        var converter = new PngRowConverter(frameHeader, palette, transparency, requested);
        Image frame = frameHeader.CreateImage(converter.Target);
        Image? canvas = null;
        try
        {
            var fdat = new IdatStream(reader, length, sync, frameData: true);
            await DecodeImageDataAsync(fdat, frameHeader, converter, frame, sync, cancellationToken).ConfigureAwait(false);
            await ReadToEndAsync(reader, fdat, head, cancellationToken).ConfigureAwait(false);
            reader.Finish();

            if (!fills)
            {
                canvas = header.CreateImage(frame.PixelFormat);
                int bytes = frame.BytesPerPixel;
                for (int y = 0; y < frame.Height; y++)
                {
                    frame.GetRowBytes(y).CopyTo(canvas.GetRowBytes((int)top + y)[((int)left * bytes)..]);
                }

                frame.Dispose();
                frame = canvas;
                canvas = null;
            }

            frame.ColorSpace = colorSpace;
            if (options.ApplyOrientation && orientation != 1)
            {
                Image upright = Orientation.Apply(frame, orientation);
                frame.Dispose();
                frame = upright;
            }

            return frame;
        }
        catch
        {
            frame.Dispose();
            canvas?.Dispose();
            throw;
        }
    }

    private static async ValueTask DecodeImageDataAsync(
        IdatStream idat, PngHeader header, PngRowConverter converter, Image image, bool sync, CancellationToken cancellationToken)
    {
        int maxRowBytes = header.RowBytes(header.Width);
        byte[] current = ArrayPool<byte>.Shared.Rent(maxRowBytes + 1);
        byte[] previous = ArrayPool<byte>.Shared.Rent(maxRowBytes + 1);
        byte[]? scattered = header.Interlaced ? ArrayPool<byte>.Shared.Rent(header.Width * image.BytesPerPixel) : null;
        var zlib = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true);
        try
        {
            for (int pass = header.Interlaced ? 0 : -1; pass < (header.Interlaced ? 7 : 0); pass++)
            {
                (int x0, int y0, int dx, int dy) = pass < 0 ? (0, 0, 1, 1) : (Adam7[pass * 4], Adam7[(pass * 4) + 1], Adam7[(pass * 4) + 2], Adam7[(pass * 4) + 3]);
                int passWidth = header.Width > x0 ? (header.Width - x0 + dx - 1) / dx : 0;
                int passHeight = header.Height > y0 ? (header.Height - y0 + dy - 1) / dy : 0;
                if (passWidth == 0 || passHeight == 0)
                {
                    continue;
                }

                int rowBytes = header.RowBytes(passWidth);
                previous.AsSpan(0, rowBytes + 1).Clear();
                for (int row = 0; row < passHeight; row++)
                {
                    int read = sync
                        ? zlib.ReadAtLeast(current.AsSpan(0, rowBytes + 1), rowBytes + 1, throwOnEndOfStream: false)
                        : await zlib.ReadAtLeastAsync(current.AsMemory(0, rowBytes + 1), rowBytes + 1, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                    if (read < rowBytes + 1)
                    {
                        throw ImageErrors.Corrupt("The PNG file's image data ends before its last row.");
                    }

                    PngFilters.Unfilter(current.AsSpan(0, rowBytes + 1), previous.AsSpan(1, rowBytes), header.FilterBytesPerPixel);
                    int y = y0 + (row * dy);
                    if (scattered is null)
                    {
                        converter.Convert(current.AsSpan(1, rowBytes), image.GetRowBytes(y), passWidth);
                    }
                    else
                    {
                        Span<byte> pixels = scattered.AsSpan(0, passWidth * image.BytesPerPixel);
                        converter.Convert(current.AsSpan(1, rowBytes), pixels, passWidth);
                        Scatter(pixels, image.GetRowBytes(y), image.BytesPerPixel, x0, dx);
                    }

                    (current, previous) = (previous, current);
                }
            }

            // One more byte makes zlib read its end and check the Adler-32. Data past the image isn't inflated.
            byte[] probe = new byte[1];
            _ = sync ? zlib.Read(probe) : await zlib.ReadAsync(probe, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw new ImageFormatException("The PNG file's compressed image data is damaged.", ex);
        }
        finally
        {
            await zlib.DisposeAsync().ConfigureAwait(false);
            ArrayPool<byte>.Shared.Return(current);
            ArrayPool<byte>.Shared.Return(previous);
            if (scattered is not null)
            {
                ArrayPool<byte>.Shared.Return(scattered);
            }
        }
    }

    private static void Scatter(ReadOnlySpan<byte> pixels, Span<byte> row, int bytesPerPixel, int x0, int dx)
    {
        int step = dx * bytesPerPixel;
        int to = x0 * bytesPerPixel;
        for (int from = 0; from < pixels.Length; from += bytesPerPixel, to += step)
        {
            pixels.Slice(from, bytesPerPixel).CopyTo(row[to..]);
        }
    }

    // After the image data: the rest of the IDAT run, then any chunks up to IEND.
    private static async ValueTask ReadToEndAsync(DataReader reader, IdatStream idat, byte[] head, CancellationToken cancellationToken)
    {
        (long length, uint type) = await idat.SkipRestAsync(cancellationToken).ConfigureAwait(false);
        while (type != Iend)
        {
            if (type == Idat)
            {
                throw ImageErrors.Corrupt("The PNG file's IDAT chunks aren't consecutive.");
            }

            if (type is Plte or Ihdr)
            {
                throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The PNG file has a {ChunkName(type)} chunk after its image data."));
            }

            RefuseUnknownCritical(type);
            await reader.SkipAsync(length + CrcLength, cancellationToken).ConfigureAwait(false);
            (length, type) = await ReadChunkHeaderAsync(reader, head, cancellationToken).ConfigureAwait(false);
        }

        if (length != 0)
        {
            throw ImageErrors.Corrupt("The PNG file's IEND chunk isn't empty.");
        }

        _ = await ReadChunkDataAsync(reader, type, length, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<byte[]> ReadPaletteAsync(
        DataReader reader, PngHeader header, byte[]? existing, long length, CancellationToken cancellationToken)
    {
        if (header.ColourType is 0 or 4)
        {
            throw ImageErrors.Corrupt("The PNG file is greyscale but has a PLTE chunk.");
        }

        if (existing is not null)
        {
            throw ImageErrors.Corrupt("The PNG file has more than one PLTE chunk.");
        }

        if (length == 0 || length % 3 != 0 || length > 256 * 3)
        {
            throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The PNG file's PLTE chunk is {length} bytes; it must hold 1 to 256 three-byte entries."));
        }

        byte[] palette = await ReadChunkDataAsync(reader, Plte, length, cancellationToken).ConfigureAwait(false);
        if (header.ColourType == 3 && length / 3 > 1 << header.BitDepth)
        {
            throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The PNG file's palette has {length / 3} entries, more than {header.BitDepth}-bit indices can reach."));
        }

        // A palette in an RGB file is only a suggestion for screens with few colours: it's checked, then ignored.
        return palette;
    }

    private static async ValueTask<byte[]> ReadTransparencyAsync(
        DataReader reader, PngHeader header, byte[]? palette, byte[]? existing, long length, CancellationToken cancellationToken)
    {
        if (existing is not null)
        {
            throw ImageErrors.Corrupt("The PNG file has more than one tRNS chunk.");
        }

        bool valid = header.ColourType switch
        {
            0 => length == 2,
            2 => length == 6,
            _ => palette is not null && length <= palette.Length / 3,
        };
        if (!valid)
        {
            throw ImageErrors.Corrupt(header.ColourType == 3 && palette is null
                ? "The PNG file's tRNS chunk comes before its PLTE chunk."
                : string.Create(CultureInfo.InvariantCulture, $"The PNG file's tRNS chunk has the wrong length ({length} bytes) for its colour type."));
        }

        return await ReadChunkDataAsync(reader, Trns, length, cancellationToken).ConfigureAwait(false);
    }

    private static void RefuseUnknownCritical(uint type)
    {
        // Bit 5 of the first byte clear marks a critical chunk: one a decoder must understand to show the image.
        if ((type & 0x2000_0000) == 0)
        {
            throw new UnsupportedImageFormatException(
                string.Create(CultureInfo.InvariantCulture, $"The PNG file has a critical chunk, {ChunkName(type)}, that this decoder doesn't know."),
                ImageFormat.Png);
        }
    }

    internal static async ValueTask<(long Length, uint Type)> ReadChunkHeaderAsync(DataReader reader, byte[] head, CancellationToken cancellationToken)
    {
        await reader.ReadExactlyAsync(head.AsMemory(0, 8), cancellationToken).ConfigureAwait(false);
        long length = BinaryPrimitives.ReadUInt32BigEndian(head);
        uint type = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
        if (length > int.MaxValue)
        {
            throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The PNG file's {ChunkName(type)} chunk claims {length} bytes, more than the format allows."));
        }

        return (length, type);
    }

    /// <summary>
    /// The colour chunks before the image data. The PNG specification's precedence decides: <c>cICP</c>, then
    /// <c>iCCP</c>, then <c>sRGB</c>, then <c>gAMA</c> and <c>cHRM</c>. A second chunk of a kind is ignored.
    /// </summary>
    private sealed class ColourChunks
    {
        private byte[]? _cicp;
        private byte[]? _iccProfile;
        private bool _srgb;
        private int _gamma;
        private int[]? _chromaticities;

        public async ValueTask ReadAsync(DataReader reader, uint type, long length, CancellationToken cancellationToken)
        {
            bool seen = type switch
            {
                Cicp => _cicp is not null,
                Iccp => _iccProfile is not null,
                SrgbChunk => _srgb,
                Gama => _gamma != 0,
                _ => _chromaticities is not null,
            };
            if (seen)
            {
                await reader.SkipAsync(length + CrcLength, cancellationToken).ConfigureAwait(false);
                return;
            }

            long expected = type switch
            {
                Cicp => 4,
                SrgbChunk => 1,
                Gama => 4,
                Chrm => 32,
                _ => -1,
            };
            if (expected >= 0 && length != expected)
            {
                throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The PNG file's {ChunkName(type)} chunk is {length} bytes; it must be {expected}."));
            }

            if (type == Iccp && length > ColorSpace.MaxIccProfileBytes)
            {
                throw ImageErrors.ExceedsLimits(string.Create(CultureInfo.InvariantCulture, $"The PNG file's iCCP chunk is {length} bytes, more than the {ColorSpace.MaxIccProfileBytes}-byte limit for a profile."));
            }

            byte[] data = await ReadChunkDataAsync(reader, type, length, cancellationToken).ConfigureAwait(false);
            switch (type)
            {
                case Cicp:
                    _cicp = data;
                    break;

                case Iccp:
                    _iccProfile = InflateProfile(data);
                    break;

                case SrgbChunk:
                    _srgb = true;
                    break;

                case Gama:
                    _gamma = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data), int.MaxValue);
                    if (_gamma == 0)
                    {
                        throw ImageErrors.Corrupt("The PNG file's gAMA chunk gives a gamma of 0.");
                    }

                    break;

                default:
                    _chromaticities = new int[8];
                    for (int i = 0; i < 8; i++)
                    {
                        _chromaticities[i] = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i * 4)), int.MaxValue);
                    }

                    break;
            }
        }

        public ColorSpace Resolve()
        {
            if (_cicp is not null)
            {
                return ColorSpace.FromCicp(_cicp[0], _cicp[1], _cicp[2], _cicp[3] != 0, _iccProfile);
            }

            if (_iccProfile is not null)
            {
                return ColorSpace.FromCheckedIccProfile(_iccProfile);
            }

            return _srgb || (_gamma == 0 && _chromaticities is null) ? ColorSpace.Srgb : ColorSpace.FromPngGamma(_gamma, _chromaticities);
        }

        // iCCP: a profile name of 1 to 79 bytes, a zero byte, compression method 0, then the zlib-compressed profile.
        private static byte[] InflateProfile(byte[] data)
        {
            int nameEnd = data.AsSpan(0, Math.Min(data.Length, 80)).IndexOf((byte)0);
            if (nameEnd < 1 || nameEnd + 1 >= data.Length || data[nameEnd + 1] != 0)
            {
                throw ImageErrors.Corrupt("The PNG file's iCCP chunk doesn't start with a profile name and compression method 0.");
            }

            byte[] profile;
            try
            {
                using var zlib = new ZLibStream(new MemoryStream(data, nameEnd + 2, data.Length - nameEnd - 2), CompressionMode.Decompress);
                using var inflated = new MemoryStream();
                byte[] block = new byte[16 * 1024];
                int read;
                while ((read = zlib.Read(block)) > 0)
                {
                    // Bounded: a small chunk can't inflate into an unbounded profile.
                    if (inflated.Length + read > ColorSpace.MaxIccProfileBytes)
                    {
                        throw ImageErrors.ExceedsLimits(string.Create(CultureInfo.InvariantCulture, $"The PNG file's ICC profile inflates to more than the {ColorSpace.MaxIccProfileBytes}-byte limit."));
                    }

                    inflated.Write(block, 0, read);
                }

                profile = inflated.ToArray();
            }
            catch (InvalidDataException ex)
            {
                throw new ImageFormatException("The PNG file's compressed ICC profile is damaged.", ex);
            }

            return ColorSpace.CheckIccProfile(profile) is null
                ? profile
                : throw ImageErrors.Corrupt("The PNG file's iCCP chunk doesn't hold an ICC profile.");
        }
    }

    // Reads a chunk's data and CRC, checks the CRC, and returns the data.
    private static async ValueTask<byte[]> ReadChunkDataAsync(DataReader reader, uint type, long length, CancellationToken cancellationToken)
    {
        byte[] data = new byte[length + CrcLength];
        await reader.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        CheckCrc(type, data.AsSpan(0, (int)length), BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)length)));
        return data[..(int)length];
    }

    internal static void CheckCrc(uint type, ReadOnlySpan<byte> data, uint expected)
    {
        Span<byte> name = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(name, type);
        if (Crc32.Append(Crc32.Compute(name), data) != expected)
        {
            throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The PNG file's {ChunkName(type)} chunk fails its CRC check."));
        }
    }

    internal static string ChunkName(uint type)
    {
        Span<char> name = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            char c = (char)((type >> (24 - (i * 8))) & 0xFF);
            if (!char.IsAsciiLetter(c))
            {
                return string.Create(CultureInfo.InvariantCulture, $"0x{type:X8}");
            }

            name[i] = c;
        }

        return new string(name);
    }
}
