using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Zorg.Imaging.Processing;

/// <summary>Separable two-pass resizing with precomputed weights.</summary>
/// <remarks>
/// <para>
/// Each source row is converted to floats once (to linear light when shrinking, with alpha premultiplied),
/// resampled across, then added into the few output rows it contributes to. So memory is a handful of output-width
/// rows however large the shrink factor, never a copy of the whole image.
/// </para>
/// <para>
/// Weights follow the half-pixel centre convention: output pixel <c>i</c> is centred on source position
/// <c>(i + 0.5) × scale</c>. When shrinking, the filter is widened by the scale. Source pixels outside the image are
/// left out and the remaining weights renormalised, as Pillow and ImageMagick do.
/// </para>
/// </remarks>
internal static class Resizer
{
    public static Image Resize(Image source, int width, int height, Resampler? resampler)
    {
        bool shrinking = width < source.Width || height < source.Height;
        Resampler filterX = resampler ?? (width < source.Width ? Resampler.Lanczos3 : Resampler.Mitchell);
        Resampler filterY = resampler ?? (height < source.Height ? Resampler.Lanczos3 : Resampler.Mitchell);
        var result = new Image(width, height, source.PixelFormat) { ColorSpace = source.ColorSpace };
        if (filterX.IsNearest && filterY.IsNearest)
        {
            Nearest(source, result);
            return result;
        }

        // Shrinking blends many pixels, which is only right in linear light: averaging encoded values darkens fine
        // detail. Enlarging stays in the encoded values, which keeps edges from growing dark halos.
        TransferFunction transfer = shrinking ? TransferFunction.For(source.ColorSpace) : TransferFunction.Identity;
        new Pass(source, result, transfer, Weights.For(source.Width, width, filterX), Weights.For(source.Height, height, filterY)).Run();
        return result;
    }

    private static void Nearest(Image source, Image result)
    {
        int[] columns = NearestIndices(source.Width, result.Width);
        int[] rows = NearestIndices(source.Height, result.Height);
        switch (source.BytesPerPixel)
        {
            case 8:
                Copy<ulong>(source, result, columns, rows);
                break;
            case 6:
                Copy<Rgb48>(source, result, columns, rows);
                break;
            case 4:
                Copy<uint>(source, result, columns, rows);
                break;
            case 3:
                Copy<Rgb24>(source, result, columns, rows);
                break;
            case 2:
                Copy<ushort>(source, result, columns, rows);
                break;
            default:
                Copy<byte>(source, result, columns, rows);
                break;
        }
    }

    private static int[] NearestIndices(int from, int to)
    {
        int[] indices = new int[to];
        for (int i = 0; i < to; i++)
        {
            // floor((i + 0.5) × from / to), in integers.
            indices[i] = (int)Math.Min((((2L * i) + 1) * from) / (2L * to), from - 1);
        }

        return indices;
    }

    private static void Copy<T>(Image source, Image result, int[] columns, int[] rows)
        where T : unmanaged
    {
        for (int y = 0; y < result.Height; y++)
        {
            ReadOnlySpan<T> from = MemoryMarshal.Cast<byte, T>(source.GetRowBytes(rows[y]));
            Span<T> to = MemoryMarshal.Cast<byte, T>(result.GetRowBytes(y));
            for (int x = 0; x < to.Length; x++)
            {
                to[x] = from[columns[x]];
            }
        }
    }

    /// <summary>For each output index, the first source index and the weights of the source indices from there.</summary>
    private sealed class Weights
    {
        private Weights(int count, int stride)
        {
            Start = new int[count];
            Length = new int[count];
            Stride = stride;
            Values = new float[count * stride];
        }

        public int[] Start { get; }

        public int[] Length { get; }

        public int Stride { get; }

        public float[] Values { get; }

        /// <summary>Whether output index i is source index i with weight 1.</summary>
        public bool IsIdentity { get; private init; }

