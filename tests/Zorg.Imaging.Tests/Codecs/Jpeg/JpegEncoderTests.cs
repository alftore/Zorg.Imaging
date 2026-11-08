using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Metadata;
using Zorg.Imaging.Tests.Codecs.Png;

namespace Zorg.Imaging.Tests.Codecs.Jpeg;

public class JpegEncoderTests
{
    internal static byte[] Save(Image image, JpegEncoderOptions? options = null)
    {
        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream, options);
        return stream.ToArray();
    }

    /// <summary>A photo-like picture: gradients, a hard edge, texture and a little noise.</summary>
    internal static Image Photo(int width, int height, PixelFormat format = PixelFormat.Rgb24, int seed = 1)
    {
        var random = new Random(seed);
        var image = new Image(width, height, PixelFormat.Rgba64);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba64> row = image.GetRow<Rgba64>(y);
            for (int x = 0; x < width; x++)
            {
                double r = x < width / 3 ? 0.85 : (double)x / width;
                double g = (double)y / height;
                double b = 0.5 + (0.3 * Math.Sin((x + (2 * y)) / 6.0)) + (random.NextDouble() * 0.05);
                double a = format is PixelFormat.Rgba32 or PixelFormat.Bgra32 or PixelFormat.Rgba64 ? Math.Clamp((x + y) / (double)(width + height) * 1.5, 0, 1) : 1;
                row[x] = new Rgba64((ushort)(Math.Clamp(r, 0, 1) * 65535), (ushort)(g * 65535), (ushort)(Math.Clamp(b, 0, 1) * 65535), (ushort)(a * 65535));
            }
        }

        if (format == PixelFormat.Rgba64)
        {
            return image;
        }

        using (image)
        {
            return Convert(image, format);
        }
    }

    internal static double Psnr(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        double total = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double d = a[i] - b[i];
            total += d * d;
        }

        double mse = total / a.Length;
        return mse == 0 ? 100 : 10 * Math.Log10(255.0 * 255 / mse);
    }

    [Theory]
    [InlineData(80, JpegChromaSubsampling.Yuv420, 30)]
    [InlineData(90, JpegChromaSubsampling.Yuv444, 36)]
    [InlineData(100, JpegChromaSubsampling.Yuv444, 44)]
    [InlineData(30, JpegChromaSubsampling.Yuv420, 26)]
    public void A_photo_round_trips_with_the_expected_fidelity(int quality, JpegChromaSubsampling subsampling, double minimumPsnr)
    {
        using Image photo = Photo(203, 131);
        byte[] jpeg = Save(photo, new JpegEncoderOptions { Quality = quality, ChromaSubsampling = subsampling });
        using Image decoded = Image.Decode(jpeg);
        Assert.Equal((PixelFormat.Rgb24, 203, 131), (decoded.PixelFormat, decoded.Width, decoded.Height));
        double psnr = Psnr(decoded.GetPixelBytes(), photo.GetPixelBytes());
        Assert.True(psnr >= minimumPsnr, $"PSNR {psnr:0.0} dB");
    }

    [Theory]
    [InlineData(80, JpegChromaSubsampling.Yuv420, 120, 90, PixelFormat.Rgb24)]
    [InlineData(80, JpegChromaSubsampling.Yuv444, 37, 23, PixelFormat.Rgb24)]
    [InlineData(100, JpegChromaSubsampling.Yuv420, 17, 9, PixelFormat.Rgb24)]
    [InlineData(5, JpegChromaSubsampling.Yuv420, 131, 77, PixelFormat.Rgb24)]
    [InlineData(60, JpegChromaSubsampling.Yuv420, 1, 1, PixelFormat.Rgb24)]
    [InlineData(80, JpegChromaSubsampling.Yuv420, 45, 33, PixelFormat.L8)]
    [InlineData(100, JpegChromaSubsampling.Yuv420, 8, 64, PixelFormat.L8)]
    public void Progressive_files_decode_to_exactly_the_baseline_pixels(int quality, JpegChromaSubsampling subsampling, int width, int height, PixelFormat format)
    {
        using Image photo = Photo(width, height, format, seed: quality);
        byte[] baseline = Save(photo, new JpegEncoderOptions { Quality = quality, ChromaSubsampling = subsampling });
        byte[] progressive = Save(photo, new JpegEncoderOptions { Quality = quality, ChromaSubsampling = subsampling, Progressive = true });

        // SOF2 marks a progressive frame.
        List<(int Offset, byte Marker)> segments = JpegTestFiles.Segments(progressive);
        Assert.Contains(segments, s => s.Marker == 0xC2);
        Assert.DoesNotContain(segments, s => s.Marker == 0xC0);

        using Image fromBaseline = Image.Decode(baseline);
        using Image fromProgressive = Image.Decode(progressive);
        Assert.Equal(fromBaseline.ToString(), fromProgressive.ToString());
        Assert.True(fromBaseline.GetPixelBytes().SequenceEqual(fromProgressive.GetPixelBytes()));
    }

    [Fact]
    public void A_progressive_flat_image_needs_end_of_band_runs_past_their_limit()
    {
        // 40,000 blocks with nothing in their AC bands: more than one run of at most 32,767 blocks codes them.
        using var flat = new Image(1600, 1600, PixelFormat.L8);
        flat.GetPixelBytes().Fill(77);
        byte[] progressive = Save(flat, new JpegEncoderOptions { Progressive = true });
        using Image decoded = Image.Decode(progressive);
        Assert.All(decoded.GetPixelBytes().ToArray(), sample => Assert.Equal(77, sample));
        // About a bit a block for the DC differences and another for the DC refinement: 40,000 blocks, about 10 KB.
        Assert.True(progressive.Length < 12_000, $"{progressive.Length} bytes");
    }

    [Fact]
    public void A_progressive_photo_is_no_larger_than_baseline()
    {
        using Image photo = Photo(640, 480);
        byte[] baseline = Save(photo, new JpegEncoderOptions());
        byte[] progressive = Save(photo, new JpegEncoderOptions { Progressive = true });
        Assert.True(progressive.Length <= baseline.Length, $"progressive {progressive.Length}, baseline {baseline.Length}");
    }

    [Fact]
    public void Higher_quality_makes_larger_files()
    {
        using Image photo = Photo(160, 120);
        int[] sizes = [.. new[] { 20, 50, 80, 95 }.Select(q => Save(photo, new JpegEncoderOptions { Quality = q }).Length)];
        Assert.True(sizes[0] < sizes[1] && sizes[1] < sizes[2] && sizes[2] < sizes[3], string.Join(", ", sizes));
        Assert.True(Save(photo, new JpegEncoderOptions { ChromaSubsampling = JpegChromaSubsampling.Yuv444 }).Length > sizes[2]);
    }

    [Fact]
    public void Grey_images_become_one_component_files()
    {
        using Image grey = Photo(50, 30, PixelFormat.L8);
        byte[] jpeg = Save(grey);
        (int sof, _) = JpegTestFiles.Segments(jpeg).First(s => s.Marker == 0xC0);
        Assert.Equal(1, jpeg[sof + 9]);
        using Image decoded = Image.Decode(jpeg);
        Assert.Equal(PixelFormat.L8, decoded.PixelFormat);
        Assert.True(Psnr(decoded.GetPixelBytes(), grey.GetPixelBytes()) > 35);

        using Image wide = Photo(50, 30, PixelFormat.L16);
        using Image fromWide = Image.Decode(Save(wide));
        Assert.Equal(PixelFormat.L8, fromWide.PixelFormat);
        Assert.True(Psnr(fromWide.GetPixelBytes(), decoded.GetPixelBytes()) > 40);
    }

    [Fact]
    public void Transparency_is_drawn_over_the_background()
    {
        using var image = new Image(32, 16, PixelFormat.Rgba32);
        for (int y = 0; y < 16; y++)
        {
            Span<Rgba32> row = image.GetRow<Rgba32>(y);
            for (int x = 0; x < 32; x++)
            {
                row[x] = x < 16 ? new Rgba32(0, 255, 0, 0) : new Rgba32(0, 0, 255, 255);
            }
        }

        using Image white = Image.Decode(Save(image, new JpegEncoderOptions { Quality = 95, ChromaSubsampling = JpegChromaSubsampling.Yuv444 }));
        using Image black = Image.Decode(Save(image, new JpegEncoderOptions { Quality = 95, ChromaSubsampling = JpegChromaSubsampling.Yuv444, Background = new Rgba32(0, 0, 0) }));
        Assert.True(Near(white.GetRow<Rgb24>(8)[2], 255, 255, 255), white.GetRow<Rgb24>(8)[2].ToString());
        Assert.True(Near(black.GetRow<Rgb24>(8)[2], 0, 0, 0), black.GetRow<Rgb24>(8)[2].ToString());
        Assert.True(Near(white.GetRow<Rgb24>(8)[29], 0, 0, 255), white.GetRow<Rgb24>(8)[29].ToString());

        // Bgra32 and Rgba64 hold the same picture.
        using Image bgra = Convert(image, PixelFormat.Bgra32);
        using Image wide = Convert(image, PixelFormat.Rgba64);
        byte[] expected = Save(image);
        Assert.Equal(expected, Save(bgra));
        Assert.Equal(expected, Save(wide));
    }

    [Fact]
    public void Output_is_the_same_every_time_and_on_any_number_of_threads()
    {
        using Image photo = Photo(700, 500);
        byte[] first = Save(photo);
        Assert.Equal(first, Save(photo));
        using Image strip = photo.Crop(0, 0, 700, 64);
        using Image fromStrip = Image.Decode(Save(strip));
        using Image whole = Image.Decode(first);
        using Image top = whole.Crop(0, 0, 700, 56);
        using Image stripTop = fromStrip.Crop(0, 0, 700, 56);
        Assert.Equal(top.GetPixelBytes().ToArray(), stripTop.GetPixelBytes().ToArray());
    }

    [Fact]
    public void Srgb_images_carry_no_profile_and_others_do()
    {
        using Image photo = Photo(40, 24);
        Assert.DoesNotContain(JpegTestFiles.Segments(Save(photo)), s => s.Marker == 0xE2);

        photo.ColorSpace = ColorSpace.DisplayP3;
        using Image p3 = Image.Decode(Save(photo));
        byte[] profile = p3.ColorSpace.IccProfile.ToArray();
        Assert.Equal("Display P3", Description(profile));
        Assert.Equal(profile, IccProfileWriter.For(ColorSpace.DisplayP3, "JPEG").ToArray());

        // A profile the image already has is written back byte for byte.
        byte[] own = PngColourTests.FakeProfile(70_000, seed: 2);
        photo.ColorSpace = ColorSpace.FromIccProfile(own);
        using Image again = Image.Decode(Save(photo));
        Assert.Equal(own, again.ColorSpace.IccProfile.ToArray());
    }

    [Fact]
    public void Hdr_and_unknown_colour_spaces_are_refused_rather_than_shifted()
    {
        using Image photo = Photo(16, 16);
        photo.ColorSpace = ColorSpace.Rec2020Pq;
        Assert.Contains("HDR", Assert.Throws<NotSupportedException>(() => Save(photo)).Message, StringComparison.Ordinal);
        photo.ColorSpace = ColorSpace.FromCicp(22, 13);
        Assert.Throws<NotSupportedException>(() => Save(photo));
    }

    [Fact]
    public void A_generated_srgb_profile_has_the_published_d50_colorants()
    {
        // IEC 61966-2-1 primaries adapted to D50 with Bradford, as in the ICC's own sRGB profiles.
        byte[] profile = IccProfileWriter.Write("sRGB", [0.3127, 0.3290, 0.64, 0.33, 0.30, 0.60, 0.15, 0.06], IccProfileWriter.Curve.Srgb);
        Assert.Equal(profile.Length, BinaryPrimitives.ReadInt32BigEndian(profile));
        Assert.Equal("acsp"u8.ToArray(), profile[36..40]);
        AssertXyz(profile, "rXYZ", 0.4360747, 0.2225045, 0.0139322);
        AssertXyz(profile, "gXYZ", 0.3850649, 0.7168786, 0.0971045);
        AssertXyz(profile, "bXYZ", 0.1430804, 0.0606169, 0.7141733);
        AssertXyz(profile, "wtpt", 0.9642, 1.0, 0.8249);
        Assert.Equal(ColorSpace.FromIccProfile(profile).IccProfile.ToArray(), profile);
    }

    [Fact]
    public void Sides_over_65535_pixels_are_refused()
    {
        using var image = new Image(65536, 1, PixelFormat.L8);
        Assert.Throws<NotSupportedException>(() => Save(image));
    }

    [Fact]
    public void Options_are_checked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegEncoderOptions { Quality = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegEncoderOptions { Quality = 101 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegEncoderOptions { ChromaSubsampling = (JpegChromaSubsampling)7 });
        Assert.Equal(80, JpegEncoderOptions.Default.Quality);
        using var image = new Image(2, 2, PixelFormat.L8);
        Assert.Throws<ArgumentNullException>(() => image.SaveAsJpeg(null!));
        image.Dispose();
        Assert.Throws<ObjectDisposedException>(() => image.SaveAsJpeg(new MemoryStream()));
    }

    [Fact]
    public async Task The_pipeline_saves_jpeg()
    {
        byte[] png = PngEncoderTests.Save(Photo(300, 200, PixelFormat.Rgba32));
        using var output = new MemoryStream();
        await ImagePipeline.Load(png).Fit(150, 150).SaveAsJpegAsync(output, new JpegEncoderOptions { Quality = 70 }, TestContext.Current.CancellationToken);
        using Image result = Image.Decode(output.ToArray());
        Assert.Equal((150, 100), (result.Width, result.Height));

        using var async = new MemoryStream();
        using Image photo = Photo(30, 20);
        await photo.SaveAsJpegAsync(async, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Save(photo), async.ToArray());
    }

    internal static string Description(byte[] profile)
    {
        (int offset, int size) = Tag(profile, "desc");
        int length = BinaryPrimitives.ReadInt32BigEndian(profile.AsSpan(offset + 20));
        int at = BinaryPrimitives.ReadInt32BigEndian(profile.AsSpan(offset + 24));
        Assert.True(at + length <= size);
        return System.Text.Encoding.BigEndianUnicode.GetString(profile, offset + at, length);
    }

    private static (int Offset, int Size) Tag(byte[] profile, string signature)
    {
        int count = BinaryPrimitives.ReadInt32BigEndian(profile.AsSpan(128));
        for (int i = 0; i < count; i++)
        {
            int entry = 132 + (12 * i);
            if (System.Text.Encoding.ASCII.GetString(profile, entry, 4) == signature)
            {
                return (BinaryPrimitives.ReadInt32BigEndian(profile.AsSpan(entry + 4)), BinaryPrimitives.ReadInt32BigEndian(profile.AsSpan(entry + 8)));
            }
        }

        throw new InvalidOperationException($"No {signature} tag.");
    }

    private static void AssertXyz(byte[] profile, string signature, double x, double y, double z)
    {
        (int offset, _) = Tag(profile, signature);
        double Read(int at) => BinaryPrimitives.ReadInt32BigEndian(profile.AsSpan(offset + at)) / 65536.0;
        Assert.True(Math.Abs(Read(8) - x) < 0.0005 && Math.Abs(Read(12) - y) < 0.0005 && Math.Abs(Read(16) - z) < 0.0005, $"{signature}: {Read(8)}, {Read(12)}, {Read(16)}");
    }

    private static bool Near(Rgb24 pixel, int r, int g, int b) => Math.Abs(pixel.R - r) <= 6 && Math.Abs(pixel.G - g) <= 6 && Math.Abs(pixel.B - b) <= 6;

    // The same picture in another layout, through PNG (which converts exactly).
    private static Image Convert(Image image, PixelFormat format)
    {
        byte[] png = PngEncoderTests.Save(image);
        return format switch
        {
            PixelFormat.Rgba64 or PixelFormat.Rgba32 or PixelFormat.Bgra32 => Image.Decode(png, new DecodeOptions { PixelFormat = format }),
            _ => Narrow(Image.Decode(png, new DecodeOptions { PixelFormat = PixelFormat.Rgba64 }), format),
        };
    }

    private static Image Narrow(Image wide, PixelFormat format)
    {
        using (wide)
        {
            var result = new Image(wide.Width, wide.Height, format);
            for (int y = 0; y < wide.Height; y++)
            {
                ReadOnlySpan<Rgba64> from = wide.GetRow<Rgba64>(y);
                Span<byte> to = result.GetRowBytes(y);
                Span<ushort> to16 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(to);
                for (int x = 0; x < wide.Width; x++)
                {
                    Rgba64 p = from[x];
                    switch (format)
                    {
                        case PixelFormat.Rgb24:
                            to[3 * x] = (byte)((p.R + 128) / 257);
                            to[(3 * x) + 1] = (byte)((p.G + 128) / 257);
                            to[(3 * x) + 2] = (byte)((p.B + 128) / 257);
                            break;
                        case PixelFormat.L8:
                            to[x] = (byte)((p.G + 128) / 257);
                            break;
                        case PixelFormat.L16:
                            to16[x] = p.G;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(format));
                    }
                }
            }

            return result;
        }
    }
}
