using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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
        var result = new Image(width, height, source.PixelFormat) { ColorSpace = source.ColorSpace, SourceMetadata = source.SourceMetadata };
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
    /// <remarks>
    /// Rows hold one float per channel, except that RGB is padded to four, so a pixel is one 128-bit vector. Large
    /// images are split into bands of output rows, resized in parallel; each band reads the few source rows at its
    /// edges again rather than sharing them.
    /// </remarks>
    private sealed class Pass
    {
        private readonly Image _source;
        private readonly Image _result;
        private readonly Weights _x;
        private readonly Weights _y;
        private readonly int _channels;
        private readonly int _lanes;
        private readonly bool _alpha;
        private readonly bool _sixteen;
        private readonly float[] _toLinear;
        private readonly Quantizer _encode;

        public Pass(Image source, Image result, TransferFunction transfer, Weights x, Weights y)
        {
            _source = source;
            _result = result;
            _x = x;
            _y = y;
            (_channels, _alpha) = source.PixelFormat switch
            {
                PixelFormat.L8 or PixelFormat.L16 => (1, false),
                PixelFormat.Rgb24 or PixelFormat.Rgb48 => (3, false),
                _ => (4, true),
            };
            _lanes = _channels == 1 ? 1 : 4;
            _sixteen = source.PixelFormat is PixelFormat.L16 or PixelFormat.Rgb48 or PixelFormat.Rgba64;
            _toLinear = transfer.LinearTable(_sixteen);
            _encode = transfer.Encoder(_sixteen);
        }

        public void Run()
        {
            int height = _result.Height;
            int ring = OpenRows();
            long work = ((long)_source.Width * _source.Height) + ((long)_result.Width * height);
            int bands = Parallelism.Bands(work, height / 16);
            Parallelism.For(bands, b => Band((int)((long)height * b / bands), (int)((long)height * (b + 1) / bands), ring));
        }

        // Output rows [from, to): each source row they need is read, resampled across, and added into the open rows.
        private void Band(int from, int to, int ring)
        {
            int rowLength = _result.Width * _lanes;
            float[] linear = ArrayPool<float>.Shared.Rent(_source.Width * _lanes);
            float[] across = _x.IsIdentity ? linear : ArrayPool<float>.Shared.Rent(rowLength);
            float[] sums = ArrayPool<float>.Shared.Rent(ring * rowLength);
            sums.AsSpan(0, ring * rowLength).Clear();
            try
            {
                Rows(from, to, ring, rowLength, linear, across, sums);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(sums);
                if (!_x.IsIdentity)
                {
                    ArrayPool<float>.Shared.Return(across);
                }

                ArrayPool<float>.Shared.Return(linear);
            }
        }

        private void Rows(int from, int to, int ring, int rowLength, float[] linear, float[] across, float[] sums)
        {
            int first = from;
            int end = _y.Start[to - 1] + _y.Length[to - 1];
            for (int r = _y.Start[from]; r < end; r++)
            {
                ReadRow(r, linear);
                if (!_x.IsIdentity)
                {
                    Across(linear, across);
                }

                for (int y = first; y < to && _y.Start[y] <= r; y++)
                {
                    float w = _y.Values[(y * _y.Stride) + r - _y.Start[y]];
                    AddScaled(sums.AsSpan((y % ring) * rowLength, rowLength), across.AsSpan(0, rowLength), w);
                }

                while (first < to && _y.Start[first] + _y.Length[first] - 1 <= r)
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

        private static void AddScaled(Span<float> sum, ReadOnlySpan<float> row, float w)
        {
            int i = 0;
            var weight = new Vector<float>(w);
            for (; i <= sum.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                (new Vector<float>(sum[i..]) + (weight * new Vector<float>(row[i..]))).CopyTo(sum[i..]);
            }

            for (; i < sum.Length; i++)
            {
                sum[i] += w * row[i];
            }
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
            float[] table = _toLinear;
            if (_alpha)
            {
                for (int i = 0; i + 3 < samples.Length && i + 3 < linear.Length; i += 4)
                {
                    float a = Sample(samples[i + 3]) * unit;
                    linear[i] = table[Sample(samples[i])] * a;
                    linear[i + 1] = table[Sample(samples[i + 1])] * a;
                    linear[i + 2] = table[Sample(samples[i + 2])] * a;
                    linear[i + 3] = a;
                }
            }
            else if (_channels == 3)
            {
                for (int i = 0, o = 0; i + 2 < samples.Length && o + 3 < linear.Length; i += 3, o += 4)
                {
                    linear[o] = table[Sample(samples[i])];
                    linear[o + 1] = table[Sample(samples[i + 1])];
                    linear[o + 2] = table[Sample(samples[i + 2])];
                    linear[o + 3] = 0;
                }
            }
            else
            {
                for (int i = 0; i < samples.Length && i < linear.Length; i++)
                {
                    linear[i] = table[Sample(samples[i])];
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
            int width = _result.Width;
            int stride = _x.Stride;
            int[] starts = _x.Start;
            int[] lengths = _x.Length;
            float[] weights = _x.Values;
            if (_lanes == 4)
            {
                // Indices are in range by construction: each window lies inside the source row.
                ref float source = ref MemoryMarshal.GetArrayDataReference(linear);
                ref float weight = ref MemoryMarshal.GetArrayDataReference(weights);
                for (int x = 0; x < width; x++)
                {
                    int length = lengths[x];
                    nuint start = (nuint)starts[x] * 4;
                    int w = x * stride;
                    // Four sums, so each multiply-add doesn't wait for the one before it.
                    Vector128<float> sum0 = Vector128<float>.Zero;
                    Vector128<float> sum1 = Vector128<float>.Zero;
                    Vector128<float> sum2 = Vector128<float>.Zero;
                    Vector128<float> sum3 = Vector128<float>.Zero;
                    int j = 0;
                    for (; j + 3 < length; j += 4)
                    {
                        nuint at = start + ((nuint)j * 4);
                        sum0 += Vector128.LoadUnsafe(ref source, at) * Vector128.Create(Unsafe.Add(ref weight, w + j));
                        sum1 += Vector128.LoadUnsafe(ref source, at + 4) * Vector128.Create(Unsafe.Add(ref weight, w + j + 1));
                        sum2 += Vector128.LoadUnsafe(ref source, at + 8) * Vector128.Create(Unsafe.Add(ref weight, w + j + 2));
                        sum3 += Vector128.LoadUnsafe(ref source, at + 12) * Vector128.Create(Unsafe.Add(ref weight, w + j + 3));
                    }

                    for (; j < length; j++)
                    {
                        sum0 += Vector128.LoadUnsafe(ref source, start + ((nuint)j * 4)) * Vector128.Create(Unsafe.Add(ref weight, w + j));
                    }

                    ((sum0 + sum1) + (sum2 + sum3)).CopyTo(across.AsSpan(x * 4, 4));
                }

                return;
            }

            for (int x = 0; x < width; x++)
            {
                ReadOnlySpan<float> w = weights.AsSpan(x * stride, lengths[x]);
                ReadOnlySpan<float> s = linear.AsSpan(starts[x], w.Length);
                float sum = 0;
                for (int j = 0; j < w.Length; j++)
                {
                    sum += w[j] * s[j];
                }

                across[x] = sum;
            }
        }

        private void WriteRow(int y, Span<float> sum)
        {
            Span<byte> bytes = _result.GetRowBytes(y);
            Span<ushort> wide = _sixteen ? MemoryMarshal.Cast<byte, ushort>(bytes) : default;
            int width = _result.Width;
            int c = _channels;
            int colours = _alpha ? 3 : c;
            float max = _sixteen ? 65535 : 255;
            for (int x = 0; x < width; x++)
            {
                int i = x * _lanes;
                int o = x * c;
                float scale = 1;
                if (_alpha)
                {
                    float a = Math.Clamp(sum[i + 3], 0f, 1f);
                    scale = a > 0 ? 1 / a : 0;
                    int alpha = (int)MathF.Round(a * max);
                    if (_sixteen)
                    {
                        wide[o + 3] = (ushort)alpha;
                    }
                    else
                    {
                        bytes[o + 3] = (byte)alpha;
                    }
                }

                for (int k = 0; k < colours; k++)
                {
                    int code = _encode.Encode(sum[i + k] * scale);
                    if (_sixteen)
                    {
                        wide[o + k] = (ushort)code;
                    }
                    else
                    {
                        bytes[o + k] = (byte)code;
                    }
                }
            }
        }
    }
}
