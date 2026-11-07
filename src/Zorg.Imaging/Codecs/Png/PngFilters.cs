using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Globalization;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Codecs.Png;

/// <summary>PNG's five row filters (filter method 0): None, Sub, Up, Average and Paeth.</summary>
internal static class PngFilters
{
    /// <summary>Undoes a row's filter in place.</summary>
    /// <param name="row">The filter type byte, then the filtered bytes, which become the raw bytes.</param>
    /// <param name="previous">The previous row's raw bytes, or zeros for the first row of a pass.</param>
    /// <param name="bytesPerPixel">How far back Sub, Average and Paeth look.</param>
    /// <exception cref="ImageFormatException">The filter type isn't 0 to 4.</exception>
    public static void Unfilter(Span<byte> row, ReadOnlySpan<byte> previous, int bytesPerPixel)
    {
        byte filter = row[0];
        Span<byte> x = row[1..];
        int length = x.Length;
        switch (filter)
        {
            case 0:
                break;

            case 1:
                for (int i = bytesPerPixel; i < length; i++)
                {
                    x[i] += x[i - bytesPerPixel];
                }

                break;

            case 2:
                for (int i = 0; i < length; i++)
                {
                    x[i] += previous[i];
                }

                break;

            case 3:
                for (int i = 0; i < bytesPerPixel; i++)
                {
                    x[i] += (byte)(previous[i] >> 1);
                }

                for (int i = bytesPerPixel; i < length; i++)
                {
                    x[i] += (byte)((x[i - bytesPerPixel] + previous[i]) >> 1);
                }

                break;

            case 4:
                for (int i = 0; i < bytesPerPixel; i++)
                {
                    x[i] += previous[i];
                }

                for (int i = bytesPerPixel; i < length; i++)
                {
                    x[i] += Paeth(x[i - bytesPerPixel], previous[i], previous[i - bytesPerPixel]);
                }

                break;

            default:
                throw ImageErrors.Corrupt(string.Create(CultureInfo.InvariantCulture, $"The PNG file has a row with filter type {filter}, which doesn't exist."));
        }
    }

    /// <summary>Applies a filter to a raw row.</summary>
    /// <param name="filter">The filter type, 0 to 4.</param>
    /// <param name="raw">The row's raw bytes.</param>
    /// <param name="previous">The previous row's raw bytes, or zeros for the first row.</param>
    /// <param name="bytesPerPixel">How far back Sub, Average and Paeth look.</param>
    /// <param name="destination">Where the filtered bytes go, as long as <paramref name="raw"/>.</param>
    public static void Filter(int filter, ReadOnlySpan<byte> raw, ReadOnlySpan<byte> previous, int bytesPerPixel, Span<byte> destination)
    {
        int length = raw.Length;
        for (int i = 0; i < length; i++)
        {
            byte left = i >= bytesPerPixel ? raw[i - bytesPerPixel] : (byte)0;
            byte upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : (byte)0;
            destination[i] = filter switch
            {
                0 => raw[i],
                1 => (byte)(raw[i] - left),
                2 => (byte)(raw[i] - previous[i]),
                3 => (byte)(raw[i] - ((left + previous[i]) >> 1)),
                _ => (byte)(raw[i] - Paeth(left, previous[i], upLeft)),
            };
        }
    }

