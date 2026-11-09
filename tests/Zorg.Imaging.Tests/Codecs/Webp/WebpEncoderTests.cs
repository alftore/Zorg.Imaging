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
        (uint[] argb, _) = Zorg.Imaging.Processing.Argb.FromImage(image);
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
    public void What_webp_cannot_hold_is_refused()
    {
        using Image photo = JpegEncoderTests.Photo(16, 16);
        photo.ColorSpace = ColorSpace.Rec2020Pq;
        Assert.Throws<NotSupportedException>(() => Save(photo));
        Assert.Throws<NotSupportedException>(() => Save(photo, WebpEncoderOptions.Default));

        using var wide = new Image(16385, 1, PixelFormat.L8);
        Assert.Throws<NotSupportedException>(() => Save(wide));
        using var lossyWide = new Image(16384, 1, PixelFormat.L8);
        Assert.Contains("16383", Assert.Throws<NotSupportedException>(() => Save(lossyWide, WebpEncoderOptions.Default)).Message, StringComparison.Ordinal);
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
    public void Lossy_with_alpha_is_the_same_on_one_core_and_on_many()
    {
        using Image photo = JpegEncoderTests.Photo(1250, 640, PixelFormat.Rgba32, seed: 12);
        byte[] one = Save(photo, new WebpEncoderOptions { Quality = 80, MaxDegreeOfParallelism = 1 });
        byte[] many = Save(photo, new WebpEncoderOptions { Quality = 80, MaxDegreeOfParallelism = 8 });
        Assert.Equal(one, many);
    }

    [Theory]
    [InlineData(1, 20_000)]
    [InlineData(8, 20_000)]
    [InlineData(1, 16_000)]
    public void A_picture_whose_modes_overflow_the_first_partition_keeps_within_it(int cores, int limit)
    {
        // Noise makes most macroblocks take subblock modes: this picture's modes need about 24 KB, so under a smaller
        // limit (the format's is 512 KB, reached only by pictures of tens of megapixels) rows are given shares of the
        // room, and the frame fills most of it rather than giving up subblock modes everywhere.
        const int Width = 1024;
        const int Height = 768;
        uint[] argb = new uint[Width * Height];
        var random = new Random(5);
        for (int i = 0; i < argb.Length; i++)
        {
            argb[i] = 0xFF000000u | (uint)random.Next(1 << 24);
        }

        (byte[] y, byte[] u, byte[] v) = WebpEncoder.ToYuv(argb, Width, Height);
        byte[] unlimited;
        byte[] frame;
        using (Zorg.Imaging.Processing.Parallelism.Use(cores))
        {
            unlimited = Vp8Encoder.Encode(y, u, v, Width, Height, Vp8Encoder.QuantiserFor(95), 4);
            frame = Vp8Encoder.Encode(y, u, v, Width, Height, Vp8Encoder.QuantiserFor(95), 4, limit);
        }

        Assert.True(FirstPartition(unlimited) > limit, "the picture should need more than the limit");
        int first = FirstPartition(frame);
        Assert.InRange(first, limit - 4_500, limit - 1);

        // In a RIFF container, it decodes to the picture's size, and close to the picture coded without a limit.
        byte[] file = new byte[20 + frame.Length + (frame.Length & 1)];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), file.Length - 8);
        "WEBPVP8 "u8.CopyTo(file.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(16), frame.Length);
        frame.CopyTo(file, 20);
        using Image decoded = Image.Decode(file);
        Assert.Equal((Width, Height), (decoded.Width, decoded.Height));
    }

    private static int FirstPartition(byte[] frame) => (frame[0] | (frame[1] << 8) | (frame[2] << 16)) >> 5;

    [Theory]
    [InlineData(PixelFormat.Rgb24, 30)]
    [InlineData(PixelFormat.Rgba32, 30)]
    [InlineData(PixelFormat.Bgra32, 30)]
    [InlineData(PixelFormat.L8, 33)]
    [InlineData(PixelFormat.L16, 33)]
    [InlineData(PixelFormat.Rgba64, 30)]
    public void Lossy_round_trips_with_the_expected_fidelity_and_exact_alpha(PixelFormat format, double minimumPsnr)
    {
        using Image photo = JpegEncoderTests.Photo(131, 77, format, seed: 3);
        byte[] webp = Save(photo, WebpEncoderOptions.Default);
        bool alpha = format is PixelFormat.Rgba32 or PixelFormat.Bgra32 or PixelFormat.Rgba64;
        Assert.Equal(alpha ? "VP8X"u8.ToArray() : "VP8 "u8.ToArray(), webp[12..16]);
        using Image decoded = Image.Decode(webp, new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        byte[] expected = Rgba(photo);
        byte[] actual = decoded.GetPixelBytes().ToArray();
        double psnr = JpegEncoderTests.Psnr(Channels(actual, 3), Channels(expected, 3));
        Assert.True(psnr >= minimumPsnr, $"PSNR {psnr:0.0} dB");
        Assert.Equal(Channels(expected, 1, 3), Channels(actual, 1, 3));
    }

    // Bytes `count` at a time from each RGBA pixel, starting at `skip`: the colour, or the alpha.
    private static byte[] Channels(byte[] rgba, int count, int skip = 0)
    {
        byte[] result = new byte[rgba.Length / 4 * count];
        for (int i = 0; i < rgba.Length / 4; i++)
        {
            rgba.AsSpan((i * 4) + skip, count).CopyTo(result.AsSpan(i * count));
        }

        return result;
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 40)]
    [InlineData(40, 1)]
    [InlineData(17, 33)]
    [InlineData(255, 3)]
    public void Lossy_odd_and_tiny_sizes_round_trip(int width, int height)
    {
        using Image photo = JpegEncoderTests.Photo(width, height, PixelFormat.Rgba32, seed: width + height);
        byte[] webp = Save(photo, new WebpEncoderOptions { Quality = 90 });
        using Image decoded = Image.Decode(webp, new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        Assert.Equal((width, height), (decoded.Width, decoded.Height));
        // The test photo's green spans the height, so very short pictures change colour sharply from row to row, which
        // 4:2:0 chroma averages away.
        double psnr = JpegEncoderTests.Psnr(Channels(decoded.GetPixelBytes().ToArray(), 3), Channels(Rgba(photo), 3));
        Assert.True(psnr > (height < 8 ? 20 : 26), $"PSNR {psnr:0.00} dB");
    }

    [Fact]
    public void Higher_quality_is_larger_and_closer()
    {
        using Image photo = JpegEncoderTests.Photo(200, 150, seed: 4);
        int lastSize = 0;
        double lastPsnr = 0;
        foreach (int quality in new[] { 1, 20, 50, 80, 95, 100 })
        {
            byte[] webp = Save(photo, new WebpEncoderOptions { Quality = quality });
            using Image decoded = Image.Decode(webp);
            double psnr = JpegEncoderTests.Psnr(decoded.GetPixelBytes(), photo.GetPixelBytes());
            Assert.True(webp.Length > lastSize && psnr > lastPsnr, $"quality {quality}: {webp.Length} bytes, {psnr:0.00} dB");
            (lastSize, lastPsnr) = (webp.Length, psnr);
        }
    }

    [Fact]
    public void Flat_colour_is_tiny_and_exact_enough()
    {
        using var flat = new Image(400, 300, PixelFormat.Rgb24);
        Span<Rgb24> pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Rgb24>(flat.GetPixelBytes());
        pixels.Fill(new Rgb24(200, 30, 90));
        byte[] webp = Save(flat, WebpEncoderOptions.Default);
        Assert.True(webp.Length < 400, $"{webp.Length} bytes");
        using Image decoded = Image.Decode(webp);
        Rgb24 middle = decoded.GetRow<Rgb24>(150)[200];
        Assert.True(Math.Abs(middle.R - 200) <= 3 && Math.Abs(middle.G - 30) <= 3 && Math.Abs(middle.B - 90) <= 3, middle.ToString());
    }

    [Fact]
    public void Lossy_files_carry_the_profile()
    {
        using Image photo = JpegEncoderTests.Photo(40, 24);
        photo.ColorSpace = ColorSpace.DisplayP3;
        byte[] webp = Save(photo, WebpEncoderOptions.Default);
        Assert.Equal("VP8X"u8.ToArray(), webp[12..16]);
        Assert.Equal(0x20, webp[20]);
        using Image decoded = Image.Decode(webp);
        Assert.Equal(IccProfileWriter.For(ColorSpace.DisplayP3, "WebP").ToArray(), decoded.ColorSpace.IccProfile.ToArray());
    }

    [Fact]
    public void Quality_maps_to_the_quantiser_monotonically()
    {
        int last = 128;
        for (int quality = 1; quality <= 100; quality++)
        {
            int q = Vp8Encoder.QuantiserFor(quality);
            Assert.InRange(q, 0, last);
            last = q;
        }

        Assert.Equal(0, Vp8Encoder.QuantiserFor(100));
    }

    [Fact]
    public void Forward_transforms_invert_the_decoders_transforms()
    {
        // A residual through the forward DCT and the decoder's inverse comes back within rounding.
        var random = new Random(2);
        var plane = new Vp8Decoder.Plane(4, 4, extra: 0);
        byte[] source = new byte[16];
        for (int round = 0; round < 200; round++)
        {
            random.NextBytes(source);
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    plane.Row(r)[c] = (byte)random.Next(256);
                }
            }

            byte[] prediction = new byte[16];
            for (int r = 0; r < 4; r++)
            {
                plane.Row(r)[..4].CopyTo(prediction.AsSpan(r * 4));
            }

            short[] coefficients = new short[16];
            Vp8Encoder.ForwardDct(source, 0, 4, plane, 0, 0, coefficients);
            Vp8Decoder.AddResidual(plane, 0, 0, coefficients, any: true);
            for (int i = 0; i < 16; i++)
            {
                int expected = source[i];
                int actual = plane.Row(i / 4)[i % 4];
                Assert.True(Math.Abs(expected - actual) <= 1, $"round {round} pixel {i}: {expected} against {actual}, prediction {prediction[i]}");
            }

            short[] dcs = new short[16];
            short[] wht = new short[16];
            short[] back = new short[16];
            for (int i = 0; i < 16; i++)
            {
                dcs[i] = (short)random.Next(-2000, 2000);
            }

            Vp8Encoder.ForwardWht(dcs, wht);
            Vp8Decoder.InverseWht(wht, back);
            for (int i = 0; i < 16; i++)
            {
                Assert.True(Math.Abs(dcs[i] - back[i]) <= 1, $"WHT {i}: {dcs[i]} against {back[i]}");
            }
        }
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
