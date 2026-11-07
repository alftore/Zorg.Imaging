using System;
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