    /// <summary>
    /// Applies a filter to a raw row and scores the result as the sum of the bytes' magnitudes read as signed (the
    /// "minimum sum of absolute differences" heuristic, as libpng uses it).
    /// </summary>
    /// <param name="filter">The filter type, 0 to 4.</param>
    /// <param name="raw">The row's raw bytes.</param>
    /// <param name="previous">The previous row's raw bytes, or zeros for the first row.</param>
    /// <param name="bytesPerPixel">How far back Sub, Average and Paeth look.</param>
    /// <param name="destination">Where the filtered bytes go, as long as <paramref name="raw"/>.</param>
    /// <returns>The score; lower usually compresses better.</returns>
    /// <remarks>
    /// Filtering, unlike unfiltering, has no dependency from one byte to the next, so after the first pixel (which has
    /// no left neighbour) the row is done 16 bytes at a time; Paeth in 16-bit lanes, eight bytes at a time. The scalar
    /// <see cref="Filter"/> is the reference it is tested against.
    /// </remarks>
    public static long FilterAndScore(int filter, ReadOnlySpan<byte> raw, ReadOnlySpan<byte> previous, int bytesPerPixel, Span<byte> destination)
    {
        int length = raw.Length;
        int head = Math.Min(bytesPerPixel, length);
        Filter(filter, raw[..head], previous[..head], bytesPerPixel, destination[..head]);
        int i = head;
        if (Vector128.IsHardwareAccelerated)
        {
            ref byte r = ref MemoryMarshal.GetReference(raw);
            ref byte p = ref MemoryMarshal.GetReference(previous);
            ref byte d = ref MemoryMarshal.GetReference(destination);
            nuint back = (nuint)bytesPerPixel;
            if (filter == 4)
            {
                for (; i + 8 <= length; i += 8)
                {
                    nuint at = (nuint)i;
                    Vector128<short> x = Load8(ref r, at);
                    Vector128<short> a = Load8(ref r, at - back);
                    Vector128<short> b = Load8(ref p, at);
                    Vector128<short> c = Load8(ref p, at - back);
                    Vector128<short> pa = Vector128.Abs(b - c);
                    Vector128<short> pb = Vector128.Abs(a - c);
                    Vector128<short> pc = Vector128.Abs(a + b - c - c);
                    Vector128<short> useA = Vector128.LessThanOrEqual(pa, pb) & Vector128.LessThanOrEqual(pa, pc);
                    Vector128<short> useB = Vector128.LessThanOrEqual(pb, pc);
                    Vector128<short> prediction = Vector128.ConditionalSelect(useA, a, Vector128.ConditionalSelect(useB, b, c));
                    Vector128<ushort> filtered = (x - prediction).AsUInt16() & Vector128.Create((ushort)0xFF);
                    Vector128<byte> bytes = Vector128.Narrow(filtered, filtered);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, i), bytes.AsUInt64().ToScalar());
                }
            }
            else if (filter != 0)
            {
                for (; i + 16 <= length; i += 16)
                {
                    nuint at = (nuint)i;
                    Vector128<byte> x = Vector128.LoadUnsafe(ref r, at);
                    Vector128<byte> result = filter switch
                    {
                        1 => x - Vector128.LoadUnsafe(ref r, at - back),
                        2 => x - Vector128.LoadUnsafe(ref p, at),
                        _ => x - Average(Vector128.LoadUnsafe(ref r, at - back), Vector128.LoadUnsafe(ref p, at)),
                    };
                    result.StoreUnsafe(ref d, at);
                }
            }
        }

        if (i < length)
        {
            // The tail, one byte at a time with the scalar rule (it looks back at most one pixel).
            for (; i < length; i++)
            {
                byte left = raw[i - bytesPerPixel];
                byte upLeft = previous[i - bytesPerPixel];
                destination[i] = filter switch
                {
                    0 => raw[i],
                    1 => (byte)(raw[i] - left),
                    2 => (byte)(raw[i] - previous[i]),
                    3 => (byte)(raw[i] - ((left + previous[i]) >> 1)),
                    _ => (byte)(raw[i] - Paeth(left, previous[i], upLeft)),
                };
            }
        }

        return Score(filter == 0 ? raw : destination);
    }

    /// <summary>The sum of the bytes' magnitudes read as signed: 16 at a time, widened before adding.</summary>
    public static long Score(ReadOnlySpan<byte> filtered)
    {
        long sum = 0;
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref byte f = ref MemoryMarshal.GetReference(filtered);
            Vector128<uint> total = Vector128<uint>.Zero;
            for (; i + 16 <= filtered.Length; i += 16)
            {
                // |b| as signed, which for -128 is 128: correct read as unsigned.
                Vector128<byte> magnitude = Vector128.Abs(Vector128.LoadUnsafe(ref f, (nuint)i).AsSByte()).AsByte();
                Vector128<ushort> shorts = Vector128.WidenLower(magnitude) + Vector128.WidenUpper(magnitude);
                total += Vector128.WidenLower(shorts) + Vector128.WidenUpper(shorts);
            }

            sum = Vector128.Sum(total);
        }

        for (; i < filtered.Length; i++)
        {
            sum += Math.Abs((int)(sbyte)filtered[i]);
        }

        return sum;
    }

    // floor((a + b) / 2) per byte without overflow.
    private static Vector128<byte> Average(Vector128<byte> a, Vector128<byte> b) => (a & b) + Vector128.ShiftRightLogical(a ^ b, 1);

    private static Vector128<short> Load8(ref byte source, nuint at) =>
        Vector128.WidenLower(Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, at))).AsByte()).AsInt16();

    /// <summary>The Paeth predictor: whichever of left, up and upper-left is closest to left + up - upper-left.</summary>
    /// <param name="a">The byte to the left.</param>
    /// <param name="b">The byte above.</param>
    /// <param name="c">The byte above and to the left.</param>
    /// <returns>The prediction.</returns>
    public static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return a;
        }

        return pb <= pc ? b : c;
    }
}
