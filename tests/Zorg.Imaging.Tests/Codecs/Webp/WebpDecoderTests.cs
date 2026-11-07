using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Tests.Codecs.Png;

namespace Zorg.Imaging.Tests.Codecs.Webp;

/// <summary>
/// The WebP decoder against libwebp: the files in <c>Files</c> are made by <c>scripts/make-webp-fixtures.sh</c> with
/// <c>cwebp</c> and <c>webpmux</c>, and <c>dwebp</c>'s decodes of them (PAM, RGBA) are the references.
/// </summary>
public class WebpDecoderTests
{
    public static TheoryData<string> Lossless() =>
    [
        "lossless-z0", "lossless-z6", "lossless-z9", "lossless-alpha", "lossless-logo", "lossless-palette3", "lossless-palette12", "near-lossless",
    ];

    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Codecs", "Webp", "Files");

    internal static byte[] Sample(string name) => File.ReadAllBytes(Path.Combine(Folder, name + ".webp"));

    internal static (int Width, int Height, byte[] Rgba) Pam(string name)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Folder, name + ".pam"));
        int end = file.AsSpan().IndexOf("ENDHDR\n"u8) + 7;
        string header = Encoding.ASCII.GetString(file, 0, end);
        int Value(string key)
        {
            int at = header.IndexOf(key + " ", StringComparison.Ordinal) + key.Length + 1;
            return int.Parse(header[at..header.IndexOf('\n', at)], CultureInfo.InvariantCulture);
        }

        Assert.Equal(4, Value("DEPTH"));
        return (Value("WIDTH"), Value("HEIGHT"), file[end..]);
    }

    [Theory]
    [MemberData(nameof(Lossless))]
    public void Lossless_files_decode_exactly_as_libwebp_decodes_them(string name)
    {
        (int width, int height, byte[] expected) = Pam(name);
        using Image image = Image.Decode(Sample(name), new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        Assert.Equal((width, height), (image.Width, image.Height));
        Assert.Equal(expected, image.GetPixelBytes().ToArray());
    }

    [Fact]
    public void The_natural_layout_has_alpha_only_when_the_file_says_so()
    {
        using Image opaque = Image.Decode(Sample("lossless-z6"));
        using Image alpha = Image.Decode(Sample("lossless-alpha"));
        Assert.Equal(PixelFormat.Rgb24, opaque.PixelFormat);
        Assert.Equal(PixelFormat.Rgba32, alpha.PixelFormat);

        using Image bgra = Image.Decode(Sample("lossless-alpha"), new DecodeOptions { PixelFormat = PixelFormat.Bgra32 });
        using Image wide = Image.Decode(Sample("lossless-alpha"), new DecodeOptions { PixelFormat = PixelFormat.Rgba64 });
        Rgba32 p = alpha.GetRow<Rgba32>(5)[7];
        Assert.Equal(new Bgra32(p.B, p.G, p.R, p.A), bgra.GetRow<Bgra32>(5)[7]);
        Assert.Equal(new Rgba64((ushort)(p.R * 257), (ushort)(p.G * 257), (ushort)(p.B * 257), (ushort)(p.A * 257)), wide.GetRow<Rgba64>(5)[7]);
    }

    [Fact]
    public void An_animation_gives_its_first_frame_on_a_transparent_canvas()
    {
        // webpmux placed the 33 x 17 first frame at (6, 4) on a 40 x 24 canvas.
        (_, _, byte[] frame) = Pam("lossless-alpha");
        using Image image = Image.Decode(Sample("animated"), new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        Assert.Equal((40, 24), (image.Width, image.Height));
        for (int y = 0; y < 24; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                Rgba32 actual = image.GetRow<Rgba32>(y)[x];
                bool inside = x >= 6 && x < 39 && y >= 4 && y < 21;
                int at = inside ? ((((y - 4) * 33) + (x - 6)) * 4) : 0;
                Rgba32 expected = inside ? new Rgba32(frame[at], frame[at + 1], frame[at + 2], frame[at + 3]) : default;
                Assert.True(expected == actual, $"({x}, {y}): {actual}, expected {expected}");
            }
        }
    }

    [Theory]
    [InlineData("lossy")]
    [InlineData("lossy-alpha")]
    public void Lossy_files_decode_as_libwebp_decodes_them(string name)
    {
        (int width, int height, byte[] expected) = Pam(name);
        using Image image = Image.Decode(Sample(name), new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        Assert.Equal((width, height), (image.Width, image.Height));
        byte[] actual = image.GetPixelBytes().ToArray();
        int max = 0;
        int differing = 0;
        for (int i = 0; i < actual.Length; i++)
        {
            int d = Math.Abs(actual[i] - expected[i]);
            max = Math.Max(max, d);
            differing += d == 0 ? 0 : 1;
        }

        Assert.True(max == 0, $"{name}: {differing} of {actual.Length} samples differ, by up to {max}");
    }

    [Fact]
    public void An_icc_profile_becomes_the_colour_space_and_exif_orientation_is_applied()
    {
        byte[] profile = PngColourTests.FakeProfile(901, seed: 3);
        byte[] webp = Extended(Sample("lossless-z6"), flags: 0x20 | 0x08, ("ICCP", profile), ("VP8L", null), ("EXIF", PngTestFiles.ExifWithOrientation(6)));
        using Image image = Image.Decode(webp);
        Assert.Equal(profile, image.ColorSpace.IccProfile.ToArray());
        Assert.Equal((45, 67), (image.Width, image.Height));
        Assert.Equal(6, Image.Identify(webp).Info!.Orientation);

        using Image stored = Image.Decode(webp, new DecodeOptions { ApplyOrientation = false });
        using Image plain = Image.Decode(Sample("lossless-z6"));
        Assert.Equal(plain.GetPixelBytes().ToArray(), stored.GetPixelBytes().ToArray());

        byte[] huge = Extended(Sample("lossless-z6"), flags: 0x20, ("ICCP", new byte[(4 * 1024 * 1024) + 2]), ("VP8L", null));
        Assert.Throws<ImageLimitException>(() => Image.Decode(huge));
    }

    [Fact]
    public async Task Decoding_from_a_stream_that_cannot_seek_gives_the_same_pixels()
    {
        foreach (string name in Lossless())
        {
            byte[] webp = Sample(name);
            using Image fromBytes = Image.Decode(webp);
            using Image fromStream = await Image.DecodeAsync(new ForwardOnlyStream(webp), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(fromBytes.GetPixelBytes().ToArray(), fromStream.GetPixelBytes().ToArray());
        }
    }

    [Fact]
    public void A_canvas_over_MaxPixels_is_refused_before_anything_is_allocated()
    {
        byte[] webp = Extended(Sample("lossless-z6"), flags: 0, ("VP8L", null));
        webp[24] = 0xFF;
        webp[25] = 0xFF;
        webp[26] = 0xFF;
        webp[27] = 0xFF;
        webp[28] = 0xFF;
        webp[29] = 0xFF;
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ImageLimitException>(() => Image.Decode(webp));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_000_000);
    }

    [Fact]
    public void Every_truncation_throws_an_image_format_exception()
    {
        byte[] webp = Sample("lossless-alpha");
        for (int length = 0; length < webp.Length; length++)
        {
            Exception? error = Record.Exception(() => Image.Decode(webp[..length]).Dispose());
            Assert.True(error is ImageFormatException or UnsupportedImageFormatException, $"cut to {length}: {error}");
        }
    }

    [Fact]
    public void Random_damage_throws_only_our_exceptions_and_never_hangs()
    {
        var small = new DecodeOptions { Limits = new ImageLimits { MaxPixels = 1_000_000 } };
        foreach (string name in new[] { "lossless-z0", "lossless-z9", "lossless-alpha", "lossless-palette3", "lossless-palette12", "animated", "lossy", "lossy-alpha" })
        {
            byte[] webp = Sample(name);
            var random = new Random(name.Length);
            for (int round = 0; round < 1500; round++)
            {
                byte[] copy = (byte[])webp.Clone();
                int flips = 1 + random.Next(4);
                for (int f = 0; f < flips; f++)
                {
                    int at = 12 + random.Next(copy.Length - 12);
                    copy[at] = random.Next(3) == 0 ? (byte)random.Next(256) : (byte)(copy[at] ^ (1 << random.Next(8)));
                }

                long started = Stopwatch.GetTimestamp();
                Exception? error = Record.Exception(() => Image.Decode(copy, small).Dispose());
                TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
                Assert.True(error is null or ImageException, $"{name} round {round}: {error}");
                Assert.True(elapsed < TimeSpan.FromSeconds(2), $"{name} round {round} took {elapsed}");
            }
        }
    }

    [Fact]
    public async Task The_pipeline_reads_webp()
    {
        using Image thumb = await ImagePipeline.Load(Sample("lossless-z6")).Fit(20, 20).ToImageAsync(TestContext.Current.CancellationToken);
        Assert.Equal((20, 13), (thumb.Width, thumb.Height));
    }

    // A VP8X file around a simple file's VP8L chunk ("VP8L" with null data), with other chunks in the order given.
    internal static byte[] Extended(byte[] simple, byte flags, params (string FourCc, byte[]? Data)[] chunks)
    {
        (int width, int height, _) = Header(simple);
        var body = new List<byte>("WEBP"u8.ToArray());
        byte[] vp8x = [flags, 0, 0, 0, (byte)(width - 1), (byte)((width - 1) >> 8), (byte)((width - 1) >> 16), (byte)(height - 1), (byte)((height - 1) >> 8), (byte)((height - 1) >> 16)];
        AddChunk(body, "VP8X", vp8x);
        foreach ((string fourCc, byte[]? data) in chunks)
        {
            AddChunk(body, fourCc, data ?? simple[20..(20 + BinaryPrimitives.ReadInt32LittleEndian(simple.AsSpan(16)))]);
        }

        byte[] file = [.. "RIFF"u8, 0, 0, 0, 0, .. body];
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), body.Count);
        return file;
    }

    private static (int Width, int Height, bool Alpha) Header(byte[] simple)
    {
        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(simple.AsSpan(21));
        return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1, ((bits >> 28) & 1) != 0);
    }

    private static void AddChunk(List<byte> body, string fourCc, byte[] data)
    {
        body.AddRange(Encoding.ASCII.GetBytes(fourCc));
        byte[] size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, data.Length);
        body.AddRange(size);
        body.AddRange(data);
        if ((data.Length & 1) != 0)
        {
            body.Add(0);
        }
    }
}
