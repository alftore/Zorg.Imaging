using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Formats;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Codecs.Gif;

/// <summary>Decodes a GIF's first frame, drawn on its canvas, as a still image.</summary>
/// <remarks>
/// The image is the canvas after the first frame: where that frame doesn't reach, or its pixels are transparent, the
/// image is transparent. Reading stops after the first frame, so a stream is left there. A first frame that fills the
/// canvas, as almost all do, goes from its colour indices straight to the image, in bands of rows on several cores.
/// </remarks>
internal static class GifDecoder
{
    public static async ValueTask<Image> DecodeAsync(DataReader reader, DecodeOptions options, CancellationToken cancellationToken)
    {
        GifReader gif = await GifReader.OpenAsync(reader, options.Limits, cancellationToken, keepFirstFrame: true).ConfigureAwait(false);
        if (!await gif.NextFrameAsync(cancellationToken).ConfigureAwait(false))
        {
            throw ImageErrors.Corrupt("The GIF file has no image.");
        }

        reader.Finish();
        Image image = gif.FirstFrame is { } frame ? FromFrame(frame, gif.Width, gif.Height, options.PixelFormat) : FromCanvas(gif.Canvas, gif.Width, gif.Height, options.PixelFormat);
        image.ColorSpace = gif.ColorSpace;
        return image;
    }

    private static Image FromCanvas(uint[] canvas, int width, int height, PixelFormat requested)
    {
        bool alpha = false;
        foreach (uint p in canvas)
        {
            if (p < 0xFF000000u)
            {
                alpha = true;
                break;
            }
        }

        PixelFormat format = requested != PixelFormat.None ? requested : alpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24;
        return Argb.ToImage(canvas, width, height, alpha, format);
    }

    private static Image FromFrame(GifReader.Frame frame, int width, int height, PixelFormat requested)
    {
        try
        {
            int pixels = width * height;
            bool alpha = frame.Decoded < pixels;
            int transparent = Array.IndexOf(frame.Colours, 0u);
            if (!alpha && transparent >= 0)
            {
                alpha = frame.Indices.AsSpan(0, pixels).Contains((byte)transparent);
            }

            PixelFormat format = requested != PixelFormat.None ? requested : alpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24;
            var image = new Image(width, height, format);
            int bands = Parallelism.Bands(pixels, height);
            Parallelism.For(bands, band =>
            {
                Span<uint> row = new uint[width];
                int from = (int)((long)height * band / bands);
                int to = (int)((long)height * (band + 1) / bands);
                for (int stored = 0; stored < height; stored++)
                {
                    int y = frame.Interlaced ? GifReader.InterlacedRow(stored, height) : stored;
                    if (y < from || y >= to)
                    {
                        continue;
                    }

                    int start = stored * width;
                    int available = Math.Clamp(frame.Decoded - start, 0, width);
                    ReadOnlySpan<byte> indices = frame.Indices.AsSpan(start, available);
                    for (int x = 0; x < indices.Length; x++)
                    {
                        row[x] = frame.Colours[indices[x]];
                    }

                    row[available..].Clear();
                    Argb.WriteRow(row, image.GetRowBytes(y), format, alpha);
                }
            });

            return image;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame.Indices);
        }
    }
}
