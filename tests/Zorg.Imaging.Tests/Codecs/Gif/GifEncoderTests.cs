using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Metadata;
using Zorg.Imaging.Tests.Codecs.Jpeg;

namespace Zorg.Imaging.Tests.Codecs.Gif;

public class GifEncoderTests
{
    internal static byte[] Save(Image image, GifEncoderOptions? options = null)
    {
        using var output = new MemoryStream();
        image.SaveAsGif(output, options);
        return output.ToArray();
    }

    [Fact]
    public void Few_colours_round_trip_exactly()
    {
        using var image = new Image(37, 23, PixelFormat.Rgb24);
        Span<Rgb24> pixels = MemoryMarshal.Cast<byte, Rgb24>(image.GetPixelBytes());
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Rgb24((byte)(i % 5 * 50), (byte)(i % 3 * 100), (byte)(i % 7 * 30));
        }

        byte[] gif = Save(image);
        Assert.Equal("GIF89a"u8.ToArray(), gif[..6]);
        using Image decoded = Image.Decode(gif);
        Assert.Equal(PixelFormat.Rgb24, decoded.PixelFormat);
        Assert.Equal(image.GetPixelBytes().ToArray(), decoded.GetPixelBytes().ToArray());
    }

    [Fact]
    public void Grey_keeps_all_256_levels()
    {
        using var image = new Image(256, 3, PixelFormat.L8);
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 256; x++)
            {
                image.GetRowBytes(y)[x] = (byte)x;
            }
        }

        using Image decoded = Image.Decode(Save(image));
        for (int x = 0; x < 256; x++)
        {
            Assert.Equal(new Rgb24((byte)x, (byte)x, (byte)x), decoded.GetRow<Rgb24>(2)[x]);
        }
    }

    [Fact]
    public void Alpha_becomes_one_bit()
    {
        using var image = new Image(4, 1, PixelFormat.Rgba32);
        Span<Rgba32> pixels = image.GetRow<Rgba32>(0);
        pixels[0] = new Rgba32(10, 20, 30, 255);
        pixels[1] = new Rgba32(10, 20, 30, 128);
        pixels[2] = new Rgba32(200, 0, 0, 127);
        pixels[3] = new Rgba32(0, 0, 0, 0);
        using Image decoded = Image.Decode(Save(image));
        Assert.Equal(PixelFormat.Rgba32, decoded.PixelFormat);
        Span<Rgba32> back = decoded.GetRow<Rgba32>(0);
        Assert.Equal(new Rgba32(10, 20, 30, 255), back[0]);
        Assert.Equal(new Rgba32(10, 20, 30, 255), back[1]);
        Assert.Equal(0, back[2].A);
        Assert.Equal(0, back[3].A);
    }

    [Fact]
    public void A_wholly_transparent_image_is_written()
    {
        using var image = new Image(3, 2, PixelFormat.Rgba32);
        using Image decoded = Image.Decode(Save(image));
        Assert.All(MemoryMarshal.Cast<byte, Rgba32>(decoded.GetPixelBytes()).ToArray(), p => Assert.Equal(0, p.A));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 50)]
    [InlineData(301, 7)]
    public void Odd_sizes_round_trip(int width, int height)
    {
        using Image photo = JpegEncoderTests.Photo(width, height, PixelFormat.Rgb24, seed: width);
        using Image decoded = Image.Decode(Save(photo));
        Assert.Equal((width, height), (decoded.Width, decoded.Height));
    }

    [Theory]
    [InlineData(1.0, 27)]
    [InlineData(0.0, 30)]
    [InlineData(0.5, 28.5)]
    public void Photos_are_reduced_with_good_fidelity(double dithering, double minimumPsnr)
    {
        using Image photo = JpegEncoderTests.Photo(200, 150, PixelFormat.Rgb24, seed: 4);
        using Image decoded = Image.Decode(Save(photo, new GifEncoderOptions { Dithering = dithering }));
        double psnr = JpegEncoderTests.Psnr(decoded.GetPixelBytes(), photo.GetPixelBytes());
        Assert.True(psnr >= minimumPsnr, $"PSNR {psnr:0.0} dB");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(16)]
    [InlineData(100)]
    public void Max_colors_is_respected_and_fewer_colours_make_smaller_files(int colours)
    {
        using Image photo = JpegEncoderTests.Photo(120, 80, PixelFormat.Rgba32, seed: 6);
        byte[] few = Save(photo, new GifEncoderOptions { MaxColors = colours });
        byte[] all = Save(photo);
        Assert.True(few.Length < all.Length, $"{few.Length} against {all.Length}");
        using Image decoded = Image.Decode(few, new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        var seen = new HashSet<Rgba32>();
        foreach (Rgba32 p in MemoryMarshal.Cast<byte, Rgba32>(decoded.GetPixelBytes()))
        {
            seen.Add(p);
        }

        Assert.InRange(seen.Count, 1, colours);
    }

    [Fact]
    public void The_file_is_the_same_on_one_core_and_on_many()
    {
        using Image photo = JpegEncoderTests.Photo(700, 500, PixelFormat.Rgba32, seed: 8);
        foreach (double dithering in new[] { 0.0, 1.0 })
        {
            byte[] one = Save(photo, new GifEncoderOptions { Dithering = dithering, MaxDegreeOfParallelism = 1 });
            byte[] many = Save(photo, new GifEncoderOptions { Dithering = dithering, MaxDegreeOfParallelism = 8 });
            Assert.Equal(one, many);
        }
    }

    [Fact]
    public void The_colour_space_is_kept_as_a_profile()
    {
        using Image photo = JpegEncoderTests.Photo(10, 10);
        photo.ColorSpace = ColorSpace.DisplayP3;
        using Image decoded = Image.Decode(Save(photo));
        Assert.Equal(IccProfileWriter.For(ColorSpace.DisplayP3, "GIF").ToArray(), decoded.ColorSpace.IccProfile.ToArray());

        using Image plain = Image.Decode(Save(JpegEncoderTests.Photo(10, 10)));
        Assert.True(plain.ColorSpace.IsSrgb);
    }

    [Fact]
    public void What_gif_cannot_hold_is_refused()
    {
        using Image photo = JpegEncoderTests.Photo(8, 8);
        photo.ColorSpace = ColorSpace.Rec2020Pq;
        Assert.Throws<NotSupportedException>(() => Save(photo));
        using var wide = new Image(65536, 1, PixelFormat.L8);
        Assert.Throws<NotSupportedException>(() => Save(wide));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GifEncoderOptions { MaxColors = 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new GifEncoderOptions { MaxColors = 257 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new GifEncoderOptions { Dithering = double.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new GifEncoderOptions { Dithering = 1.5 });
    }

    [Fact]
    public async Task The_pipeline_and_async_save_write_the_same_file()
    {
        using Image photo = JpegEncoderTests.Photo(64, 48, PixelFormat.Rgb24, seed: 2);
        byte[] expected = Save(photo);
        using var async = new MemoryStream();
        await photo.SaveAsGifAsync(async, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected, async.ToArray());

        using var piped = new MemoryStream();
        await ImagePipeline.From(photo).SaveAsGifAsync(piped, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected, piped.ToArray());
    }
}
