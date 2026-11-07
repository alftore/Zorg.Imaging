using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Xunit;

namespace Zorg.Imaging.Tests.Codecs.Jpeg;

/// <summary>
/// The JPEG files in <c>Files</c>, made by <c>scripts/make-jpeg-fixtures.sh</c> with libjpeg-turbo's <c>cjpeg</c>, and
/// <c>djpeg</c>'s decodes of them as PNM, which serve as the reference.
/// </summary>
internal static class JpegTestFiles
{
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "Codecs", "Jpeg", "Files");

    public static byte[] Sample(string name) => File.ReadAllBytes(Path.Combine(Folder, name + ".jpg"));

    /// <summary>Reads a binary PGM or PPM: the size, the channels (1 or 3) and the samples.</summary>
    public static (int Width, int Height, int Channels, byte[] Samples) Pnm(string name)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Folder, name + ".pnm"));
        int at = 0;
        string Token()
        {
            while (char.IsWhiteSpace((char)file[at]))
            {
                at++;
            }

            int start = at;
            while (!char.IsWhiteSpace((char)file[at]))
            {
                at++;
            }

            return Encoding.ASCII.GetString(file, start, at - start);
        }

        string magic = Token();
        int width = int.Parse(Token(), CultureInfo.InvariantCulture);
        int height = int.Parse(Token(), CultureInfo.InvariantCulture);
        Assert.Equal("255", Token());
        at++;
        int channels = magic == "P5" ? 1 : 3;
        return (width, height, channels, file[at..]);
    }

    /// <summary>The largest and the mean difference between two sample arrays of the same length.</summary>
    public static (int Max, double Mean) Compare(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected)
    {
        Assert.Equal(expected.Length, actual.Length);
        int max = 0;
        long total = 0;
        for (int i = 0; i < actual.Length; i++)
        {
            int difference = Math.Abs(actual[i] - expected[i]);
            max = Math.Max(max, difference);
            total += difference;
        }

        return (max, (double)total / actual.Length);
    }

    /// <summary>Each cell x cell square's mean, rounded: the exact area-average downscale, edges included.</summary>
    public static byte[] AreaAverage(Image full, int cell)
    {
        int w = (full.Width + cell - 1) / cell;
        int h = (full.Height + cell - 1) / cell;
        int c = full.BytesPerPixel;
        byte[] box = new byte[w * h * c];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                for (int k = 0; k < c; k++)
                {
                    int sum = 0;
                    int count = 0;
                    for (int yy = y * cell; yy < Math.Min((y + 1) * cell, full.Height); yy++)
                    {
                        for (int xx = x * cell; xx < Math.Min((x + 1) * cell, full.Width); xx++)
                        {
                            sum += full.GetRowBytes(yy)[(xx * c) + k];
                            count++;
                        }
                    }

                    box[(((y * w) + x) * c) + k] = (byte)((sum + (count / 2)) / count);
                }
            }
        }

        return box;
    }

    /// <summary>Inserts segments after SOI.</summary>
    public static byte[] WithSegments(byte[] jpeg, params (int Marker, byte[] Data)[] segments)
    {
        var result = new List<byte>(jpeg[..2]);
        foreach ((int marker, byte[] data) in segments)
        {
            result.AddRange([0xFF, (byte)marker, (byte)((data.Length + 2) >> 8), (byte)(data.Length + 2)]);
            result.AddRange(data);
        }

        result.AddRange(jpeg[2..]);
        return [.. result];
    }

    /// <summary>Every segment's offset (of its marker) and marker code, up to the first scan's data.</summary>
    public static List<(int Offset, byte Marker)> Segments(byte[] jpeg)
    {
        var segments = new List<(int, byte)>();
        int at = 2;
        while (at + 4 <= jpeg.Length && jpeg[at] == 0xFF)
        {
            byte marker = jpeg[at + 1];
            segments.Add((at, marker));
            int length = (jpeg[at + 2] << 8) | jpeg[at + 3];
            if (marker == 0xDA)
            {
                break;
            }

            at += 2 + length;
        }

        return segments;
    }
}
