using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Codecs.Png;

/// <summary>
/// Writes an image as a PNG: in the smallest layout that holds it exactly (<see cref="PngLayout"/>) or its own, with
/// its colour space and, when asked, its EXIF and XMP.
/// </summary>
/// <remarks>
/// Rows are filtered and compressed in bands of about a megabyte on as many cores as allowed
/// (<see cref="PngDeflate"/>), and written in order as each group of bands is done. Level 0 stores rows unfiltered.
/// </remarks>
internal static class PngEncoder
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Encodes the image.</summary>
    /// <param name="image">The image.</param>
    /// <param name="output">Where the file goes.</param>
    /// <param name="options">The options.</param>
    /// <param name="sync">Whether to write synchronously; the method then completes synchronously.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    public static async ValueTask EncodeAsync(Image image, Stream output, PngEncoderOptions options, bool sync, CancellationToken cancellationToken)
    {
        using Parallelism.Scope scope = Parallelism.Use(options.MaxDegreeOfParallelism);
        if (options.MaxColors > 0)
        {
            using Image reduced = Reduce(image, options.MaxColors, options.Dithering);
            await EncodeCoreAsync(reduced, output, options, sync, cancellationToken).ConfigureAwait(false);
            return;
        }

        await EncodeCoreAsync(image, output, options, sync, cancellationToken).ConfigureAwait(false);
    }

    // Lossy PNG: the image in at most maxColours colours with full alpha, as 8-bit RGB or RGBA, so the layout chosen
    // after is a palette (with a tRNS entry per colour) when colour types are reduced.
    private static Image Reduce(Image image, int maxColours, double dither)
    {
        (uint[] argb, _) = Argb.FromImage(image);
        RgbaQuantizer.Result result = RgbaQuantizer.Run(argb, image.Width, image.Height, maxColours, dither);
        bool alpha = Array.Exists(result.Palette, p => p < 0xFF000000u);
        for (int i = 0; i < argb.Length; i++)
        {
            argb[i] = result.Palette[result.Indices[i]];
        }

        Image reduced = Argb.ToImage(argb, image.Width, image.Height, alpha, alpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24);
        reduced.ColorSpace = image.ColorSpace;
        reduced.SourceMetadata = image.SourceMetadata;
        return reduced;
    }

    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "The synchronous branches serve SaveAsPng, which must not block on async.")]
    private static async ValueTask EncodeCoreAsync(Image image, Stream output, PngEncoderOptions options, bool sync, CancellationToken cancellationToken)
    {
        PngLayout layout = options.ReduceColorType
            ? PngLayout.Choose(image, options.CleanTransparentPixels)
            : PngLayout.Of(image, options.CleanTransparentPixels);

        // The layout is chosen by the bytes it stores, which always favours a palette; but a palette's rows are seldom
        // filtered, and sometimes the colour rows, filtered, compress smaller. Optimize tries both quickly first.
        if (options.Optimize && layout.ColourType == 3)
        {
            PngLayout colour = PngLayout.Choose(image, options.CleanTransparentPixels, allowPalette: false);
            if (TrialSize(image, colour) < TrialSize(image, layout))
            {
                layout = colour;
            }
        }

        byte[] header = new byte[8 + 12 + 13];
        Signature.CopyTo(header);
        Span<byte> ihdr = header.AsSpan(8 + 8, 13);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)image.Height);
        ihdr[8] = layout.BitDepth;
        ihdr[9] = layout.ColourType;
        PngChunkWriter.Frame(header.AsSpan(8), "IHDR"u8, 13);
        await WriteAsync(output, header, sync, cancellationToken).ConfigureAwait(false);

        // Colour chunks come before PLTE, and tRNS after it; all before the image data.
        using (var chunks = new MemoryStream())
        {
            chunks.Write(ColourChunks(image.ColorSpace));
            if (layout.Palette is not null)
            {
                WriteChunk(chunks, "PLTE"u8, layout.Palette);
            }

            if (layout.Transparency is not null)
            {
                WriteChunk(chunks, "tRNS"u8, layout.Transparency);
            }

            chunks.Write(MetadataChunks(image, options.Metadata));
            if (chunks.Length > 0)
            {
                await WriteAsync(output, chunks.ToArray(), sync, cancellationToken).ConfigureAwait(false);
            }
        }

        int level = options.Optimize ? 9 : options.CompressionLevel;
        PngFilter filter = PngDeflate.Resolve(options.Filter, layout, level);
        int rowsPerBand = PngDeflate.RowsPerBand(layout.RowBytes(image.Width));
        int bands = (image.Height + rowsPerBand - 1) / rowsPerBand;
        int group = Math.Max(1, Parallelism.Degree) * 2;
        using var idat = new PngChunkWriter(output, sync);
        await idat.WriteAsync(PngDeflate.ZlibHeader(level), cancellationToken).ConfigureAwait(false);
        uint adler = 1;
        var done = new PngDeflate.Band[Math.Min(group, bands)];
        for (int start = 0; start < bands; start += group)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(group, bands - start);
            Parallelism.For(count, i =>
            {
                int band = start + i;
                int first = band * rowsPerBand;
                done[i] = PngDeflate.Compress(image, layout, first, Math.Min(rowsPerBand, image.Height - first), filter, level, options.Optimize, last: band == bands - 1);
            });

            for (int i = 0; i < count; i++)
            {
                PngDeflate.Band band = done[i];
                adler = PngDeflate.CombineAdler(adler, band.Adler, band.RawLength);
                if (sync)
                {
                    idat.Write(band.Bytes, 0, band.Length);
                }
                else
                {
                    await idat.WriteAsync(band.Bytes.AsMemory(0, band.Length), cancellationToken).ConfigureAwait(false);
                }

                done[i] = default;
            }
        }

        byte[] trailer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(trailer, adler);
        if (sync)
        {
            idat.Write(trailer);
        }
        else
        {
            await idat.WriteAsync(trailer, cancellationToken).ConfigureAwait(false);
        }

        await idat.FinishAsync(cancellationToken).ConfigureAwait(false);

        byte[] end = new byte[12];
        PngChunkWriter.Frame(end, "IEND"u8, 0);
        await WriteAsync(output, end, sync, cancellationToken).ConfigureAwait(false);
    }

    // The file's size near enough to compare layouts: every band at zlib level 9 with the layout's usual filter, and the
    // palette and transparency chunks.
    private static long TrialSize(Image image, PngLayout layout)
    {
        PngFilter filter = PngDeflate.Resolve(PngFilter.Adaptive, layout, 9);
        int rowsPerBand = PngDeflate.RowsPerBand(layout.RowBytes(image.Width));
        int bands = (image.Height + rowsPerBand - 1) / rowsPerBand;
        long[] sizes = new long[bands];
        Parallelism.For(bands, band =>
        {
            int first = band * rowsPerBand;
            sizes[band] = PngDeflate.Compress(image, layout, first, Math.Min(rowsPerBand, image.Height - first), filter, 9, optimize: false, last: band == bands - 1).Length;
        });

        long total = 0;
        foreach (long size in sizes)
        {
            total += size;
        }

        return total + (layout.Palette is { } palette ? palette.Length + 12 : 0) + (layout.Transparency is { } key ? key.Length + 12 : 0);
    }

    /// <summary>
    /// The chunks that say what the colours mean: none for sRGB, which is what an untagged file is taken to be;
    /// otherwise <c>cICP</c> for code points, <c>iCCP</c> for a profile, or <c>gAMA</c> and <c>cHRM</c> as they were read.
    /// </summary>
    /// <param name="space">The image's colour space.</param>
    /// <returns>The chunks, framed, or empty.</returns>
    internal static byte[] ColourChunks(ColorSpace space)
    {
        if (space.IsSrgb)
        {
            return [];
        }

        using var chunks = new MemoryStream();
        if (space.CicpPrimaries is int primaries && space.CicpTransfer is int transfer)
        {
            WriteChunk(chunks, "cICP"u8, [(byte)primaries, (byte)transfer, (byte)space.CicpMatrix, space.CicpFullRange ? (byte)1 : (byte)0]);
        }

        if (!space.IccProfile.IsEmpty)
        {
            using var data = new MemoryStream();
            data.Write("ICC profile\0\0"u8);
            using (var zlib = new ZLibStream(data, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(space.IccProfile.Span);
            }

            WriteChunk(chunks, "iCCP"u8, data.GetBuffer().AsSpan(0, (int)data.Length));
        }

        if (space.CicpPrimaries is null && space.IccProfile.IsEmpty)
        {
            if (space.PngGamma != 0)
            {
                byte[] gamma = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(gamma, (uint)space.PngGamma);
                WriteChunk(chunks, "gAMA"u8, gamma);
            }

            ReadOnlySpan<int> chromaticities = space.PngChromaticities;
            if (!chromaticities.IsEmpty)
            {
                byte[] values = new byte[32];
                for (int i = 0; i < 8; i++)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(values.AsSpan(i * 4), (uint)chromaticities[i]);
                }

                WriteChunk(chunks, "cHRM"u8, values);
            }
        }

        return chunks.ToArray();
    }

    // eXIf and an uncompressed iTXt chunk with the XMP packet, both before the image data, where readers look.
    private static byte[] MetadataChunks(Image image, MetadataOptions options)
    {
        (byte[]? exif, byte[]? xmp) = Metadata.MetadataWriter.Prepare(image, options);
        using var chunks = new MemoryStream();
        if (exif is not null)
        {
            WriteChunk(chunks, "eXIf"u8, exif);
        }

        if (xmp is not null)
        {
            // Keyword, no compression, no language, no translated keyword, then the text.
            WriteChunk(chunks, "iTXt"u8, [.. "XML:com.adobe.xmp\0"u8, 0, 0, 0, 0, .. xmp]);
        }

        return chunks.ToArray();
    }

    private static void WriteChunk(MemoryStream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        byte[] chunk = new byte[data.Length + 12];
        data.CopyTo(chunk.AsSpan(8));
        PngChunkWriter.Frame(chunk, type, data.Length);
        output.Write(chunk);
    }

    internal static ValueTask WriteAsync(Stream output, ReadOnlyMemory<byte> data, bool sync, CancellationToken cancellationToken)
    {
        if (sync)
        {
            output.Write(data.Span);
            return ValueTask.CompletedTask;
        }

        return output.WriteAsync(data, cancellationToken);
    }
}
