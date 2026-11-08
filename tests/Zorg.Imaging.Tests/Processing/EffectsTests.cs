using System;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Tests.Codecs.Jpeg;

namespace Zorg.Imaging.Tests.Processing;

public class EffectsTests
{
    private static Image Solid(int width, int height, Rgba32 colour, PixelFormat format = PixelFormat.Rgba32)
    {
        var image = new Image(width, height, PixelFormat.Rgba32);
        for (int y = 0; y < height; y++)
        {
            image.GetRow<Rgba32>(y).Fill(colour);
        }

        if (format == PixelFormat.Rgba32)
        {
            return image;
        }

        using (image)
        {
            return Convert(image, format);
        }
    }

    private static Image Convert(Image rgba, PixelFormat format)
    {
        var result = new Image(rgba.Width, rgba.Height, format);
        for (int y = 0; y < rgba.Height; y++)
        {
            ReadOnlySpan<Rgba32> from = rgba.GetRow<Rgba32>(y);
            for (int x = 0; x < rgba.Width; x++)
            {
                if (format == PixelFormat.L8)
                {
                    result.GetRow<L8>(y)[x] = new L8(from[x].G);
                }
                else
                {
                    result.GetRow<Rgb24>(y)[x] = new Rgb24(from[x].R, from[x].G, from[x].B);
                }
            }
        }

        return result;
    }

    [Theory]
    [InlineData(PixelFormat.Rgba32)]
    [InlineData(PixelFormat.Rgb24)]
    [InlineData(PixelFormat.L8)]
    public void Blurring_or_sharpening_a_flat_image_changes_nothing(PixelFormat format)
    {
        using Image flat = Solid(33, 21, new Rgba32(90, 140, 200, 255), format);
        using Image blurred = flat.Blur(3);
        using Image sharpened = flat.Sharpen(2, 3);
        Assert.Equal(flat.GetPixelBytes().ToArray(), blurred.GetPixelBytes().ToArray());
        Assert.Equal(flat.GetPixelBytes().ToArray(), sharpened.GetPixelBytes().ToArray());
    }

    [Fact]
    public void A_blur_spreads_light_evenly_and_keeps_it()
    {
        // A single white pixel on black, in a grey image: the blur keeps the light (in linear light), spreads it the
        // same way in every direction, and falls off with distance.
        using var image = new Image(41, 41, PixelFormat.L16);
        image.GetRow<L16>(20)[20] = new L16(65535);
        using Image blurred = image.Blur(2);
        double total = 0;
        for (int y = 0; y < 41; y++)
        {
            for (int x = 0; x < 41; x++)
            {
                total += Linear(blurred.GetRow<L16>(y)[x].L / 65535.0);
            }
        }

        Assert.InRange(total, 0.97, 1.03);
        ushort Value(int x, int y) => blurred.GetRow<L16>(y)[x].L;
        Assert.Equal(Value(17, 20), Value(23, 20));
        Assert.Equal(Value(20, 17), Value(20, 23));
        Assert.Equal(Value(17, 20), Value(20, 17));
        Assert.True(Value(20, 20) > Value(21, 20) && Value(21, 20) > Value(22, 20) && Value(22, 20) > Value(25, 20));
        Assert.Equal(0, Value(0, 0));
    }

    [Fact]
    public void Transparent_pixels_lend_no_colour_to_a_blur()
    {
        // Left half opaque blue, right half transparent red: the blurred edge fades out, but stays blue.
        using var image = new Image(20, 6, PixelFormat.Rgba32);
        for (int y = 0; y < 6; y++)
        {
            Span<Rgba32> row = image.GetRow<Rgba32>(y);
            for (int x = 0; x < 20; x++)
            {
                row[x] = x < 10 ? new Rgba32(0, 0, 255, 255) : new Rgba32(255, 0, 0, 0);
            }
        }

        using Image blurred = image.Blur(2);
        for (int x = 8; x < 14; x++)
        {
            Rgba32 p = blurred.GetRow<Rgba32>(3)[x];
            Assert.True(p.A > 0 && p.A < 255, $"{x}: {p}");
            Assert.True(p.R <= 1 && p.B >= 254, $"{x}: {p}");
        }
    }

