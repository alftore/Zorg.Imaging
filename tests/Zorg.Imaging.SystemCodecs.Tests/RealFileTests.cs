using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Zorg.Imaging.OsCodecs.Tests;

/// <summary>
/// Real AVIF and HEIC files (from <c>scripts/fetch-heif-samples.sh</c>) through the operating system. Skipped when the
/// files aren't fetched, and where the system can't decode the format; <c>ZORG_REQUIRE_HEIF_SAMPLES=1</c> makes a
/// missing file a failure.
/// </summary>
public sealed class RealFileTests
{
    public static TheoryData<string, int, int, bool> Samples => new()
    {
        { "libavif-kodim03_yuv420_8bpc.avif", 768, 512, false },
        { "libavif-sofa_grid1x5_420.avif", 1024, 770, false },
        { "libavif-paris_icc_exif_xmp.avif", 403, 302, false },
        { "libavif-abc_color_irot_alpha_irot.avif", 256, 512, true },
        { "libavif-clap_irot_imir_non_essential.avif", 10, 8, true },
        { "libheif-example.avif", 800, 533, false },
        { "libheif-example.heic", 1280, 854, false },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Identify_gives_the_size_as_shown(string name, int width, int height, bool alpha)
    {
        byte[] file = Load(name);
        ImageCodec codec = name.EndsWith(".heic", StringComparison.Ordinal) ? SystemCodecs.Heif : SystemCodecs.Avif;
        IdentifyResult result = Image.Identify(file, new IdentifyOptions { Codecs = SystemCodecs.All });
        if (!codec.Availability.IsAvailable)
        {
            Assert.Equal(IdentifyStatus.UnsupportedFormat, result.Status);
            return;
        }

        Assert.Equal(IdentifyStatus.Ok, result.Status);
        Assert.Equal((width, height), (result.Info!.Width, result.Info.Height));
        Assert.Equal(1, result.Info.Orientation);
        Assert.Equal(alpha, result.Info.HasAlpha);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Decoding_gives_the_picture_as_shown_or_says_why_not(string name, int width, int height, bool alpha)
    {
        byte[] file = Load(name);
        ImageCodec codec = name.EndsWith(".heic", StringComparison.Ordinal) ? SystemCodecs.Heif : SystemCodecs.Avif;
        if (!codec.Availability.IsAvailable)
        {
            Assert.Skip($"{codec.Name}: {codec.Availability.Reason}");
        }

        Image image;
        try
        {
            image = Image.Decode(file, new DecodeOptions { Codecs = SystemCodecs.All });
        }
        catch (UnsupportedImageFormatException e)
        {
            // A missing codec (HEVC on Windows) or an alpha plane the system can't read: said, not hidden.
            Assert.Skip(e.Message);
            return;
        }

        using (image)
        {
            Assert.Equal((width, height), (image.Width, image.Height));
            Assert.Equal(PixelFormat.Rgba32, image.PixelFormat);
            byte lowest = 255;
            Span<byte> pixels = image.GetPixelBytes();
            for (int i = 3; i < pixels.Length; i += 4)
            {
                lowest = Math.Min(lowest, pixels[i]);
            }

            Assert.Equal(alpha, lowest < 255);
            Assert.True(Varied(pixels), "The decoded picture is one flat colour.");
        }
    }

    [Fact]
    public void A_photo_decodes_in_every_pixel_format()
    {
        byte[] file = Load("libavif-kodim03_yuv420_8bpc.avif");
        if (!SystemCodecs.Avif.Availability.IsAvailable)
        {
            Assert.Skip(SystemCodecs.Avif.Availability.Reason!);
        }

        using Image rgba = Image.Decode(file, new DecodeOptions { Codecs = SystemCodecs.All, PixelFormat = PixelFormat.Rgba32 });
        using Image bgra = Image.Decode(file, new DecodeOptions { Codecs = SystemCodecs.All, PixelFormat = PixelFormat.Bgra32 });
        using Image deep = Image.Decode(file, new DecodeOptions { Codecs = SystemCodecs.All, PixelFormat = PixelFormat.Rgba64 });
        Span<byte> a = rgba.GetPixelBytes();
        Span<byte> b = bgra.GetPixelBytes();
        Span<byte> d = deep.GetPixelBytes();
        for (int i = 0; i < a.Length; i += 4 * 97)
        {
            Assert.Equal(a[i], b[i + 2]);
            Assert.Equal(a[i + 2], b[i]);
            int wide = d[(i * 2) + 1]; // the high byte of the 16-bit red
            Assert.InRange(wide, a[i] - 1, a[i] + 1);
        }
    }

    [Fact]
    public async Task The_pipeline_fits_a_grid_image()
    {
        byte[] file = Load("libavif-sofa_grid1x5_420.avif");
        if (!SystemCodecs.Avif.Availability.IsAvailable)
        {
            Assert.Skip(SystemCodecs.Avif.Availability.Reason!);
        }

        using Image thumbnail = await ImagePipeline.Load(file).WithDecodeOptions(new DecodeOptions { Codecs = SystemCodecs.All }).Fit(300, 300)
            .ToImageAsync(TestContext.Current.CancellationToken);
        Assert.Equal((300, 226), (thumbnail.Width, thumbnail.Height));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Shrink_on_load_matches_a_full_decode_resized(string name, int width, int height, bool alpha)
    {
        _ = (width, height, alpha);
        byte[] file = Load(name);
        var options = new DecodeOptions { Codecs = SystemCodecs.All };
        Image full;
        try
        {
            full = Image.Decode(file, options);
        }
        catch (UnsupportedImageFormatException e)
        {
            Assert.Skip(e.Message);
            return;
        }

        using (full)
        {
            int box = Math.Max(1, Math.Max(full.Width, full.Height) / 5);
            using Image fast = await ImagePipeline.Load(file).WithDecodeOptions(options).Fit(box, box).ToImageAsync(TestContext.Current.CancellationToken);
            using Image slow = full.Fit(box, box);
            Assert.Equal((slow.Width, slow.Height), (fast.Width, fast.Height));

            // The systems average in gamma-encoded values, as JPEG's shrink-on-load does, so the two differ a little.
            Span<byte> a = fast.GetPixelBytes();
            Span<byte> b = slow.GetPixelBytes();
            double squares = 0;
            for (int i = 0; i < a.Length; i++)
            {
                double d = a[i] - b[i];
                squares += d * d;
            }

            double psnr = 10 * Math.Log10(255.0 * 255 / (squares / a.Length));
            Assert.True(psnr > 30, $"{name}: shrink-on-load is {psnr:F1} dB from a full decode.");
        }
    }

    [Fact]
    public void Metadata_comes_through_and_is_stripped_unless_kept()
    {
        byte[] file = Load("libavif-paris_icc_exif_xmp.avif");
        if (!SystemCodecs.Avif.Availability.IsAvailable)
        {
            Assert.Skip(SystemCodecs.Avif.Availability.Reason!);
        }

        using Image photo = Image.Decode(file, new DecodeOptions { Codecs = SystemCodecs.All });
        Assert.Equal("Pixel 4a", photo.Metadata?.CameraModel);
        Assert.NotNull(photo.Metadata?.Location);

        using var kept = new MemoryStream();
        photo.SaveAsJpeg(kept, new JpegEncoderOptions { Metadata = MetadataOptions.Keep });
        using Image keptBack = Image.Decode(kept.ToArray());
        Assert.Equal("Pixel 4a", keptBack.Metadata?.CameraModel);

        using var stripped = new MemoryStream();
        photo.SaveAsJpeg(stripped);
        using Image strippedBack = Image.Decode(stripped.ToArray());
        Assert.Null(strippedBack.Metadata?.CameraModel);
    }

    private static bool Varied(Span<byte> pixels)
    {
        for (int i = 4; i < pixels.Length; i += 4)
        {
            if (pixels[i] != pixels[0] || pixels[i + 1] != pixels[1] || pixels[i + 2] != pixels[2])
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] Load(string name)
    {
        string? folder = Environment.GetEnvironmentVariable("ZORG_HEIF_SAMPLES");
        if (string.IsNullOrEmpty(folder))
        {
            string? at = AppContext.BaseDirectory;
            while (at is not null && !File.Exists(Path.Combine(at, "Zorg.Imaging.slnx")))
            {
                at = Path.GetDirectoryName(at);
            }

            folder = at is null ? string.Empty : Path.Combine(at, ".cache", "corpora", "heif");
        }

        string path = Path.Combine(folder, name);
        if (!File.Exists(path))
        {
            if (Environment.GetEnvironmentVariable("ZORG_REQUIRE_HEIF_SAMPLES") == "1")
            {
                Assert.Fail($"{name} isn't fetched (scripts/fetch-heif-samples.sh); ZORG_REQUIRE_HEIF_SAMPLES=1 makes this a failure.");
            }

            Assert.Skip($"{name} isn't fetched: run scripts/fetch-heif-samples.sh.");
        }

        return File.ReadAllBytes(path);
    }
}
