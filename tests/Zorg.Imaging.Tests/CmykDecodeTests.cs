using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Codecs.Jpeg;
using Zorg.Imaging.Tests.Codecs.Png;
using Zorg.Imaging.Tests.Codecs.Tiff;
using Zorg.Imaging.Tests.Metadata;
using static Zorg.Imaging.Tests.Codecs.Jpeg.JpegTestFiles;

namespace Zorg.Imaging.Tests;

/// <summary><see cref="Image.DecodeCmyk"/>: a CMYK file's inks, as planes, instead of sRGB.</summary>
public class CmykDecodeTests
{
    [Theory]
    [InlineData("ycck")]
    [InlineData("ycck-subsampled")]
    public void The_inks_convert_to_what_Decode_gives(string name)
    {
        byte[] jpeg = Sample(name);
        using CmykImage? inks = Image.DecodeCmyk(jpeg);
        Assert.NotNull(inks);
        using Image decoded = Image.Decode(jpeg);
        Assert.Equal((decoded.Width, decoded.Height), (inks.Width, inks.Height));
        foreach (Image plane in new[] { inks.Cyan, inks.Magenta, inks.Yellow, inks.Black })
        {
            Assert.Equal(PixelFormat.L8, plane.PixelFormat);
            Assert.Same(ColorSpace.LinearSrgb, plane.ColorSpace);
        }

        using Image srgb = inks.ToSrgb();
        Assert.Equal(decoded.GetPixelBytes().ToArray(), srgb.GetPixelBytes().ToArray());
        Assert.True(inks.IccProfile.IsEmpty);
    }

    [Fact]
    public void The_planes_hold_each_pixel_s_inks()
    {
        // The YCCK samples, decoded as RGB, are the C, M, Y inks; the check is the device formula on the planes.
        using CmykImage inks = Image.DecodeCmyk(Sample("ycck"))!;
        byte[] interleaved = inks.ToInterleaved();
        Assert.Equal(inks.Width * inks.Height * 4, interleaved.Length);
        using Image decoded = Image.Decode(Sample("ycck"));
        for (int y = 0; y < inks.Height; y += 7)
        {
            for (int x = 0; x < inks.Width; x += 5)
            {
                int at = ((y * inks.Width) + x) * 4;
                (byte c, byte m, byte ye, byte k) = (interleaved[at], interleaved[at + 1], interleaved[at + 2], interleaved[at + 3]);
                Assert.Equal((c, m, ye, k), (inks.Cyan.GetRowBytes(y)[x], inks.Magenta.GetRowBytes(y)[x], inks.Yellow.GetRowBytes(y)[x], inks.Black.GetRowBytes(y)[x]));
                Rgb24 p = decoded.GetRow<Rgb24>(y)[x];
                Assert.Equal(JpegCmyk.Device(c, m, ye, k), (p.R, p.G, p.B));
            }
        }
    }

    [Fact]
    public void Files_that_aren_t_cmyk_give_null_and_non_images_throw()
    {
        Assert.Null(Image.DecodeCmyk(Sample("s420")));
        using Image small = new(3, 2, PixelFormat.Rgb24);
        Assert.Null(Image.DecodeCmyk(PngEncoderTests.Save(small)));
        Assert.Throws<UnsupportedImageFormatException>(() => Image.DecodeCmyk(new byte[] { 1, 2, 3, 4 }));
        Assert.Throws<ImageFormatException>(() => Image.DecodeCmyk(Sample("ycck").AsMemory(0, 400)));
    }

    [Fact]
    public void Shrink_on_load_and_orientation_apply_as_for_Decode()
    {
        byte[] jpeg = Sample("ycck-subsampled");
        var options = new DecodeOptions { ShrinkTo = (17, 12) };
        using CmykImage shrunk = Image.DecodeCmyk(jpeg, options)!;
        using Image decoded = Image.Decode(jpeg, options);
        Assert.Equal((decoded.Width, decoded.Height), (shrunk.Width, shrunk.Height));
        Assert.True(shrunk.Width < 67);
        using (Image srgb = shrunk.ToSrgb())
        {
            Assert.Equal(decoded.GetPixelBytes().ToArray(), srgb.GetPixelBytes().ToArray());
        }

        // Orientation 6: the planes come out turned a quarter clockwise, as the decoded picture does.
        byte[] turned = WithSegments(jpeg, (0xE1, [.. "Exif\0\0"u8, .. PngTestFiles.ExifWithOrientation(6)]));
        using CmykImage upright = Image.DecodeCmyk(turned)!;
        using CmykImage stored = Image.DecodeCmyk(turned, new DecodeOptions { ApplyOrientation = false })!;
        using CmykImage rotated = stored.Map(plane => plane.Rotate(90));
        Assert.Equal((45, 67), (upright.Width, upright.Height));
        Assert.Equal(rotated.ToInterleaved(), upright.ToInterleaved());
    }