        public static Weights For(int from, int to, Resampler filter)
        {
            if (from == to)
            {
                var identity = new Weights(to, 1) { IsIdentity = true };
                for (int i = 0; i < to; i++)
                {
                    identity.Start[i] = i;
                    identity.Length[i] = 1;
                    identity.Values[i] = 1;
                }

                return identity;
            }

            double scale = (double)from / to;
            double widen = Math.Max(scale, 1);
            double support = filter.Radius * widen;
            int stride = Math.Min((int)Math.Ceiling(support * 2) + 2, from);
            var weights = new Weights(to, stride);
            double[] scratch = new double[stride];
            for (int i = 0; i < to; i++)
            {
                double centre = (i + 0.5) * scale;
                int start = (int)Math.Max(centre - support + 0.5, 0);
                int stop = (int)Math.Min(centre + support + 0.5, from);
                stop = Math.Min(stop, start + stride);
                double sum = 0;
                for (int j = start; j < stop; j++)
                {
                    double w = filter.Weight((j + 0.5 - centre) / widen);
                    scratch[j - start] = w;
                    sum += w;
                }

                if (stop <= start || Math.Abs(sum) < 1e-12)
                {
                    // Nothing in reach: take the nearest source pixel.
                    start = Math.Clamp((int)centre, 0, from - 1);
                    stop = start + 1;
                    scratch[0] = 1;
                    sum = 1;
                }

                weights.Start[i] = start;
                weights.Length[i] = stop - start;
                Span<float> values = weights.Values.AsSpan(i * stride, stride);
                for (int j = 0; j < stop - start; j++)
                {
                    values[j] = (float)(scratch[j] / sum);
                }
            }

            return weights;
        }
    }

    /// <summary>One resize, from source rows to output rows.</summary>
    private sealed class Pass
    {
        private readonly Image _source;
        private readonly Image _result;
        private readonly TransferFunction _transfer;
        private readonly Weights _x;
        private readonly Weights _y;
        private readonly int _channels;
        private readonly bool _alpha;
        private readonly bool _sixteen;
        private readonly float[] _toLinear;
        private readonly float[] _thresholds;

        public Pass(Image source, Image result, TransferFunction transfer, Weights x, Weights y)
        {
            _source = source;
            _result = result;
            _transfer = transfer;
            _x = x;
            _y = y;
            (_channels, _alpha) = source.PixelFormat switch
            {
                PixelFormat.L8 or PixelFormat.L16 => (1, false),
                PixelFormat.Rgb24 or PixelFormat.Rgb48 => (3, false),
                _ => (4, true),
            };
            _sixteen = source.PixelFormat is PixelFormat.L16 or PixelFormat.Rgb48 or PixelFormat.Rgba64;
            int max = _sixteen ? 65535 : 255;

            // Every encoded value is converted once; the samples index the table.
            _toLinear = new float[max + 1];
            for (int v = 0; v <= max; v++)
            {
                _toLinear[v] = (float)transfer.ToLinear((double)v / max);
            }

            // 8-bit output: code k when the linear value lies between the linear values of k - 0.5 and k + 0.5, which is
            // exact rounding in the encoded values without a power function per sample.
            _thresholds = [];
            if (!_sixteen)
            {
                _thresholds = new float[255];
                for (int k = 0; k < 255; k++)
                {
                    _thresholds[k] = (float)transfer.ToLinear((k + 0.5) / 255);
                }
            }
        }

        public void Run()
        {
            int sourceWidth = _source.Width;
            int width = _result.Width;
            int height = _result.Height;
            int rowLength = width * _channels;
            float[] linear = new float[sourceWidth * _channels];
            float[] across = _x.IsIdentity ? linear : new float[rowLength];

            // The output rows a source row adds into are consecutive; the ring holds as many as can be open at once.
            int ring = OpenRows();
            float[] sums = new float[ring * rowLength];
            int first = 0;
            for (int r = 0; r < _source.Height && first < height; r++)
            {
                if (r < _y.Start[first])
                {
                    continue;
                }

                ReadRow(r, linear);
                if (!_x.IsIdentity)
                {
                    Across(linear, across);
                }

                for (int y = first; y < height && _y.Start[y] <= r; y++)
                {
                    float w = _y.Values[(y * _y.Stride) + r - _y.Start[y]];
                    Span<float> sum = sums.AsSpan((y % ring) * rowLength, rowLength);
                    for (int i = 0; i < sum.Length; i++)
                    {
                        sum[i] += w * across[i];
                    }
                }

                while (first < height && _y.Start[first] + _y.Length[first] - 1 <= r)
                {
                    Span<float> sum = sums.AsSpan((first % ring) * rowLength, rowLength);
                    WriteRow(first, sum);
                    sum.Clear();
                    first++;
                }
            }
        }

