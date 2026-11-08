using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Imaging.Codecs.Png;

/// <summary>
/// Writes an image as a PNG: grey, RGB or RGBA, 8 or 16 bits, as the image holds it, with its colour space. No other
/// metadata is written.
/// </summary>
/// <remarks>
/// Each row gets the filter whose output has the smallest sum of absolute values, read as signed bytes: the heuristic
/// the PNG specification suggests. Level 0 stores rows unfiltered. Choosing a smaller colour type is the optimiser's job.
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
    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "The synchronous branches serve SaveAsPng, which must not block on async.")]
    public static async ValueTask EncodeAsync(Image image, Stream output, PngEncoderOptions options, bool sync, CancellationToken cancellationToken)
    {
        byte colourType = image.PixelFormat switch
        {
            PixelFormat.L8 or PixelFormat.L16 => 0,
            PixelFormat.Rgb24 or PixelFormat.Rgb48 => 2,
            _ => 6,
        };
        bool wide = image.PixelFormat is PixelFormat.L16 or PixelFormat.Rgb48 or PixelFormat.Rgba64;

        byte[] header = new byte[8 + 12 + 13];
        Signature.CopyTo(header);
        Span<byte> ihdr = header.AsSpan(8 + 8, 13);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)image.Height);
        ihdr[8] = wide ? (byte)16 : (byte)8;
        ihdr[9] = colourType;
        PngChunkWriter.Frame(header.AsSpan(8), "IHDR"u8, 13);
        await WriteAsync(output, header, sync, cancellationToken).ConfigureAwait(false);

        byte[] colour = ColourChunks(image.ColorSpace);
        if (colour.Length > 0)
        {
            await WriteAsync(output, colour, sync, cancellationToken).ConfigureAwait(false);
        }

        byte[] metadata = MetadataChunks(image, options.Metadata);
        if (metadata.Length > 0)
        {
            await WriteAsync(output, metadata, sync, cancellationToken).ConfigureAwait(false);
        }

        int bytesPerPixel = image.PixelFormat == PixelFormat.Bgra32 ? 4 : image.BytesPerPixel;
        int rowBytes = image.Width * bytesPerPixel;
        byte[] raw = ArrayPool<byte>.Shared.Rent(rowBytes);
        byte[] previous = ArrayPool<byte>.Shared.Rent(rowBytes);
        byte[] best = ArrayPool<byte>.Shared.Rent(rowBytes + 1);
        byte[] trial = ArrayPool<byte>.Shared.Rent(rowBytes + 1);
        var chunks = new PngChunkWriter(output, sync);
        var zlib = new ZLibStream(chunks, new ZLibCompressionOptions { CompressionLevel = options.CompressionLevel }, leaveOpen: true);
        try
        {
            previous.AsSpan(0, rowBytes).Clear();
            for (int y = 0; y < image.Height; y++)
            {
                byte[] chosen = FilterRow(image, y, raw.AsSpan(0, rowBytes), previous.AsSpan(0, rowBytes), bytesPerPixel, options.CompressionLevel, best, trial);
                if (sync)
                {
                    zlib.Write(chosen, 0, rowBytes + 1);
                }
                else
                {
                    await zlib.WriteAsync(chosen.AsMemory(0, rowBytes + 1), cancellationToken).ConfigureAwait(false);
                }

                (raw, previous) = (previous, raw);
            }

            if (sync)
            {
                zlib.Dispose();
            }
            else
            {
                await zlib.DisposeAsync().ConfigureAwait(false);
            }

            await chunks.FinishAsync(cancellationToken).ConfigureAwait(false);

            byte[] end = new byte[12];
            PngChunkWriter.Frame(end, "IEND"u8, 0);
            await WriteAsync(output, end, sync, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await zlib.DisposeAsync().ConfigureAwait(false);
            ArrayPool<byte>.Shared.Return(raw);
            ArrayPool<byte>.Shared.Return(previous);
            ArrayPool<byte>.Shared.Return(best);
            ArrayPool<byte>.Shared.Return(trial);
        }
    }

    // Fills `raw` and returns the buffer (best or trial) that holds the chosen filter byte and filtered row.
    private static byte[] FilterRow(
        Image image, int y, Span<byte> raw, ReadOnlySpan<byte> previous, int bytesPerPixel, int level, byte[] best, byte[] trial)
    {
        ReadOnlySpan<byte> pixels = image.GetRowBytes(y);
        if (image.PixelFormat is PixelFormat.L16 or PixelFormat.Rgb48 or PixelFormat.Rgba64)
        {
            // PNG samples are big-endian; the image's are in the machine's order.
            ReadOnlySpan<ushort> samples = MemoryMarshal.Cast<byte, ushort>(pixels);
            for (int i = 0; i < samples.Length; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(raw[(i * 2)..], samples[i]);
            }
        }
        else if (image.PixelFormat == PixelFormat.Bgra32)
        {
            for (int i = 0; i < raw.Length; i += 4)
            {
                raw[i] = pixels[i + 2];
                raw[i + 1] = pixels[i + 1];
                raw[i + 2] = pixels[i];
                raw[i + 3] = pixels[i + 3];
            }
        }
        else
        {
            pixels.CopyTo(raw);
        }

        best[0] = 0;
        raw.CopyTo(best.AsSpan(1));
        if (level == 0)
        {
            return best;
        }

        // Each filter scored in the same pass that applies it; the winner swaps into `best`, so nothing is copied.
        long bestScore = PngFilters.Score(raw);
        for (int filter = 1; filter <= 4; filter++)
        {
            long score = PngFilters.FilterAndScore(filter, raw, previous, bytesPerPixel, trial.AsSpan(1, raw.Length));
            if (score < bestScore)
            {
                bestScore = score;
                trial[0] = (byte)filter;
                (best, trial) = (trial, best);
            }
        }

        return best;
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
