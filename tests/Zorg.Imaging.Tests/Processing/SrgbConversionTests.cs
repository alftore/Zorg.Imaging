using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Metadata;
using Zorg.Imaging.Processing;
using Zorg.Imaging.Tests.Codecs.Jpeg;

namespace Zorg.Imaging.Tests.Processing;

public class SrgbConversionTests
{
    private static readonly double[] P3 = ColourMath.Chromaticities(12)!;

    [Theory]
    [InlineData(12)]
    [InlineData(9)]
    public void Our_own_profiles_read_back_as_their_primaries_and_curve(int primaries)
    {
        ColorSpace space = ColorSpace.FromCicp(primaries, primaries == 9 ? 1 : 13);
        byte[] profile = IccProfileWriter.For(space, "test").ToArray();
        IccProfileReader.Model model = IccProfileReader.Read(profile)!;
        double[] expected = ColourMath.RgbToPcs(ColourMath.Chromaticities(primaries)!);
        for (int i = 0; i < 9; i++)
        {
            Assert.Equal(expected[i], model.ToPcs![i], 3);
        }

        TransferFunction curve = TransferFunction.For(space);
        for (int v = 0; v <= 255; v += 5)
        {
            Assert.Equal(curve.ToLinear(v / 255.0), model.Curves[0](v / 255.0), 3);
        }
    }

    [Fact]
    public void Greys_stay_grey_and_white_stays_white()
    {
        using Image image = Fill(PixelFormat.Rgb24, ColorSpace.DisplayP3, (0, 0, 0), (128, 128, 128), (255, 255, 255), (37, 37, 37));
        using Image srgb = image.ToSrgb();
        Assert.Equal(image.GetPixelBytes().ToArray(), srgb.GetPixelBytes().ToArray());
        Assert.True(srgb.ColorSpace.IsSrgb);
    }

    [Fact]
    public void A_saturated_p3_red_is_outside_and_clips()
    {
        using Image image = Fill(PixelFormat.Rgba32, ColorSpace.DisplayP3, (255, 0, 0));
        Assert.Null(SrgbConverter.Convert(image, onlyIfInside: true));
        using Image clipped = image.ToSrgb();
        Rgba32 red = clipped.GetRow<Rgba32>(0)[0];
        Assert.Equal((255, 0, 0, 200), (red.R, red.G, red.B, red.A));
    }

    [Fact]
    public void Muted_p3_colours_match_a_reference_conversion()
    {
        var random = new Random(4);
        using var image = new Image(64, 64, PixelFormat.Rgb24) { ColorSpace = ColorSpace.DisplayP3 };
        Span<byte> pixels = image.GetPixelBytes();
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(100 + random.Next(60));
        }

