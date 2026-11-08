using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Codecs.Webp;
using Zorg.Imaging.Metadata;
using Zorg.Imaging.Tests.Codecs.Jpeg;
using Zorg.Imaging.Tests.Codecs.Png;

namespace Zorg.Imaging.Tests.Codecs.Webp;

public class WebpEncoderTests
{
    private static readonly WebpEncoderOptions Lossless = new() { Lossless = true };

    internal static byte[] Save(Image image, WebpEncoderOptions? options = null)
    {
        using var stream = new MemoryStream();
        image.SaveAsWebp(stream, options ?? Lossless);
        return stream.ToArray();
    }

    /// <summary>The image's pixels as RGBA, 8 bits a sample, as a lossless file should give them back.</summary>
    internal static byte[] Rgba(Image image)
    {
        (uint[] argb, _) = WebpEncoder.ToArgb(image);
        byte[] rgba = new byte[argb.Length * 4];
        for (int i = 0; i < argb.Length; i++)
        {
            rgba[4 * i] = (byte)(argb[i] >> 16);
            rgba[(4 * i) + 1] = (byte)(argb[i] >> 8);
            rgba[(4 * i) + 2] = (byte)argb[i];
            rgba[(4 * i) + 3] = (byte)(argb[i] >> 24);
        }

        return rgba;
    }