    [Fact]
    public void A_cmyk_profile_is_kept_and_used_for_the_screen()
    {
        byte[] profile = IccTestProfiles.NaiveCmyk(IccTestProfiles.TagType.Lut16, points: 17);
        byte[] jpeg = WithIcc(Sample("ycck"), profile);
        using CmykImage inks = Image.DecodeCmyk(jpeg)!;
        Assert.Equal(profile, inks.IccProfile.ToArray());
        using Image decoded = Image.Decode(jpeg);
        using Image srgb = inks.ToSrgb();
        Assert.Equal(decoded.GetPixelBytes().ToArray(), srgb.GetPixelBytes().ToArray());
    }

    [Fact]
    public void Map_resizes_every_ink_and_refuses_what_doesn_t_fit()
    {
        using CmykImage inks = Image.DecodeCmyk(Sample("ycck"))!;
        using CmykImage half = inks.Map(plane => plane.Fit(34, 34));
        Assert.Equal((34, 23), (half.Width, half.Height));
        Assert.Equal(inks.IccProfile.Length, half.IccProfile.Length);

        // Ink blends as an amount: a fit of flat ink keeps the amount (no gamma, which would shift it).
        using CmykImage flat = inks.Map(plane =>
        {
            var image = new Image(40, 40, PixelFormat.L8);
            image.GetPixelBytes().Fill(100);
            return image;
        });
        using CmykImage small = flat.Map(plane => plane.Fit(13, 13));
        Assert.All(small.ToInterleaved(), b => Assert.Equal(100, b));

        Assert.Throws<ArgumentNullException>(() => inks.Map(null!));
        Assert.Throws<InvalidOperationException>(() => inks.Map(plane => plane));
        Assert.Throws<InvalidOperationException>(() => inks.Map(plane => plane.Convert(PixelFormat.Rgb24)));
        int calls = 0;
        Assert.Throws<InvalidOperationException>(() => inks.Map(plane => plane.Fit(20 + calls++, 20 + calls)));
    }

    [Fact]
    public void A_separated_tiff_gives_its_inks()
    {
        byte[] samples = new byte[5 * 3 * 4];
        new Random(4).NextBytes(samples);
        byte[] tiff = TiffDecoderTests.Tiff(5, 3, bits: 8, samples: 4, photometric: 5, strips: [samples], rowsPerStrip: 3);
        using CmykImage inks = Image.DecodeCmyk(tiff)!;
        Assert.Equal(samples, inks.ToInterleaved());
        using Image decoded = Image.Decode(tiff);
        using Image srgb = inks.ToSrgb();
        using Image decodedRgb = decoded.Convert(PixelFormat.Rgb24);
        Assert.Equal(decodedRgb.GetPixelBytes().ToArray(), srgb.GetPixelBytes().ToArray());
    }

    [Fact]
    public void Disposing_returns_every_plane()
    {
        CmykImage inks = Image.DecodeCmyk(Sample("ycck"))!;
        inks.Dispose();
        Assert.Throws<ObjectDisposedException>(() => inks.Cyan.GetPixelBytes());
        Assert.Throws<ObjectDisposedException>(() => inks.Black.GetPixelBytes());
        Assert.Throws<ObjectDisposedException>(() => inks.ToInterleaved());
    }

    [Fact]
    public async Task The_inks_match_ImageMagick_s()
    {
        // ImageMagick writes a plain (Adobe-marked) CMYK JPEG and reads back its raw inks: ours must be the same.
        string magick = Oracle.Require("magick");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        using Image source = PngEncoderTests.Smooth(48, 32, PixelFormat.Rgb24, seed: 6);
        await File.WriteAllBytesAsync(folder.File("in.png"), PngEncoderTests.Save(source), TestContext.Current.CancellationToken);
        (int code, _, string errors) = await Oracle.RunAsync(magick, folder.File("in.png"), "-colorspace", "CMYK", "-quality", "95", folder.File("cmyk.jpg"));
        Assert.True(code == 0, errors);
        (code, byte[] raw, errors) = await Oracle.RunAsync(magick, folder.File("cmyk.jpg"), "-depth", "8", "cmyk:-");
        Assert.True(code == 0, errors);

        using CmykImage inks = Image.DecodeCmyk(await File.ReadAllBytesAsync(folder.File("cmyk.jpg"), TestContext.Current.CancellationToken))!;
        Assert.NotNull(inks);
        (int max, double mean) = Compare(inks.ToInterleaved(), raw);
        Assert.True(max <= 1 && mean < 0.1, $"largest {max}, mean {mean:0.###}");
    }

    // The file with the profile in APP2 chunks of at most 65,519 bytes, after SOI.
    private static byte[] WithIcc(byte[] jpeg, byte[] profile)
    {
        const int Chunk = 65519;
        int count = (profile.Length + Chunk - 1) / Chunk;
        var segments = new (int, byte[])[count];
        for (int i = 0; i < count; i++)
        {
            byte[] part = profile.AsSpan(i * Chunk, Math.Min(Chunk, profile.Length - (i * Chunk))).ToArray();
            segments[i] = (0xE2, [.. "ICC_PROFILE\0"u8, (byte)(i + 1), (byte)count, .. part]);
        }

        return WithSegments(jpeg, segments);
    }
}