        // The most output rows whose source windows cover one source row.
        private int OpenRows()
        {
            int most = 1;
            int first = 0;
            int last = -1;
            for (int r = 0; r < _source.Height; r++)
            {
                while (last + 1 < _result.Height && _y.Start[last + 1] <= r)
                {
                    last++;
                }

                while (first < _result.Height && _y.Start[first] + _y.Length[first] <= r)
                {
                    first++;
                }

                most = Math.Max(most, last - first + 1);
            }

            return most;
        }

        private void ReadRow(int y, float[] linear)
        {
            Span<byte> bytes = _source.GetRowBytes(y);
            if (_sixteen)
            {
                ReadSamples(MemoryMarshal.Cast<byte, ushort>(bytes), linear, 1f / 65535);
            }
            else
            {
                ReadSamples<byte>(bytes, linear, 1f / 255);
            }
        }

        private void ReadSamples<T>(ReadOnlySpan<T> samples, float[] linear, float unit)
            where T : unmanaged
        {
            for (int i = 0; i < samples.Length; i += _channels)
            {
                if (_alpha)
                {
                    float a = Sample(samples[i + 3]) * unit;
                    linear[i] = _toLinear[Sample(samples[i])] * a;
                    linear[i + 1] = _toLinear[Sample(samples[i + 1])] * a;
                    linear[i + 2] = _toLinear[Sample(samples[i + 2])] * a;
                    linear[i + 3] = a;
                }
                else
                {
                    for (int k = 0; k < _channels; k++)
                    {
                        linear[i + k] = _toLinear[Sample(samples[i + k])];
                    }
                }
            }
        }

        private static int Sample<T>(T value)
            where T : unmanaged
        {
            // A constant test once T is known, so each instantiation keeps one branch.
            if (typeof(T) == typeof(ushort))
            {
                return Unsafe.As<T, ushort>(ref value);
            }

            return Unsafe.As<T, byte>(ref value);
        }

        private void Across(float[] linear, float[] across)
        {
            int c = _channels;
            for (int x = 0; x < _result.Width; x++)
            {
                ReadOnlySpan<float> w = _x.Values.AsSpan(x * _x.Stride, _x.Length[x]);
                int start = _x.Start[x] * c;
                for (int k = 0; k < c; k++)
                {
                    float sum = 0;
                    for (int j = 0; j < w.Length; j++)
                    {
                        sum += w[j] * linear[start + (j * c) + k];
                    }

                    across[(x * c) + k] = sum;
                }
            }
        }

        private void WriteRow(int y, Span<float> sum)
        {
            Span<byte> bytes = _result.GetRowBytes(y);
            Span<ushort> wide = _sixteen ? MemoryMarshal.Cast<byte, ushort>(bytes) : default;
            int c = _channels;
            for (int i = 0; i < sum.Length; i += c)
            {
                float scale = 1;
                if (_alpha)
                {
                    float a = Math.Clamp(sum[i + 3], 0f, 1f);
                    scale = a > 0 ? 1 / a : 0;
                    Put(bytes, wide, i + 3, a, encode: false);
                }

                int colours = _alpha ? 3 : c;
                for (int k = 0; k < colours; k++)
                {
                    Put(bytes, wide, i + k, Math.Clamp(sum[i + k] * scale, 0f, 1f), encode: true);
                }
            }
        }

        private void Put(Span<byte> bytes, Span<ushort> wide, int index, float value, bool encode)
        {
            if (_sixteen)
            {
                double v = encode ? _transfer.FromLinear(value) : value;
                wide[index] = (ushort)Math.Round(Math.Clamp(v, 0, 1) * 65535);
            }
            else if (encode)
            {
                bytes[index] = (byte)Code(value);
            }
            else
            {
                bytes[index] = (byte)Math.Round(value * 255);
            }
        }

        // The number of thresholds at or below the value: its 8-bit code.
        private int Code(float value)
        {
            int low = 0;
            int high = _thresholds.Length;
            while (low < high)
            {
                int mid = (low + high) >> 1;
                if (_thresholds[mid] <= value)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            return low;
        }
    }
}