    /// <summary>A picture of exactly <paramref name="colours"/> colours (or as many as fit), in blocks and stripes.</summary>
    internal static Image Graphic(int width, int height, int colours, bool alpha = false, int seed = 1)
    {
        var random = new Random(seed);
        var palette = new Rgba32[colours];
        for (int i = 0; i < colours; i++)
        {
            palette[i] = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), alpha ? (byte)random.Next(256) : (byte)255);
        }

        var image = new Image(width, height, PixelFormat.Rgba32);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba32> row = image.GetRow<Rgba32>(y);
            for (int x = 0; x < width; x++)
            {
                // Every colour appears in the first row and column when there's room, then blocks of colour.
                int index = (y == 0 ? x : (x / 5) + (y / 3 * 7)) % colours;
                row[x] = palette[index];
            }
        }

        return image;
    }

    private static void AssertRoundTrips(Image image, WebpEncoderOptions? options = null)
    {
        byte[] webp = Save(image, options);
        using Image decoded = Image.Decode(webp, new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        Assert.Equal((image.Width, image.Height), (decoded.Width, decoded.Height));
        Assert.Equal(Rgba(image), decoded.GetPixelBytes().ToArray());
    }

    [Theory]
    [InlineData(PixelFormat.Rgb24)]
    [InlineData(PixelFormat.Rgba32)]
    [InlineData(PixelFormat.Bgra32)]
    [InlineData(PixelFormat.L8)]
    [InlineData(PixelFormat.L16)]
    [InlineData(PixelFormat.Rgb48)]
    [InlineData(PixelFormat.Rgba64)]
    public void Every_pixel_format_round_trips_exactly(PixelFormat format)
    {
        using Image photo = format == PixelFormat.Rgb48 ? Rgb48(JpegEncoderTests.Photo(131, 77, PixelFormat.Rgba64, seed: 3)) : JpegEncoderTests.Photo(131, 77, format, seed: 3);
        AssertRoundTrips(photo);
    }

    private static Image Rgb48(Image wide)
    {
        using (wide)
        {
            var result = new Image(wide.Width, wide.Height, PixelFormat.Rgb48);
            for (int y = 0; y < wide.Height; y++)
            {
                ReadOnlySpan<Rgba64> from = wide.GetRow<Rgba64>(y);
                Span<Rgb48> to = result.GetRow<Rgb48>(y);
                for (int x = 0; x < wide.Width; x++)
                {
                    to[x] = new Rgb48(from[x].R, from[x].G, from[x].B);
                }
            }

            return result;
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 40)]
    [InlineData(40, 1)]
    [InlineData(2, 2)]
    [InlineData(17, 33)]
    [InlineData(64, 64)]
    [InlineData(257, 3)]
    public void Odd_and_tiny_sizes_round_trip(int width, int height)
    {
        using Image photo = JpegEncoderTests.Photo(width, height, PixelFormat.Rgba32, seed: width + height);
        AssertRoundTrips(photo);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Every_effort_round_trips_and_more_effort_is_not_larger(int effort)
    {
        using Image photo = JpegEncoderTests.Photo(160, 120, PixelFormat.Rgba32, seed: 5);
        AssertRoundTrips(photo, new WebpEncoderOptions { Lossless = true, Effort = effort });
        if (effort > 0)
        {
            int less = Save(photo, new WebpEncoderOptions { Lossless = true, Effort = 0 }).Length;
            int more = Save(photo, new WebpEncoderOptions { Lossless = true, Effort = effort }).Length;
            Assert.True(more <= less * 1.02, $"effort {effort}: {more} bytes against {less} at effort 0");
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(16, false)]
    [InlineData(17, true)]
    [InlineData(200, false)]
    [InlineData(256, true)]
    [InlineData(257, false)]
    [InlineData(1000, true)]
    public void Graphics_round_trip_with_and_without_a_colour_table(int colours, bool alpha)
    {
        using Image graphic = Graphic(301, 45, colours, alpha, seed: colours);
        AssertRoundTrips(graphic);
    }

    [Fact]
    public void Few_colours_are_much_smaller_than_the_same_picture_as_png()
    {
        using Image graphic = Graphic(400, 300, 12);
        int webp = Save(graphic).Length;
        int png = PngEncoderTests.Save(graphic).Length;
        Assert.True(webp < png, $"WebP {webp} bytes, PNG {png}");
    }

    [Fact]
    public void Noise_and_flat_colour_round_trip()
    {
        var random = new Random(7);
        using var noise = new Image(97, 61, PixelFormat.Rgba32);
        random.NextBytes(noise.GetPixelBytes());
        AssertRoundTrips(noise);

        using var flat = new Image(500, 400, PixelFormat.Rgb24);
        flat.GetPixelBytes().Fill(77);
        AssertRoundTrips(flat);
        Assert.True(Save(flat).Length < 100);

        // Long runs and repeats far apart, for distances beyond the plane codes and copies of the longest length.
        using var repeats = new Image(4500, 30, PixelFormat.Rgb24);
        Span<byte> bytes = repeats.GetPixelBytes();
        random.NextBytes(bytes[..(3 * 4500)]);
        for (int y = 1; y < 30; y++)
        {
            bytes[..(3 * 4500)].CopyTo(bytes[(y * 3 * 4500)..]);
        }

        bytes[(5 * 3 * 4500)..(6 * 3 * 4500)].Fill(9);
        AssertRoundTrips(repeats);
    }

    [Fact]
    public void Alpha_is_flagged_only_when_used()
    {
        using Image opaque = JpegEncoderTests.Photo(20, 10, PixelFormat.Rgba32);
        Span<Rgba32> pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Rgba32>(opaque.GetPixelBytes());
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = pixels[i] with { A = 255 };
        }

        Assert.False(Header(Save(opaque)).Alpha);
        pixels[17] = pixels[17] with { A = 254 };
        Assert.True(Header(Save(opaque)).Alpha);
        using Image decoded = Image.Decode(Save(opaque));
        Assert.Equal(PixelFormat.Rgba32, decoded.PixelFormat);
    }

    private static (int Width, int Height, bool Alpha) Header(byte[] webp)
    {
        Assert.Equal("VP8L"u8.ToArray(), webp[12..16]);
        return Vp8lDecoder.ReadHeader(webp.AsSpan(20));
    }

    [Fact]
    public void The_container_is_well_formed()
    {
        using Image photo = JpegEncoderTests.Photo(33, 21);
        byte[] webp = Save(photo);
        Assert.Equal("RIFF"u8.ToArray(), webp[..4]);
        Assert.Equal(webp.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(webp.AsSpan(4)));
        Assert.Equal("WEBP"u8.ToArray(), webp[8..12]);
        int chunk = BinaryPrimitives.ReadInt32LittleEndian(webp.AsSpan(16));
        Assert.Equal(webp.Length, 20 + chunk + (chunk & 1));
        Assert.Equal((33, 21, false), Header(webp));
        Assert.Equal(ImageFormat.Webp, Image.Identify(webp).Format);
    }

    [Fact]
    public void Srgb_images_carry_no_profile_and_others_do()
    {
        using Image photo = JpegEncoderTests.Photo(40, 24, PixelFormat.Rgba32);
        Assert.Equal("VP8L"u8.ToArray(), Save(photo)[12..16]);

        photo.ColorSpace = ColorSpace.DisplayP3;
        byte[] webp = Save(photo);
        Assert.Equal("VP8X"u8.ToArray(), webp[12..16]);
        Assert.Equal(0x30, webp[20]);
        using Image p3 = Image.Decode(webp);
        Assert.Equal(IccProfileWriter.For(ColorSpace.DisplayP3, "WebP").ToArray(), p3.ColorSpace.IccProfile.ToArray());
        Assert.Equal(Rgba(photo), Image.Decode(webp, new DecodeOptions { PixelFormat = PixelFormat.Rgba32 }).GetPixelBytes().ToArray());

        // A profile of odd length is padded, and the image's own profile is written back byte for byte.
        byte[] own = PngColourTests.FakeProfile(70_001, seed: 2);
        photo.ColorSpace = ColorSpace.FromIccProfile(own);
        using Image again = Image.Decode(Save(photo));
        Assert.Equal(own, again.ColorSpace.IccProfile.ToArray());
    }

    [Fact]
    public void What_this_version_cannot_write_is_refused()
    {
        using Image photo = JpegEncoderTests.Photo(16, 16);
        Assert.Contains("Lossless", Assert.Throws<NotSupportedException>(() => Save(photo, WebpEncoderOptions.Default)).Message, StringComparison.Ordinal);
        photo.ColorSpace = ColorSpace.Rec2020Pq;
        Assert.Throws<NotSupportedException>(() => Save(photo));

        using var wide = new Image(16385, 1, PixelFormat.L8);
        Assert.Throws<NotSupportedException>(() => Save(wide));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebpEncoderOptions { Effort = 7 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebpEncoderOptions { Quality = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebpEncoderOptions { MaxDegreeOfParallelism = -1 });
    }

    [Fact]
    public async Task Async_and_the_pipeline_write_the_same_file()
    {
        using Image photo = JpegEncoderTests.Photo(90, 60);
        byte[] expected = Save(photo);
        using var stream = new MemoryStream();
        await photo.SaveAsWebpAsync(stream, Lossless, TestContext.Current.CancellationToken);
        Assert.Equal(expected, stream.ToArray());

        using var piped = new MemoryStream();
        await ImagePipeline.From(photo).SaveAsWebpAsync(piped, Lossless, TestContext.Current.CancellationToken);
        Assert.Equal(expected, piped.ToArray());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void The_file_is_the_same_on_one_core_and_on_many(int effort)
    {
        // Large enough for every threaded stage to split its work.
        using Image photo = JpegEncoderTests.Photo(1250, 640, PixelFormat.Rgba32, seed: 11);
        byte[] one = Save(photo, new WebpEncoderOptions { Lossless = true, Effort = effort, MaxDegreeOfParallelism = 1 });
        byte[] many = Save(photo, new WebpEncoderOptions { Lossless = true, Effort = effort, MaxDegreeOfParallelism = 8 });
        Assert.Equal(one, many);
        using Image decoded = Image.Decode(many, new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        Assert.Equal(Rgba(photo), decoded.GetPixelBytes().ToArray());
    }

    [Fact]
    public void Prefix_code_lengths_stay_within_the_limit_and_fill_the_code()
    {
        // Fibonacci counts make the deepest possible Huffman tree.
        int[] counts = new int[40];
        counts[0] = 1;
        counts[1] = 1;
        for (int i = 2; i < counts.Length; i++)
        {
            counts[i] = Math.Min(counts[i - 1] + counts[i - 2], int.MaxValue / 4);
        }

        foreach (int limit in new[] { 7, 15 })
        {
            byte[] lengths = new byte[counts.Length];
            Vp8lHuffman.Lengths(counts, limit, lengths);
            double kraft = 0;
            foreach (byte length in lengths)
            {
                Assert.InRange(length, 1, limit);
                kraft += Math.Pow(2, -length);
            }

            Assert.Equal(1.0, kraft, 12);
        }
    }
}