        using Image converted = SrgbConverter.Convert(image, onlyIfInside: true)!;
        Assert.NotNull(converted);
        double[] m = ColourMath.Multiply(ColourMath.Invert(ColourMath.RgbToPcs(ColourMath.Chromaticities(1)!)), ColourMath.RgbToPcs(P3));
        ReadOnlySpan<byte> output = converted.GetPixelBytes();
        for (int i = 0; i < pixels.Length; i += 3)
        {
            double r = SrgbToLinear(pixels[i]), g = SrgbToLinear(pixels[i + 1]), b = SrgbToLinear(pixels[i + 2]);
            for (int c = 0; c < 3; c++)
            {
                double l = (m[c * 3] * r) + (m[(c * 3) + 1] * g) + (m[(c * 3) + 2] * b);
                int expected = (int)Math.Round(LinearToSrgb(Math.Clamp(l, 0, 1)) * 255);
                Assert.InRange(output[i + c], expected - 1, expected + 1);
            }
        }
    }

    [Fact]
    public void Png_gamma_greys_convert_through_the_curve()
    {
        using var image = new Image(256, 1, PixelFormat.L8) { ColorSpace = ColorSpace.FromPngGamma(55556, null) };
        for (int x = 0; x < 256; x++)
        {
            image.GetRowBytes(0)[x] = (byte)x;
        }

        using Image srgb = image.ToSrgb();
        for (int x = 0; x < 256; x++)
        {
            int expected = (int)Math.Round(LinearToSrgb(Math.Pow(x / 255.0, 100_000.0 / 55556)) * 255);
            Assert.InRange(srgb.GetRowBytes(0)[x], expected - 1, expected + 1);
        }
    }

    [Theory]
    [InlineData(PixelFormat.Rgb48)]
    [InlineData(PixelFormat.Rgba64)]
    [InlineData(PixelFormat.Bgra32)]
    public void Every_layout_keeps_its_alpha_and_order(PixelFormat format)
    {
        using Image image = Fill(format, ColorSpace.Rec2020, (100, 120, 140));
        using Image srgb = image.ToSrgb();
        Assert.Equal(format, srgb.PixelFormat);
        using Image p3 = Fill(format, ColorSpace.DisplayP3, (128, 128, 128));
        using Image same = p3.ToSrgb();
        Assert.Equal(p3.GetPixelBytes().ToArray(), same.GetPixelBytes().ToArray());
    }

    [Fact]
    public void Hdr_and_unknown_profiles_are_not_converted()
    {
        using Image pq = Fill(PixelFormat.Rgba64, ColorSpace.Rec2020Pq, (1, 2, 3));
        Assert.Throws<NotSupportedException>(() => pq.ToSrgb());
        Assert.False(SrgbConverter.CanConvert(ColorSpace.Rec2020Pq));

        byte[] lut = IccProfileWriter.For(ColorSpace.DisplayP3, "test").ToArray();
        "CMYK"u8.CopyTo(lut.AsSpan(16));
        Assert.False(SrgbConverter.CanConvert(ColorSpace.FromIccProfile(lut)));
    }

    [Fact]
    public void Damaged_profiles_never_throw()
    {
        byte[] profile = IccProfileWriter.For(ColorSpace.DisplayP3, "test").ToArray();
        var random = new Random(9);
        for (int round = 0; round < 20_000; round++)
        {
            byte[] copy = (byte[])profile.Clone();
            for (int f = random.Next(1, 6); f > 0; f--)
            {
                copy[random.Next(copy.Length)] = (byte)random.Next(256);
            }

            IccProfileReader.Model? model = IccProfileReader.Read(copy.AsSpan(0, random.Next(copy.Length + 1)));
            if (model is not null)
            {
                _ = model.Curves[0](0.5);
            }
        }
    }

    [Fact]
    public void Jpeg_and_webp_drop_the_profile_when_the_colours_fit_and_keep_it_when_not()
    {
        using Image muted = Fill(PixelFormat.Rgb24, ColorSpace.DisplayP3, (120, 130, 140), (90, 80, 70));
        using Image vivid = Fill(PixelFormat.Rgb24, ColorSpace.DisplayP3, (255, 0, 0), (90, 80, 70));

        Assert.True(Decode(SaveJpeg(muted, new JpegEncoderOptions())).ColorSpace.IsSrgb);
        Assert.False(Decode(SaveJpeg(vivid, new JpegEncoderOptions())).ColorSpace.IsSrgb);
        Assert.False(Decode(SaveJpeg(muted, new JpegEncoderOptions { ColorSpaceHandling = ColorSpaceHandling.Keep })).ColorSpace.IsSrgb);
        Assert.True(Decode(SaveJpeg(vivid, new JpegEncoderOptions { ColorSpaceHandling = ColorSpaceHandling.Srgb })).ColorSpace.IsSrgb);

        using var webp = new MemoryStream();
        muted.SaveAsWebp(webp, new WebpEncoderOptions { Lossless = true });
        using Image back = Image.Decode(webp.ToArray());
        Assert.True(back.ColorSpace.IsSrgb);
        using Image expected = muted.ToSrgb();
        Assert.Equal(expected.GetPixelBytes().ToArray(), back.GetPixelBytes().ToArray());

        // PNG keeps it unless asked.
        using var png = new MemoryStream();
        muted.SaveAsPng(png);
        Assert.False(Decode(png.ToArray()).ColorSpace.IsSrgb);
        using var pngSrgb = new MemoryStream();
        muted.SaveAsPng(pngSrgb, new PngEncoderOptions { ColorSpaceHandling = ColorSpaceHandling.SrgbWhenLossless });
        Assert.True(Decode(pngSrgb.ToArray()).ColorSpace.IsSrgb);

        // The caller's image is untouched.
        Assert.Equal(ColorSpace.DisplayP3, muted.ColorSpace);
        Assert.Equal(120, muted.GetRowBytes(0)[0]);
    }

    [Fact]
    public void The_result_is_the_same_on_one_core_and_on_many()
    {
        using Image photo = JpegEncoderTests.Photo(900, 700, PixelFormat.Rgba32, seed: 2);
        photo.ColorSpace = ColorSpace.Rec2020;
        using Image one = Convert(photo, 1);
        using Image many = Convert(photo, 8);
        Assert.Equal(one.GetPixelBytes().ToArray(), many.GetPixelBytes().ToArray());

        static Image Convert(Image image, int degree)
        {
            using Parallelism.Scope scope = Parallelism.Use(degree);
            return image.ToSrgb();
        }
    }

    [Theory]
    [InlineData(12, 13)]
    [InlineData(9, 1)]
    public async Task Conversion_matches_littlecms_through_imagemagick(int primaries, int transfer)
    {
        string magick = Oracle.Require("magick");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        ColorSpace space = ColorSpace.FromCicp(primaries, transfer);
        using Image photo = JpegEncoderTests.Photo(97, 61, PixelFormat.Rgb24, seed: primaries);
        photo.ColorSpace = space;
        await File.WriteAllBytesAsync(folder.File("in.rgb"), photo.GetPixelBytes().ToArray(), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(folder.File("source.icc"), IccProfileWriter.For(space, "test").ToArray(), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            folder.File("srgb.icc"),
            IccProfileWriter.Write("sRGB", ColourMath.Chromaticities(1)!, IccProfileWriter.Curve.Srgb),
            TestContext.Current.CancellationToken);
        (int exit, _, string errors) = await Oracle.RunAsync(
            magick, "-size", "97x61", "-depth", "8", "rgb:" + folder.File("in.rgb"), "-profile", folder.File("source.icc"),
            "-intent", "Relative", "-profile", folder.File("srgb.icc"), "-depth", "8", "rgb:" + folder.File("out.rgb"));
        Assert.True(exit == 0, errors);
        byte[] expected = await File.ReadAllBytesAsync(folder.File("out.rgb"), TestContext.Current.CancellationToken);
        using Image ours = photo.ToSrgb();
        ReadOnlySpan<byte> actual = ours.GetPixelBytes();
        int worst = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(expected[i] - actual[i]));
        }

        Assert.True(worst <= 1, $"largest difference {worst}");
    }

    private static Image Decode(byte[] file) => Image.Decode(file);

    private static byte[] SaveJpeg(Image image, JpegEncoderOptions options)
    {
        using var output = new MemoryStream();
        image.SaveAsJpeg(output, options);
        return output.ToArray();
    }

    private static Image Fill(PixelFormat format, ColorSpace space, params (int R, int G, int B)[] colours)
    {
        var image = new Image(colours.Length, 1, format) { ColorSpace = space };
        for (int i = 0; i < colours.Length; i++)
        {
            (int r, int g, int b) = colours[i];
            switch (format)
            {
                case PixelFormat.Rgb24:
                    image.GetRow<Rgb24>(0)[i] = new Rgb24((byte)r, (byte)g, (byte)b);
                    break;
                case PixelFormat.Rgba32:
                    image.GetRow<Rgba32>(0)[i] = new Rgba32((byte)r, (byte)g, (byte)b, 200);
                    break;
                case PixelFormat.Bgra32:
                    image.GetRow<Bgra32>(0)[i] = new Bgra32((byte)b, (byte)g, (byte)r, 200);
                    break;
                case PixelFormat.Rgb48:
                    image.GetRow<Rgb48>(0)[i] = new Rgb48((ushort)(r * 257), (ushort)(g * 257), (ushort)(b * 257));
                    break;
                default:
                    image.GetRow<Rgba64>(0)[i] = new Rgba64((ushort)(r * 257), (ushort)(g * 257), (ushort)(b * 257), 50000);
                    break;
            }
        }

        return image;
    }

    private static double SrgbToLinear(byte v)
    {
        double x = v / 255.0;
        return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
    }

    private static double LinearToSrgb(double l) => l <= 0.0031308 ? l * 12.92 : (1.055 * Math.Pow(l, 1 / 2.4)) - 0.055;
}