    [Fact]
    public void Sharpening_overshoots_on_both_sides_of_an_edge_and_leaves_alpha()
    {
        using var image = new Image(24, 4, PixelFormat.Rgba32);
        for (int y = 0; y < 4; y++)
        {
            Span<Rgba32> row = image.GetRow<Rgba32>(y);
            for (int x = 0; x < 24; x++)
            {
                row[x] = x < 12 ? new Rgba32(60, 60, 60, 200) : new Rgba32(180, 180, 180, 200);
            }
        }

        using Image sharp = image.Sharpen(1, 1);
        ReadOnlySpan<Rgba32> result = sharp.GetRow<Rgba32>(2);
        Assert.True(result[11].R < 60, result[11].ToString());
        Assert.True(result[12].R > 180, result[12].ToString());
        Assert.Equal(60, result[2].R);
        Assert.All(result.ToArray(), p => Assert.Equal(200, p.A));
    }

    [Fact]
    public void Drawing_an_image_composites_it_where_it_lands()
    {
        using Image background = Solid(10, 8, new Rgba32(0, 0, 200, 255), PixelFormat.Rgb24);
        using var badge = new Image(4, 3, PixelFormat.Rgba32);
        for (int y = 0; y < 3; y++)
        {
            Span<Rgba32> row = badge.GetRow<Rgba32>(y);
            row.Fill(new Rgba32(200, 100, 0, 255));
            row[0] = new Rgba32(0, 255, 0, 0);
        }

        // Off the left edge: only the part inside is drawn; the transparent column leaves the background.
        using Image drawn = background.DrawImage(badge, -1, 6);
        Assert.Equal(PixelFormat.Rgb24, drawn.PixelFormat);
        Assert.Equal(new Rgb24(200, 100, 0), drawn.GetRow<Rgb24>(6)[0]);
        Assert.Equal(new Rgb24(200, 100, 0), drawn.GetRow<Rgb24>(7)[2]);
        Assert.Equal(new Rgb24(0, 0, 200), drawn.GetRow<Rgb24>(7)[3]);
        Assert.Equal(new Rgb24(0, 0, 200), drawn.GetRow<Rgb24>(5)[0]);

        using Image half = background.DrawImage(badge, 2, 2, opacity: 0.5);
        Assert.Equal(new Rgb24(100, 50, 100), half.GetRow<Rgb24>(3)[4]);

        // A grey image takes colour from a colour overlay.
        using Image grey = Solid(6, 6, new Rgba32(128, 128, 128, 255), PixelFormat.L8);
        using Image coloured = grey.DrawImage(badge, 0, 0);
        Assert.Equal(PixelFormat.Rgb24, coloured.PixelFormat);
        Assert.Equal(new Rgb24(200, 100, 0), coloured.GetRow<Rgb24>(1)[1]);
        Assert.Equal(new Rgb24(128, 128, 128), coloured.GetRow<Rgb24>(1)[0]);
    }

    [Fact]
    public void Trimming_finds_the_content_inside_a_border()
    {
        using Image page = Solid(40, 30, new Rgba32(250, 250, 250, 255), PixelFormat.Rgb24);
        page.GetRow<Rgb24>(7)[5] = new Rgb24(0, 0, 0);
        page.GetRow<Rgb24>(20)[31] = new Rgb24(10, 10, 10);
        page.GetRow<Rgb24>(25)[35] = new Rgb24(245, 245, 245);
        using Image trimmed = page.Trim();
        Assert.Equal((27, 14), (trimmed.Width, trimmed.Height));
        Assert.Equal(new Rgb24(0, 0, 0), trimmed.GetRow<Rgb24>(0)[0]);

        using Image strict = page.Trim(threshold: 2);
        Assert.Equal((31, 19), (strict.Width, strict.Height));

        using Image flat = Solid(5, 5, new Rgba32(1, 2, 3, 255));
        using Image same = flat.Trim();
        Assert.Equal((5, 5), (same.Width, same.Height));
    }

