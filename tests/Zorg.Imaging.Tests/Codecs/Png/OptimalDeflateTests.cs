using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Xunit;
using Zorg.Imaging.Codecs.Png;

namespace Zorg.Imaging.Tests.Codecs.Png;

public class OptimalDeflateTests
{
    public static TheoryData<string> Inputs => new() { "empty", "one", "run", "random", "text", "blocks", "pattern" };

    [Theory]
    [MemberData(nameof(Inputs))]
    public void What_it_writes_inflates_back(string name)
    {
        byte[] data = Make(name);
        (byte[] bytes, int length) = OptimalDeflate.Compress(data, last: true);
        Assert.Equal(data, Inflate(bytes.AsSpan(0, length).ToArray()));
    }

    [Fact]
    public void Bands_ended_by_a_sync_flush_join_into_one_stream()
    {
        byte[] first = Make("text");
        byte[] second = Make("pattern");
        (byte[] a, int aLength) = OptimalDeflate.Compress(first, last: false);
        (byte[] b, int bLength) = OptimalDeflate.Compress(second, last: true);
        byte[] joined = [.. a.AsSpan(0, aLength), .. b.AsSpan(0, bLength)];
        Assert.Equal([.. first, .. second], Inflate(joined));

        // A sync flush ends on 00 00 FF FF.
        Assert.Equal([0, 0, 0xFF, 0xFF], a.AsSpan(aLength - 4, 4).ToArray());
    }

    [Theory]
    [InlineData("text")]
    [InlineData("pattern")]
    [InlineData("blocks")]
    public void It_is_no_larger_than_zlib_at_level_9(string name)
    {
        byte[] data = Make(name);
        (_, int ours) = OptimalDeflate.Compress(data, last: true);
        using var zlib = new MemoryStream();
        using (var deflate = new DeflateStream(zlib, new ZLibCompressionOptions { CompressionLevel = 9 }, leaveOpen: true))
        {
            deflate.Write(data);
        }

        Assert.True(ours <= zlib.Length, $"ours {ours}, zlib {zlib.Length}");
    }

    [Fact]
    public void Optimize_is_no_larger_than_colour_rows_for_an_image_a_palette_could_hold()
    {
        using var image = new Image(256, 192, PixelFormat.Rgb24);
        for (int y = 0; y < image.Height; y++)
        {
            Span<Rgb24> row = image.GetRow<Rgb24>(y);
            for (int x = 0; x < row.Length; x++)
            {
                int v = (x + y) & 255;
                row[x] = new Rgb24((byte)v, (byte)(255 - v), (byte)(v / 2));
            }
        }

        byte[] optimized = PngEncoderTests.Save(image, new PngEncoderOptions { Optimize = true });
        byte[] colour = PngEncoderTests.Save(image, new PngEncoderOptions { Optimize = true, ReduceColorType = false });
        Assert.True(optimized.Length <= colour.Length, $"optimized {optimized.Length}, colour rows {colour.Length}");
        using Image back = Image.Decode(optimized, new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        using Image expected = Image.Decode(colour, new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        Assert.Equal(expected.GetPixelBytes().ToArray(), back.GetPixelBytes().ToArray());
    }

    [Fact]
    public void Code_lengths_stay_within_the_limit_however_skewed_the_counts()
    {
        // Fibonacci counts give the deepest Huffman tree: 30 symbols would want codes of up to 29 bits.
        int[] counts = new int[30];
        (int x, int y) = (1, 1);
        for (int i = 0; i < counts.Length; i++)
        {
            counts[i] = x;
            (x, y) = (y, x + y);
        }

        byte[] lengths = OptimalDeflate.Lengths(counts, 15);
        Assert.True(lengths.All(l => l is >= 1 and <= 15));
        double kraft = lengths.Sum(l => Math.Pow(2, -l));
        Assert.Equal(1.0, kraft, 9);
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Make(string name)
    {
        var random = new Random(name.Length);
        switch (name)
        {
            case "empty":
                return [];
            case "one":
                return [42];
            case "run":
                return Enumerable.Repeat((byte)7, 100_000).Concat(Enumerable.Repeat((byte)9, 3)).ToArray();
            case "random":
                byte[] noise = new byte[70_000];
                random.NextBytes(noise);
                return noise;
            case "text":
                var text = new StringBuilder();
                string[] words = ["pixel", "row", "filter", "deflate", "palette", "the", "a", "colour", "band", "zlib"];
                while (text.Length < 60_000)
                {
                    text.Append(words[random.Next(words.Length)]).Append(random.Next(5) == 0 ? ".\n" : " ");
                }

                return Encoding.ASCII.GetBytes(text.ToString());
            case "blocks":
                // More than one 128 KiB block: rows of a smooth gradient with noise, PNG-filtered style.
                byte[] rows = new byte[300_000];
                for (int i = 0; i < rows.Length; i++)
                {
                    rows[i] = (byte)(i % 1201 == 0 ? 1 : random.Next(4) == 0 ? random.Next(256) : (i % 7) + 1);
                }

                return rows;
            default:
                byte[] pattern = new byte[50_000];
                for (int i = 0; i < pattern.Length; i++)
                {
                    pattern[i] = (byte)((i * 7 % 13) + (i / 997 % 5));
                }

                return pattern;
        }
    }
}
