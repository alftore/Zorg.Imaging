using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Tests.Codecs.Jpeg;

namespace Zorg.Imaging.Tests.Codecs.Png;

/// <summary>Lossy PNG: <see cref="PngEncoderOptions.MaxColors"/> reduces to a palette with full alpha.</summary>
public class PngLossyTests
{
    // A photo with a soft round alpha falloff, as a cut-out has.
    internal static Image CutOut(int width, int height, int seed)
    {
        using Image photo = JpegEncoderTests.Photo(width, height, PixelFormat.Rgb24, seed);
        var image = new Image(width, height, PixelFormat.Rgba32);
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<Rgb24> from = photo.GetRow<Rgb24>(y);
            Span<Rgba32> to = image.GetRow<Rgba32>(y);
            for (int x = 0; x < width; x++)
            {
                double d = Math.Sqrt(Math.Pow(x - (width / 2.0), 2) + Math.Pow(y - (height / 2.0), 2)) / (height / 2.0);
                to[x] = new Rgba32(from[x].R, from[x].G, from[x].B, (byte)Math.Clamp((1.3 - d) * 255, 0, 255));
            }
        }

        return image;
    }

    // PSNR over premultiplied colour and alpha, so the hidden colour of transparent pixels doesn't count.
    internal static double Psnr(Image expected, Image actual)
    {
        double squares = 0;
        long count = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            ReadOnlySpan<Rgba32> e = expected.GetRow<Rgba32>(y);
            ReadOnlySpan<Rgba32> a = actual.GetRow<Rgba32>(y);
            for (int x = 0; x < expected.Width; x++)
            {
                double ea = e[x].A / 255.0;
                double aa = a[x].A / 255.0;
                squares += Math.Pow((e[x].R * ea) - (a[x].R * aa), 2) + Math.Pow((e[x].G * ea) - (a[x].G * aa), 2)
                    + Math.Pow((e[x].B * ea) - (a[x].B * aa), 2) + Math.Pow(e[x].A - a[x].A, 2);
                count += 4;
            }
        }

        return 10 * Math.Log10(255.0 * 255 / (squares / count));
    }

    private static Image DecodeRgba(byte[] png) => Image.Decode(png, new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });

    [Theory]
    [InlineData(256, 1.0)]
    [InlineData(256, 0.0)]
    [InlineData(32, 0.5)]
    public void A_cut_out_becomes_a_much_smaller_palette_file(int colours, double dithering)
    {
        using Image image = CutOut(160, 120, seed: 4);
        byte[] lossless = PngEncoderTests.Save(image);
        byte[] lossy = PngEncoderTests.Save(image, new PngEncoderOptions { MaxColors = colours, Dithering = dithering });
        Assert.Equal(3, lossy[25]);
        Assert.True(lossy.Length < lossless.Length / 2, $"{lossy.Length} against {lossless.Length}");
        using Image back = DecodeRgba(lossy);
        double psnr = Psnr(image, back);
        Assert.True(psnr > (colours == 256 ? 30 : 25), $"{psnr:0.0} dB");

        // Fully transparent stays fully transparent, and opaque stays opaque.
        for (int y = 0; y < image.Height; y++)
        {
            ReadOnlySpan<Rgba32> e = image.GetRow<Rgba32>(y);
            ReadOnlySpan<Rgba32> a = back.GetRow<Rgba32>(y);
            for (int x = 0; x < image.Width; x++)
            {
                if (e[x].A == 0)
                {
                    Assert.Equal(0, a[x].A);
                }
            }
        }
    }

    [Fact]
    public void An_opaque_image_stays_opaque()
    {
        using Image photo = JpegEncoderTests.Photo(120, 90, PixelFormat.Rgb24, seed: 8);
        byte[] lossy = PngEncoderTests.Save(photo, new PngEncoderOptions { MaxColors = 64 });
        using Image back = Image.Decode(lossy);
        Assert.Equal(PixelFormat.Rgb24, back.PixelFormat);
        double psnr = JpegEncoderTests.Psnr(back.GetPixelBytes(), photo.GetPixelBytes());
        Assert.True(psnr > 22, $"{psnr:0.0} dB");

        // Without dithering, at least as close as the GIF encoder's Wu quantiser.
        using Image plain = Image.Decode(PngEncoderTests.Save(photo, new PngEncoderOptions { MaxColors = 64, Dithering = 0 }));
        using Image gif = Image.Decode(Gif.GifEncoderTests.Save(photo, new GifEncoderOptions { MaxColors = 64, Dithering = 0 }));
        double ours = JpegEncoderTests.Psnr(plain.GetPixelBytes(), photo.GetPixelBytes());
        double wu = JpegEncoderTests.Psnr(gif.GetPixelBytes(), photo.GetPixelBytes());
        Assert.True(ours >= wu - 0.3, $"{ours:0.00} dB against Wu's {wu:0.00} dB");
    }

    [Fact]
    public void Few_colours_are_kept_exactly()
    {
        using var image = new Image(20, 10, PixelFormat.Rgba32);
        for (int y = 0; y < 10; y++)
        {
            Span<Rgba32> row = image.GetRow<Rgba32>(y);
            for (int x = 0; x < 20; x++)
            {
                row[x] = ((x + y) % 3) switch
                {
                    0 => new Rgba32(255, 0, 0, 255),
                    1 => new Rgba32(0, 128, 255, 77),
                    _ => new Rgba32(9, 9, 9, 0),
                };
            }
        }

        using Image back = DecodeRgba(PngEncoderTests.Save(image, new PngEncoderOptions { MaxColors = 3 }));
        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                Rgba32 e = image.GetRow<Rgba32>(y)[x];
                Rgba32 a = back.GetRow<Rgba32>(y)[x];
                Assert.Equal(e.A == 0 ? new Rgba32(0, 0, 0, 0) : e, a);
            }
        }
    }

    [Fact]
    public void The_options_are_checked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PngEncoderOptions { MaxColors = 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PngEncoderOptions { MaxColors = 257 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PngEncoderOptions { Dithering = 1.5 });
        Assert.Equal(0, PngEncoderOptions.Default.MaxColors);
    }

    [Fact]
    public void The_result_is_the_same_on_any_number_of_cores()
    {
        using Image image = CutOut(300, 200, seed: 6);
        byte[] one = PngEncoderTests.Save(image, new PngEncoderOptions { MaxColors = 200, MaxDegreeOfParallelism = 1 });
        byte[] many = PngEncoderTests.Save(image, new PngEncoderOptions { MaxColors = 200, MaxDegreeOfParallelism = 8 });
        Assert.Equal(one, many);
    }

    [Theory]
    [InlineData(256)]
    [InlineData(64)]
    public async Task Our_reduction_is_about_as_close_as_magicks(int colours)
    {
        string magick = Oracle.Require("magick");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        using Image image = CutOut(300, 200, seed: 12);
        await File.WriteAllBytesAsync(folder.File("in.png"), PngEncoderTests.Save(image), TestContext.Current.CancellationToken);
        (int written, _, string errors) = await Oracle.RunAsync(magick, folder.File("in.png"), "-dither", "None", "-colors", $"{colours}", "PNG8:" + folder.File("magick.png"));
        Assert.True(written == 0, errors);
        byte[] theirs = await File.ReadAllBytesAsync(folder.File("magick.png"), TestContext.Current.CancellationToken);
        using Image theirImage = DecodeRgba(theirs);
        byte[] ours = PngEncoderTests.Save(image, new PngEncoderOptions { MaxColors = colours, Dithering = 0 });
        using Image ourImage = DecodeRgba(ours);
        double theirPsnr = Psnr(image, theirImage);
        double ourPsnr = Psnr(image, ourImage);
        Assert.True(ourPsnr >= theirPsnr - 0.5, $"ours {ourPsnr:0.00} dB ({ours.Length} bytes), magick {theirPsnr:0.00} dB ({theirs.Length} bytes)");
    }
}
