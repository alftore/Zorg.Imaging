using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Codecs.Png;
using static Zorg.Imaging.Tests.Codecs.Png.PngTestFiles;

namespace Zorg.Imaging.Tests.Codecs.Png;

public class PngOptimiserTests
{
    public static TheoryData<string> Layouts() => [.. Cases.Select(c => c.Name)];

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Each_image_gets_the_smallest_layout_and_decodes_to_the_same_pixels(string name)
    {
        LayoutCase c = Cases.Single(c => c.Name == name);
        using Image image = c.Make();

        byte[] png = PngEncoderTests.Save(image, c.Options ?? new PngEncoderOptions());

        Assert.Equal((c.ColourType, c.Depth), (png[25], png[24]));
        string[] chunks = [.. Chunks(png).Select(k => k.Type)];
        Assert.Equal(c.ColourType == 3, chunks.Contains("PLTE"));
        Assert.Equal(c.Trns, chunks.Contains("tRNS"));
        Assert.Equal(Expected(image, c.Options?.CleanTransparentPixels ?? false), Rgba64(png));
    }

    [Fact]
    public void A_palette_lists_translucent_entries_first_so_tRNS_is_short()
    {
        using Image image = Make(100, 40, PixelFormat.Rgba32, (x, y) => x switch
        {
            < 2 => (10, 20, 30, 128),
            < 4 => (200, 0, 0, 0),
            _ => (x % 16 * 10, y % 4, 5, 255),
        });

        byte[] png = PngEncoderTests.Save(image);

        (int offset, _) = Chunks(png).Single(c => c.Type == "tRNS");
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset)));
        Assert.Equal(Expected(image, clean: false), Rgba64(png));
    }

    [Fact]
    public void A_colour_space_keeps_colour_as_colour_and_grey_as_grey()
    {
        using Image grey = Make(16, 16, PixelFormat.Rgb24, (x, y) => ((byte)(x * 16), (byte)(x * 16), (byte)(x * 16), 255));
        grey.ColorSpace = ColorSpace.DisplayP3;

        byte[] png = PngEncoderTests.Save(grey);

        Assert.True(png[25] is 2 or 3, $"colour type {png[25]}");
        using Image decoded = Image.Decode(png);
        Assert.Equal(ColorSpace.DisplayP3, decoded.ColorSpace);
    }

    [Fact]
    public void Off_the_file_has_the_image_layout()
    {
        using var image = new Image(8, 8, PixelFormat.Rgba32);

        byte[] png = PngEncoderTests.Save(image, new PngEncoderOptions { ReduceColorType = false });

        Assert.Equal((6, 8), ((int)png[25], (int)png[24]));
    }

    public static TheoryData<PixelFormat, PngFilter> FilterCases()
    {
        var data = new TheoryData<PixelFormat, PngFilter>();
        foreach (PngFilter filter in Enum.GetValues<PngFilter>())
        {
            foreach (PixelFormat format in new[] { PixelFormat.L8, PixelFormat.Rgb24, PixelFormat.Rgba32, PixelFormat.Rgb48 })
            {
                data.Add(format, filter);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FilterCases))]
    public void Every_filter_strategy_round_trips(PixelFormat format, PngFilter filter)
    {
        using Image image = PngEncoderTests.Smooth(53, 31, format, seed: (int)filter);

        byte[] png = PngEncoderTests.Save(image, new PngEncoderOptions { Filter = filter });

        Assert.Equal(Expected(image, clean: false), Rgba64(png));
        if (filter is >= PngFilter.None and <= PngFilter.Paeth)
        {
            Assert.All(FilterBytes(png, image), f => Assert.Equal((int)filter - 1, f));
        }
    }

    [Fact]
    public void Optimize_round_trips_and_is_no_larger_than_level_9()
    {
        foreach (PixelFormat format in new[] { PixelFormat.Rgb24, PixelFormat.Rgba32, PixelFormat.L8 })
        {
            using Image image = PngEncoderTests.Smooth(120, 90, format, seed: 11);

            byte[] optimized = PngEncoderTests.Save(image, new PngEncoderOptions { Optimize = true });
            byte[] level9 = PngEncoderTests.Save(image, new PngEncoderOptions { CompressionLevel = 9 });

            Assert.Equal(Expected(image, clean: false), Rgba64(optimized));
            Assert.True(optimized.Length <= level9.Length, $"{format}: {optimized.Length} vs {level9.Length}");
        }
    }

    [Fact]
    public void Images_of_many_bands_round_trip_and_are_the_same_on_any_number_of_cores()
    {
        // 1 MiB bands of 1201-byte rows: about 873 rows each, so four bands.
        using Image image = PngEncoderTests.Smooth(400, 3000, PixelFormat.Rgb24, seed: 5);

        byte[] one = PngEncoderTests.Save(image, new PngEncoderOptions { MaxDegreeOfParallelism = 1 });
        byte[] many = PngEncoderTests.Save(image, new PngEncoderOptions { MaxDegreeOfParallelism = 8 });

        Assert.Equal(one, many);
        Assert.Equal(Expected(image, clean: false), Rgba64(one));
    }

    [Fact]
    public async Task Async_writes_the_same_bytes_as_sync_across_bands()
    {
        using Image image = PngEncoderTests.Smooth(500, 2500, PixelFormat.Rgba32, seed: 6);
        using var async = new MemoryStream();

        await image.SaveAsPngAsync(async, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PngEncoderTests.Save(image), async.ToArray());
    }

    [Fact]
    public void Adler32_joined_from_pieces_matches_the_whole()
    {
        var random = new Random(3);
        byte[] data = new byte[20000];
        random.NextBytes(data);
        foreach (int split in new[] { 0, 1, 5552, 7000, 19999, 20000 })
        {
            uint joined = PngDeflate.CombineAdler(PngDeflate.Adler32(1, data.AsSpan(0, split)), PngDeflate.Adler32(1, data.AsSpan(split)), data.Length - split);
            Assert.Equal(PngDeflate.Adler32(1, data), joined);
        }

        Assert.Equal(0x11E60398u, PngDeflate.Adler32(1, "Wikipedia"u8));
    }

    [Fact]
    public void Random_images_round_trip_exactly()
    {
        var random = new Random(42);
        PixelFormat[] formats = [PixelFormat.L8, PixelFormat.Rgb24, PixelFormat.Rgba32, PixelFormat.Bgra32, PixelFormat.L16, PixelFormat.Rgb48, PixelFormat.Rgba64];
        for (int round = 0; round < 200; round++)
        {
            PixelFormat format = formats[random.Next(formats.Length)];
            int width = random.Next(1, 40);
            int height = random.Next(1, 20);
            using Image image = RandomImage(random, width, height, format);
            bool clean = random.Next(2) == 0;

            byte[] png = PngEncoderTests.Save(image, new PngEncoderOptions { CleanTransparentPixels = clean, Filter = (PngFilter)random.Next(10) });

            Assert.True(Expected(image, clean).SequenceEqual(Rgba64(png)), $"round {round}: {format} {width}x{height} clean {clean}, colour type {png[25]} depth {png[24]}");
        }
    }

    [Fact]
    public void Options_are_checked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PngEncoderOptions { Filter = (PngFilter)99 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PngEncoderOptions { MaxDegreeOfParallelism = -1 });
        var defaults = new PngEncoderOptions();
        Assert.Equal((true, false, PngFilter.Adaptive, false, 0), (defaults.ReduceColorType, defaults.CleanTransparentPixels, defaults.Filter, defaults.Optimize, defaults.MaxDegreeOfParallelism));
    }

    internal static Image Make(int width, int height, PixelFormat format, Func<int, int, (int R, int G, int B, int A)> pixel)
    {
        var image = new Image(width, height, format);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = image.GetRowBytes(y);
            for (int x = 0; x < width; x++)
            {
                (int ri, int gi, int bi, int ai) = pixel(x, y);
                (byte r, byte g, byte b, byte a) = ((byte)ri, (byte)gi, (byte)bi, (byte)ai);
                switch (format)
                {
                    case PixelFormat.L8:
                        row[x] = r;
                        break;
                    case PixelFormat.Rgb24:
                        row[x * 3] = r;
                        row[(x * 3) + 1] = g;
                        row[(x * 3) + 2] = b;
                        break;
                    case PixelFormat.Rgba32:
                        row[x * 4] = r;
                        row[(x * 4) + 1] = g;
                        row[(x * 4) + 2] = b;
                        row[(x * 4) + 3] = a;
                        break;
                    case PixelFormat.Bgra32:
                        row[x * 4] = b;
                        row[(x * 4) + 1] = g;
                        row[(x * 4) + 2] = r;
                        row[(x * 4) + 3] = a;
                        break;
                    default:
                    {
                        Span<ushort> samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(row);
                        int channels = format switch { PixelFormat.L16 => 1, PixelFormat.Rgb48 => 3, _ => 4 };
                        byte[] values = [r, g, b, a];
                        for (int ch = 0; ch < channels; ch++)
                        {
                            samples[(x * channels) + ch] = (ushort)(values[ch] * 257);
                        }

                        break;
                    }
                }
            }
        }

        return image;
    }

    /// <summary>The image as Rgba64 pixels, as an unreduced file of it decodes; transparent pixels black when cleaned.</summary>
    internal static byte[] Expected(Image image, bool clean)
    {
        byte[] png = PngEncoderTests.Save(image, new PngEncoderOptions { ReduceColorType = false, CleanTransparentPixels = clean });
        return Rgba64(png);
    }

    internal static byte[] Rgba64(byte[] png)
    {
        using Image decoded = Image.Decode(png, new DecodeOptions { PixelFormat = PixelFormat.Rgba64 });
        return Pixels(decoded);
    }

    private static Image RandomImage(Random random, int width, int height, PixelFormat format)
    {
        // A few kinds: any colour, few colours, grey, grey at a low depth; alpha opaque, binary or any; 16-bit exact or not.
        int kind = random.Next(4);
        int alphaKind = random.Next(3);
        (byte, byte, byte, byte)[] palette = [.. Enumerable.Range(0, random.Next(1, 300)).Select(_ => ((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)))];
        Image image = Make(width, height, format, (x, y) =>
        {
            (byte r, byte g, byte b, byte a) = palette[random.Next(palette.Length)];
            if (kind >= 2)
            {
                g = b = r;
            }

            if (kind == 3)
            {
                r = g = b = (byte)(r / 85 * 85);
            }

            if (kind == 0)
            {
                (r, g, b) = ((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            }

            a = alphaKind switch { 0 => 255, 1 => a < 128 ? (byte)0 : (byte)255, _ => a };
            return (r, g, b, a);
        });

        if (format is PixelFormat.L16 or PixelFormat.Rgb48 or PixelFormat.Rgba64 && random.Next(2) == 0)
        {
            Span<ushort> samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(image.GetPixelBytes());
            samples[random.Next(samples.Length)] ^= 1;
        }

        return image;
    }

    // The filter type byte of every row, read back from an uncompressed copy of the image data.
    private static int[] FilterBytes(byte[] png, Image image)
    {
        using var data = new MemoryStream();
        foreach ((int offset, string type) in Chunks(png).Where(c => c.Type == "IDAT"))
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            data.Write(png, offset + 8, length);
        }

        data.Position = 0;
        using var zlib = new System.IO.Compression.ZLibStream(data, System.IO.Compression.CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        byte[] bytes = raw.ToArray();
        int stride = bytes.Length / image.Height;
        return [.. Enumerable.Range(0, image.Height).Select(y => (int)bytes[y * stride])];
    }

    /// <summary>The image and options of a layout case, by name.</summary>
    internal static (Image Image, PngEncoderOptions Options) Build(string name)
    {
        LayoutCase c = Cases.Single(c => c.Name == name);
        return (c.Make(), c.Options ?? new PngEncoderOptions());
    }

    internal sealed record LayoutCase(string Name, Func<Image> Make, int ColourType, int Depth, bool Trns, PngEncoderOptions? Options = null);

    private static readonly LayoutCase[] Cases =
    [
        new("opaque RGBA becomes RGB", () => Make(300, 4, PixelFormat.Rgba32, (x, y) => ((byte)x, (byte)(x * 7), (byte)y, 255)), 2, 8, false),
        new("BGRA works the same", () => Make(300, 4, PixelFormat.Bgra32, (x, y) => ((byte)x, (byte)(x * 7), (byte)y, 255)), 2, 8, false),
        new("grey RGB becomes grey", () => Make(256, 2, PixelFormat.Rgb24, (x, y) => ((byte)x, (byte)x, (byte)x, 255)), 0, 8, false),
        new("black and white is 1 bit", () => Make(30, 5, PixelFormat.L8, (x, y) => ((byte)((x + y) % 2 * 255), 0, 0, 255)), 0, 1, false),
        new("four greys are 2 bits", () => Make(30, 5, PixelFormat.L8, (x, y) => ((byte)(x % 4 * 85), 0, 0, 255)), 0, 2, false),
        new("sixteen greys are 4 bits", () => Make(30, 5, PixelFormat.L8, (x, y) => ((byte)(x % 16 * 17), 0, 0, 255)), 0, 4, false),
        new("three grey levels of no step are a 2-bit palette", () => Make(30, 5, PixelFormat.L8, (x, y) => ((byte)(x % 3 * 50), 0, 0, 255)), 3, 2, false),
        new("three colours are a 2-bit palette", () => Make(30, 5, PixelFormat.Rgb24, (x, y) => ((byte)(x % 3 * 60), 9, 200, 255)), 3, 2, false),
        new("two hundred colours are an 8-bit palette", () => Make(200, 3, PixelFormat.Rgb24, (x, y) => ((byte)x, (byte)(x * 3), 7, 255)), 3, 8, false),
        new("three hundred colours stay RGB", () => Make(300, 3, PixelFormat.Rgb24, (x, y) => ((byte)x, (byte)(x * 3), (byte)(x / 256), 255)), 2, 8, false),
        new("one transparent colour becomes a tRNS key", () => Make(300, 4, PixelFormat.Rgba32, (x, y) => x % 5 == 0 ? (0, 0, 0, 0) : ((byte)(x | 1), (byte)(x * 7), (byte)y, 255)), 2, 8, true),
        new("a key colour also used opaque keeps alpha", () => Make(300, 4, PixelFormat.Rgba32, (x, y) => x % 5 == 0 ? (0, 0, 0, 0) : ((byte)x, (byte)(x * 7), (byte)y, 255)), 6, 8, false),
        new("varied hidden colours keep alpha", () => Make(300, 4, PixelFormat.Rgba32, (x, y) => x % 5 == 0 ? ((byte)x, 1, 2, 0) : ((byte)(x | 1), (byte)(x * 7), (byte)y, 255)), 6, 8, false),
        new("varied hidden colours cleaned become a key", () => Make(300, 4, PixelFormat.Rgba32, (x, y) => x % 5 == 0 ? ((byte)x, 1, 2, 0) : ((byte)(x | 1), (byte)(x * 7), (byte)y, 255)), 2, 8, true, new PngEncoderOptions { CleanTransparentPixels = true }),
        new("translucent grey is grey and alpha", () => Make(300, 4, PixelFormat.Rgba32, (x, y) => ((byte)x, (byte)x, (byte)x, (byte)(x + y))), 4, 8, false),
        new("grey with a transparent key", () => Make(300, 4, PixelFormat.Rgba32, (x, y) => x % 7 == 0 ? (3, 3, 3, 0) : ((byte)(x / 2 * 2), (byte)(x / 2 * 2), (byte)(x / 2 * 2), 255)), 0, 8, true),
        new("translucent few colours are a palette with tRNS", () => Make(40, 4, PixelFormat.Rgba32, (x, y) => ((byte)(x % 10), 50, 60, (byte)(x % 4 * 80))), 3, 8, true),
        new("16-bit exact 8-bit values become 8-bit", () => Make(300, 3, PixelFormat.Rgb48, (x, y) => ((byte)x, (byte)(x * 5), (byte)y, 255)), 2, 8, false),
        new("16-bit grey stays 16-bit grey", () => Wide(PixelFormat.Rgb48, grey: true, opaque: true), 0, 16, false),
        new("16-bit opaque RGBA becomes 16-bit RGB", () => Wide(PixelFormat.Rgba64, grey: false, opaque: true), 2, 16, false),
        new("16-bit translucent grey is 16-bit grey and alpha", () => Wide(PixelFormat.Rgba64, grey: true, opaque: false), 4, 16, false),
        new("16-bit colour and alpha stays", () => Wide(PixelFormat.Rgba64, grey: false, opaque: false), 6, 16, false),
        new("a single pixel is RGB, as a palette would cost more", () => Make(1, 1, PixelFormat.Rgb24, (x, y) => (1, 2, 3, 255)), 2, 8, false),
    ];

    private static Image Wide(PixelFormat format, bool grey, bool opaque)
    {
        var image = new Image(300, 3, format);
        int channels = format == PixelFormat.Rgba64 ? 4 : 3;
        for (int y = 0; y < 3; y++)
        {
            Span<ushort> samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(image.GetRowBytes(y));
            for (int x = 0; x < 300; x++)
            {
                ushort r = (ushort)((x * 211) + y);
                samples[x * channels] = r;
                samples[(x * channels) + 1] = grey ? r : (ushort)(x * 3);
                samples[(x * channels) + 2] = grey ? r : (ushort)7;
                if (channels == 4)
                {
                    samples[(x * channels) + 3] = opaque ? (ushort)0xFFFF : (ushort)(x * 100);
                }
            }
        }

        return image;
    }
}
