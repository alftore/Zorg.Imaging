using System;
using System.Buffers;
using System.Runtime.InteropServices;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Codecs.Jpeg;

/// <summary>
/// Turns decoded coefficients into an image: the inverse DCT per component, chroma upsampling, and YCbCr to RGB.
/// </summary>
/// <remarks>
/// <para>
/// Chroma stored at half size (or less) is brought to full size as cheaply and smoothly as possible. When decoding at a
/// reduced scale, a chroma block is transformed at a larger size instead, across and down separately, so at 1/2 scale
/// 4:2:0, 4:2:2 and 4:4:0 chroma need no upsampling at all. Otherwise a 2x step uses the triangle filter (3/4 of the
/// nearer sample, 1/4 of the further), with samples centred between luma samples as JFIF places them, rounded as
/// libjpeg's "fancy" upsampling rounds; other ratios repeat samples.
/// </para>
/// <para>
/// YCbCr to RGB follows JFIF (T.871, section 7), full range, in libjpeg's 16.16 fixed point. With the integer inverse
/// DCT, a full-size decode is byte for byte what libjpeg-turbo's <c>djpeg</c> gives with its defaults.
/// </para>
/// </remarks>
internal static class JpegOutput
{

    public static Image Build(JpegFrame frame, int scale, bool isRgb, PixelFormat requested, JpegCmyk? cmyk = null)
    {
        Plane[] planes = CreatePlanes(frame, scale);
        try
        {
            for (int i = 0; i < planes.Length; i++)
            {
                JpegComponent component = frame.Components[i];
                planes[i].Transform(component, component.Coefficients, component.FirstRow, 0, component.UsedHigh);
            }

            return Compose(frame, scale, planes, isRgb, requested, cmyk);
        }
        finally
        {
            Dispose(planes);
        }
    }

    /// <summary>One plane of samples for each component, at the scale's size; the caller disposes them.</summary>
    public static Plane[] CreatePlanes(JpegFrame frame, int scale)
    {
        var planes = new Plane[frame.Components.Length];
        try
        {
            for (int i = 0; i < planes.Length; i++)
            {
                planes[i] = Plane.Create(frame, frame.Components[i], scale);
            }

            return planes;
        }
        catch
        {
            Dispose(planes);
            throw;
        }
    }

    public static void Dispose(Plane[] planes)
    {
        foreach (Plane plane in planes)
        {
            plane?.Dispose();
        }
    }