    [Fact]
    public void A_blurhash_decodes_back_to_the_image_blurred()
    {
        // Smooth gradients across and down: the hash, decoded by the published algorithm, comes back close.
        using var image = new Image(48, 32, PixelFormat.Rgb24);
        for (int y = 0; y < 32; y++)
        {
            Span<Rgb24> row = image.GetRow<Rgb24>(y);
            for (int x = 0; x < 48; x++)
            {
                row[x] = new Rgb24((byte)(x * 5), (byte)(40 + (y * 5)), (byte)(255 - (x * 5)));
            }
        }

        string hash = image.ToBlurHash(4, 3);
        Assert.Equal(6 + (2 * 11), hash.Length);
        Assert.Equal('L', hash[0]);
        using Image decoded = DecodeBlurHash(hash, 48, 32);
        double psnr = JpegEncoderTests.Psnr(decoded.GetPixelBytes(), image.GetPixelBytes());
        Assert.True(psnr > 25, $"{psnr:0.0} dB");

        // The gradients run the right way: red rises to the right, blue falls, green rises downwards.
        Rgb24 topLeft = decoded.GetRow<Rgb24>(4)[4];
        Rgb24 bottomRight = decoded.GetRow<Rgb24>(28)[44];
        Assert.True(topLeft.R < bottomRight.R && topLeft.B > bottomRight.B && topLeft.G < bottomRight.G, $"{topLeft} {bottomRight}");

        Assert.Equal(6, image.ToBlurHash(1, 1).Length);
        Assert.Equal(6 + (2 * 80), image.ToBlurHash(9, 9).Length);
    }

    [Fact]
    public async Task The_pipeline_runs_the_effects_in_order()
    {
        using Image photo = JpegEncoderTests.Photo(60, 40, PixelFormat.Rgb24, seed: 9);
        using Image logo = Solid(8, 8, new Rgba32(255, 255, 255, 255));
        using Image expected = photo.Blur(1.5).Sharpen(0.8, 0.4).DrawImage(logo, 2, 3, 0.5).Trim(0);
        using Image result = await ImagePipeline.From(photo).Blur(1.5).Sharpen(0.8, 0.4).DrawImage(logo, 2, 3, 0.5).Trim(0)
            .ToImageAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected.GetPixelBytes().ToArray(), result.GetPixelBytes().ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => ImagePipeline.From(photo).Blur(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => photo.Sharpen(1, 11));
        Assert.Throws<ArgumentOutOfRangeException>(() => photo.ToBlurHash(10, 3));
    }

    private static double Linear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    // The BlurHash decoding, from the algorithm's description: the base-83 fields, then the cosine sum per pixel.
    private static Image DecodeBlurHash(string hash, int width, int height)
    {
        const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz#$%*+,-.:;=?@[]^_{|}~";
        int Read(int from, int to)
        {
            int value = 0;
            for (int i = from; i < to; i++)
            {
                value = (value * 83) + Digits.IndexOf(hash[i], StringComparison.Ordinal);
            }

            return value;
        }

        int size = Read(0, 1);
        int componentsX = (size % 9) + 1;
        int componentsY = (size / 9) + 1;
        double maximum = (Read(1, 2) + 1) / 166.0;
        var colours = new (double R, double G, double B)[componentsX * componentsY];
        int dc = Read(2, 6);
        colours[0] = (Linear((dc >> 16) / 255.0), Linear(((dc >> 8) & 255) / 255.0), Linear((dc & 255) / 255.0));
        for (int k = 1; k < colours.Length; k++)
        {
            int value = Read(4 + (2 * k), 6 + (2 * k));
            double Component(int q)
            {
                double v = (q - 9) / 9.0;
                return Math.CopySign(v * v, v) * maximum;
            }

            colours[k] = (Component(value / (19 * 19)), Component(value / 19 % 19), Component(value % 19));
        }

        var image = new Image(width, height, PixelFormat.Rgb24);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double r = 0, g = 0, b = 0;
                for (int j = 0; j < componentsY; j++)
                {
                    for (int i = 0; i < componentsX; i++)
                    {
                        double basis = Math.Cos(Math.PI * x * i / width) * Math.Cos(Math.PI * y * j / height);
                        (double cr, double cg, double cb) = colours[(j * componentsX) + i];
                        r += basis * cr;
                        g += basis * cg;
                        b += basis * cb;
                    }
                }

                static byte Encode(double l)
                {
                    double c = Math.Clamp(l, 0, 1);
                    return (byte)Math.Round(255 * (c <= 0.0031308 ? c * 12.92 : (1.055 * Math.Pow(c, 1 / 2.4)) - 0.055));
                }

                image.GetRow<Rgb24>(y)[x] = new Rgb24(Encode(r), Encode(g), Encode(b));
            }
        }

        return image;
    }
}
