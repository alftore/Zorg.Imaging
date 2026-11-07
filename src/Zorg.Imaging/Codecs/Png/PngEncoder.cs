using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Imaging.Codecs.Png;

/// <summary>Writes an image as an 8-bit PNG: grey, RGB or RGBA, as the image holds it. No metadata is written.</summary>
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
            PixelFormat.L8 => 0,
            PixelFormat.Rgb24 => 2,
            _ => 6,
        };

        byte[] header = new byte[8 + 12 + 13];
        Signature.CopyTo(header);
        Span<byte> ihdr = header.AsSpan(8 + 8, 13);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)image.Height);
        ihdr[8] = 8;
        ihdr[9] = colourType;
        PngChunkWriter.Frame(header.AsSpan(8), "IHDR"u8, 13);
        await WriteAsync(output, header, sync, cancellationToken).ConfigureAwait(false);

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
                FilterRow(image, y, raw.AsSpan(0, rowBytes), previous.AsSpan(0, rowBytes), bytesPerPixel, options.CompressionLevel, best, trial);
                if (sync)
                {
                    zlib.Write(best, 0, rowBytes + 1);
                }
                else
                {
                    await zlib.WriteAsync(best.AsMemory(0, rowBytes + 1), cancellationToken).ConfigureAwait(false);
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

    private static void FilterRow(
        Image image, int y, Span<byte> raw, ReadOnlySpan<byte> previous, int bytesPerPixel, int level, byte[] best, byte[] trial)
    {
        ReadOnlySpan<byte> pixels = image.GetRowBytes(y);
        if (image.PixelFormat == PixelFormat.Bgra32)
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
            return;
        }

        long bestScore = Score(best.AsSpan(1, raw.Length));
        for (int filter = 1; filter <= 4; filter++)
        {
            PngFilters.Filter(filter, raw, previous, bytesPerPixel, trial.AsSpan(1, raw.Length));
            long score = Score(trial.AsSpan(1, raw.Length));
            if (score < bestScore)
            {
                bestScore = score;
                trial[0] = (byte)filter;
                trial.AsSpan(0, raw.Length + 1).CopyTo(best);
            }
        }
    }

    private static long Score(ReadOnlySpan<byte> filtered)
    {
        long sum = 0;
        foreach (byte b in filtered)
        {
            sum += Math.Abs((int)(sbyte)b);
        }

        return sum;
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