    /// <summary>Upsamples and converts the finished planes into the image.</summary>
    public static Image Compose(JpegFrame frame, int scale, Plane[] planes, bool isRgb, PixelFormat requested, JpegCmyk? cmyk = null)
    {
        int width = JpegFrame.DivideUp((long)frame.Width * scale, 8);
        int height = JpegFrame.DivideUp((long)frame.Height * scale, 8);
        bool grey = frame.Components.Length == 1;
        PixelFormat format = requested != PixelFormat.None ? requested : grey ? PixelFormat.L8 : PixelFormat.Rgb24;
        var image = new Image(width, height, format);
        try
        {
            // Rows are independent once the planes exist: large images are converted in parallel bands.
            int bands = Parallelism.Bands((long)width * height, height / 16);
            Parallelism.For(bands, band =>
            {
                byte[][] rows = new byte[planes.Length][];
                for (int i = 0; i < rows.Length; i++)
                {
                    rows[i] = new byte[width];
                }

                int end = (int)((long)height * (band + 1) / bands);
                for (int y = (int)((long)height * band / bands); y < end; y++)
                {
                    if (cmyk is not null)
                    {
                        for (int i = 0; i < planes.Length; i++)
                        {
                            planes[i].Row(y, rows[i]);
                        }

                        cmyk.WriteRow(rows, image.GetRowBytes(y), image.PixelFormat, width);
                    }
                    else if (grey)
                    {
                        WriteRow(image, y, planes[0].View(y, rows[0]), default, default, grey: true, isRgb);
                    }
                    else
                    {
                        WriteRow(image, y, planes[0].View(y, rows[0]), planes[1].View(y, rows[1]), planes[2].View(y, rows[2]), grey: false, isRgb);
                    }
                }
            });

            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static void WriteRow(Image image, int y, ReadOnlySpan<byte> c0, ReadOnlySpan<byte> c1, ReadOnlySpan<byte> c2, bool grey, bool isRgb)
    {
        Span<byte> output = image.GetRowBytes(y);
        int width = image.Width;
        PixelFormat format = image.PixelFormat;
        if (grey)
        {
            ReadOnlySpan<byte> l = c0;
            if (format == PixelFormat.L8)
            {
                l[..width].CopyTo(output);
                return;
            }

            for (int x = 0; x < width; x++)
            {
                Put(output, format, x, l[x], l[x], l[x]);
            }

            return;
        }

        if (format == PixelFormat.Rgb24 && !isRgb)
        {
            // The usual case: 16 pixels at a time where the hardware allows, then the rest one by one.
            int done = JpegColour.YccToRgb24(c0, c1, c2, output, width);
            for (int x = done, o = 3 * done; x < width; x++, o += 3)
            {
                int luma = c0[x];
                int cb = c1[x] - 128;
                int cr = c2[x] - 128;
                output[o] = Clamp(luma + (((91881 * cr) + 32768) >> 16));
                output[o + 1] = Clamp(luma + (((-22554 * cb) - (46802 * cr) + 32768) >> 16));
                output[o + 2] = Clamp(luma + (((116130 * cb) + 32768) >> 16));
            }

            return;
        }

        for (int x = 0; x < width; x++)
        {
            if (isRgb)
            {
                Put(output, format, x, c0[x], c1[x], c2[x]);
                continue;
            }

            // JFIF: R = Y + 1.402 Cr', G = Y - 0.344136 Cb' - 0.714136 Cr', B = Y + 1.772 Cb', in 16.16 fixed point.
            int luma = c0[x];
            int cb = c1[x] - 128;
            int cr = c2[x] - 128;
            int r = luma + (((91881 * cr) + 32768) >> 16);
            int g = luma + (((-22554 * cb) - (46802 * cr) + 32768) >> 16);
            int b = luma + (((116130 * cb) + 32768) >> 16);
            Put(output, format, x, (byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));
        }
    }

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);

    private static void Put(Span<byte> output, PixelFormat format, int x, byte r, byte g, byte b)
    {
        switch (format)
        {
            case PixelFormat.L8:
                output[x] = r;
                break;
            case PixelFormat.Rgb24:
                output[3 * x] = r;
                output[(3 * x) + 1] = g;
                output[(3 * x) + 2] = b;
                break;
            case PixelFormat.Rgba32:
                output[4 * x] = r;
                output[(4 * x) + 1] = g;
                output[(4 * x) + 2] = b;
                output[(4 * x) + 3] = 255;
                break;
            case PixelFormat.Bgra32:
                output[4 * x] = b;
                output[(4 * x) + 1] = g;
                output[(4 * x) + 2] = r;
                output[(4 * x) + 3] = 255;
                break;
            default:
                Span<ushort> wide = MemoryMarshal.Cast<byte, ushort>(output);
                wide[4 * x] = (ushort)(r * 257);
                wide[(4 * x) + 1] = (ushort)(g * 257);
                wide[(4 * x) + 2] = (ushort)(b * 257);
                wide[(4 * x) + 3] = 65535;
                break;
        }
    }

    /// <summary>
    /// One component's samples after the inverse DCT, filled block row by block row, and how to bring a row of them to
    /// full size.
    /// </summary>
    internal sealed class Plane : IDisposable
    {
        private byte[] _samples = [];
        private int _stride;
        private int _wide;
        private int _high;
        private int _factorX;
        private int _factorY;

        private short[] _quantisation = [];
        private float[]? _reducedWeights;
        private int _blockWide;
        private int _blockHigh;

        public static Plane Create(JpegFrame frame, JpegComponent component, int scale)
        {
            int factorX = frame.MaxH / component.H;
            int factorY = frame.MaxV / component.V;

            // Below full size, transform chroma larger rather than upsample it, as far as 8 samples a side allows,
            // across and down separately: at 1/2 scale, 4:2:0, 4:2:2 and 4:4:0 chroma all land on luma's grid.
            int wide = scale;
            int high = scale;
            while (wide < 8 && factorX % 2 == 0)
            {
                wide *= 2;
                factorX /= 2;
            }

            while (high < 8 && factorY % 2 == 0)
            {
                high *= 2;
                factorY /= 2;
            }

            var plane = new Plane
            {
                _stride = component.UsedWide * wide,
                _factorX = factorX,
                _factorY = factorY,
                _wide = JpegFrame.DivideUp((long)component.SamplesWide * wide, 8),
                _high = JpegFrame.DivideUp((long)component.SamplesHigh * high, 8),
            };
            int rows = component.UsedHigh * high;
            plane._samples = ArrayPool<byte>.Shared.Rent(plane._stride * rows);
            plane._blockWide = wide;
            plane._blockHigh = high;

            // The quantisation table, stored in zigzag order, in natural order: as the integer transform's 16-bit
            // multipliers at full size, else folded with both passes into one weight row per coefficient.
            ushort[] zigzag = component.Quantisation ?? new ushort[64];
            if (wide != 8 || high != 8)
            {
                float[] quantisation = new float[64];
                for (int k = 0; k < 64; k++)
                {
                    quantisation[JpegIdct.ZigZag[k]] = zigzag[k];
                }

                plane._reducedWeights = JpegIdct.ReducedWeights(quantisation, wide, high);
            }
            else
            {
                plane._quantisation = new short[64];
                for (int k = 0; k < 64; k++)
                {
                    plane._quantisation[JpegIdct.ZigZag[k]] = (short)zigzag[k];
                }
            }

            return plane;
        }

        /// <summary>
        /// Transforms block rows <paramref name="first"/> to <paramref name="first"/> + <paramref name="count"/> of
        /// <paramref name="component"/> into the plane, stopping at the last row that holds samples. The coefficients
        /// are passed rather than read from the component, whose buffer a streaming decode may already have moved on.
        /// With <paramref name="clear"/>, the coefficients of the <paramref name="count"/> block rows are left zero, each
        /// block cleared just after its transform while it is still in cache, ready for the next band.
        /// </summary>
        public void Transform(JpegComponent component, short[] coefficients, int coefficientsFirstRow, int first, int count, bool clear = false)
        {
            int last = Math.Min(first + count, component.UsedHigh);
            if (clear)
            {
                // The rows past the last that holds samples (only in a frame's last band) and the padding blocks right
                // of the used ones are never transformed: cleared here.
                int blocksWide = component.BlocksWide;
                int rows = count;
                int transformed = Math.Max(last - first, 0);
                coefficients.AsSpan((first - coefficientsFirstRow + transformed) * blocksWide * 64, (rows - transformed) * blocksWide * 64).Clear();
                if (component.UsedWide < blocksWide)
                {
                    for (int by = first; by < last; by++)
                    {
                        coefficients.AsSpan((((by - coefficientsFirstRow) * blocksWide) + component.UsedWide) * 64, (blocksWide - component.UsedWide) * 64).Clear();
                    }
                }
            }

            if (last <= first)
            {
                return;
            }

            byte[] samples = _samples;
            int stride = _stride;
            int wide = _blockWide;
            int high = _blockHigh;
            short[] quantisation = _quantisation;
            float[]? reduced = _reducedWeights;

            // Each block row writes its own rows of the plane, so large components are transformed in parallel.
            int bands = Parallelism.Bands((long)component.UsedWide * (last - first) * 64, (last - first) / 4);
            Parallelism.For(bands, band =>
            {
                int end = first + (int)((long)(last - first) * (band + 1) / bands);
                for (int by = first + (int)((long)(last - first) * band / bands); by < end; by++)
                {
                    for (int bx = 0; bx < component.UsedWide; bx++)
                    {
                        Span<short> block = coefficients.AsSpan((((by - coefficientsFirstRow) * component.BlocksWide) + bx) * 64, 64);
                        Span<byte> target = samples.AsSpan((by * high * stride) + (bx * wide));
                        if (reduced is not null)
                        {
                            JpegIdct.TransformReduced(block, reduced, wide, high, target, stride);
                        }
                        else
                        {
                            JpegIdct.Transform(block, quantisation, target, stride);
                        }

                        if (clear)
                        {
                            block.Clear();
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Row <paramref name="y"/> at full size: the plane's own row when it needs no upsampling, else
        /// <paramref name="scratch"/> filled.
        /// </summary>
        public ReadOnlySpan<byte> View(int y, byte[] scratch)
        {
            if (_factorX == 1 && _factorY == 1)
            {
                return _samples.AsSpan(Math.Min(y, _high - 1) * _stride, scratch.Length);
            }

            Row(y, (Span<byte>)scratch);
            return scratch;
        }

        public void Row(int y, Span<byte> output)
        {
            int near = Math.Min(y / _factorY, _high - 1);
            ReadOnlySpan<byte> nearRow = _samples.AsSpan(near * _stride, _wide);
            if (_factorX == 1 && _factorY == 1)
            {
                nearRow[..output.Length].CopyTo(output);
                return;
            }

            if (_factorX > 2 || _factorY > 2)
            {
                for (int x = 0; x < output.Length; x++)
                {
                    output[x] = nearRow[Math.Min(x / _factorX, _wide - 1)];
                }

                return;
            }

            // Triangle filter: vertically 3/4 nearer row + 1/4 further (times 4), then the same across (times 16). The
            // rounding is libjpeg's, which alternates between the two outputs of each pair.
            int far = _factorY == 2 ? Math.Clamp(near + ((y & 1) == 0 ? -1 : 1), 0, _high - 1) : near;
            ReadOnlySpan<byte> farRow = _samples.AsSpan(far * _stride, _wide);
            if (_factorX == 1)
            {
                // Only down (4:4:0): the upper row of each pair rounds with 1, the lower with 2.
                int bias = 1 + (y & 1);
                for (int x = 0; x < output.Length; x++)
                {
                    output[x] = (byte)(((3 * nearRow[x]) + farRow[x] + bias) >> 2);
                }

                return;
            }

            // Each column sum once, then each output pixel from its own and its neighbour's.
            int columns = (output.Length + 1) >> 1;
            Span<short> sums = columns <= 4096 ? stackalloc short[columns] : new short[columns];
            JpegColour.ColumnSums(nearRow[..columns], _factorY == 2 ? farRow[..columns] : nearRow[..columns], _factorY == 2, sums);
            if (_factorY == 2)
            {
                JpegColour.UpsampleTwice(sums, output, 8, 7);
            }
            else
            {
                JpegColour.UpsampleTwice(sums, output, 4, 8);
            }
        }

        public void Dispose()
        {
            if (_samples.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_samples);
                _samples = [];
            }
        }
    }
}
