using System;
using System.Linq;
using System.Runtime.InteropServices;
using Xunit;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Tests.Processing;

public class ConvertTests
{
    private static readonly PixelFormat[] Formats =
        [PixelFormat.Rgba32, PixelFormat.Bgra32, PixelFormat.Rgb24, PixelFormat.L8, PixelFormat.L16, PixelFormat.Rgb48, PixelFormat.Rgba64];

    public static TheoryData<PixelFormat, PixelFormat> Pairs()
    {
        var data = new TheoryData<PixelFormat, PixelFormat>();
        foreach (PixelFormat from in Formats)
        {
            foreach (PixelFormat to in Formats)
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    /// <summary>An opaque image in <paramref name="format"/> with varied values, written through the model below.</summary>
    private static Image Sample(PixelFormat format, int width = 5, int height = 3, int seed = 1)
    {
        var random = new Random(seed);
        var image = new Image(width, height, format);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool wide = format is PixelFormat.L16 or PixelFormat.Rgb48 or PixelFormat.Rgba64;
                ushort Next() => wide ? (ushort)random.Next(65536) : (ushort)(random.Next(256) * 257);
                Put(image, x, y, (Next(), Next(), Next(), ushort.MaxValue));
            }
        }

        return image;
    }

    // An independent model of each layout: one pixel as 16-bit RGBA.
    private static (ushort R, ushort G, ushort B, ushort A) Get(Image image, int x, int y)
    {
        Span<byte> row = image.GetRowBytes(y);
        Span<ushort> wide = MemoryMarshal.Cast<byte, ushort>(row);
        return image.PixelFormat switch
        {
            PixelFormat.Rgba32 => (W(row[x * 4]), W(row[(x * 4) + 1]), W(row[(x * 4) + 2]), W(row[(x * 4) + 3])),
            PixelFormat.Bgra32 => (W(row[(x * 4) + 2]), W(row[(x * 4) + 1]), W(row[x * 4]), W(row[(x * 4) + 3])),
            PixelFormat.Rgb24 => (W(row[x * 3]), W(row[(x * 3) + 1]), W(row[(x * 3) + 2]), 65535),
            PixelFormat.L8 => (W(row[x]), W(row[x]), W(row[x]), 65535),
            PixelFormat.L16 => (wide[x], wide[x], wide[x], 65535),
            PixelFormat.Rgb48 => (wide[x * 3], wide[(x * 3) + 1], wide[(x * 3) + 2], 65535),
            _ => (wide[x * 4], wide[(x * 4) + 1], wide[(x * 4) + 2], wide[(x * 4) + 3]),
        };

        static ushort W(byte v) => (ushort)(v * 257);
    }

    private static void Put(Image image, int x, int y, (ushort R, ushort G, ushort B, ushort A) p)
    {
        Span<byte> row = image.GetRowBytes(y);
        Span<ushort> wide = MemoryMarshal.Cast<byte, ushort>(row);
        double luma = (0.2126 * p.R) + (0.7152 * p.G) + (0.0722 * p.B);
        switch (image.PixelFormat)
        {
            case PixelFormat.Rgba32:
                (row[x * 4], row[(x * 4) + 1], row[(x * 4) + 2], row[(x * 4) + 3]) = (N(p.R), N(p.G), N(p.B), N(p.A));
                break;
            case PixelFormat.Bgra32:
                (row[(x * 4) + 2], row[(x * 4) + 1], row[x * 4], row[(x * 4) + 3]) = (N(p.R), N(p.G), N(p.B), N(p.A));
                break;
            case PixelFormat.Rgb24:
                (row[x * 3], row[(x * 3) + 1], row[(x * 3) + 2]) = (N(p.R), N(p.G), N(p.B));
                break;
            case PixelFormat.L8:
                row[x] = N((ushort)Math.Round(luma));
                break;
            case PixelFormat.L16:
                wide[x] = (ushort)Math.Round(luma);
                break;
            case PixelFormat.Rgb48:
                (wide[x * 3], wide[(x * 3) + 1], wide[(x * 3) + 2]) = (p.R, p.G, p.B);
                break;
            default:
                (wide[x * 4], wide[(x * 4) + 1], wide[(x * 4) + 2], wide[(x * 4) + 3]) = (p.R, p.G, p.B, p.A);
                break;
        }

        static byte N(ushort v) => (byte)Math.Round(v / 257.0);
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Every_pair_matches_the_model(PixelFormat from, PixelFormat to)
    {
        using Image source = Sample(from, seed: (int)from);
        using Image converted = source.Convert(to);
        Assert.Equal((to, 5, 3), (converted.PixelFormat, converted.Width, converted.Height));

        using var expected = new Image(5, 3, to);
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 5; x++)
            {
                Put(expected, x, y, Get(source, x, y));
            }
        }

        // Grey from colour may differ by one step from the model's rounding: the library weighs in integers.
        byte[] want = expected.GetPixelBytes().ToArray(), got = converted.GetPixelBytes().ToArray();
        if (to is PixelFormat.L8 or PixelFormat.L16 && from is not (PixelFormat.L8 or PixelFormat.L16))
        {
            int step = to == PixelFormat.L16 ? 2 : 1;
            for (int i = 0; i < want.Length; i += step)
            {
                int a = step == 2 ? BitConverter.ToUInt16(want, i) : want[i];
                int b = step == 2 ? BitConverter.ToUInt16(got, i) : got[i];
                Assert.InRange(b - a, -1, 1);
            }
        }
        else
        {
            Assert.Equal(want, got);
        }
    }

    [Theory]
    [InlineData(PixelFormat.Rgba32)]
    [InlineData(PixelFormat.Bgra32)]
    [InlineData(PixelFormat.Rgb24)]
    [InlineData(PixelFormat.L8)]
    public void Eight_bit_values_survive_a_trip_through_sixteen_bits(PixelFormat format)
    {
        using Image source = Sample(format, 33, 7, seed: 5);
        PixelFormat wide = format == PixelFormat.L8 ? PixelFormat.L16 : format == PixelFormat.Rgb24 ? PixelFormat.Rgb48 : PixelFormat.Rgba64;
        using Image there = source.Convert(wide);
        using Image back = there.Convert(format);
        Assert.Equal(source.GetPixelBytes().ToArray(), back.GetPixelBytes().ToArray());
    }

    [Fact]
    public void Grey_stays_grey_exactly()
    {
        using var grey = new Image(256, 1, PixelFormat.Rgb24);
        Span<byte> row = grey.GetRowBytes(0);
        for (int x = 0; x < 256; x++)
        {
            row[x * 3] = row[(x * 3) + 1] = row[(x * 3) + 2] = (byte)x;
        }

        using Image l8 = grey.Convert(PixelFormat.L8);
        Assert.Equal(Enumerable.Range(0, 256).Select(v => (byte)v).ToArray(), l8.GetPixelBytes().ToArray());
    }

    [Fact]
    public void Dropping_alpha_needs_every_pixel_opaque()
    {
        using Image opaque = Sample(PixelFormat.Rgba32);
        using Image rgb = opaque.Convert(PixelFormat.Rgb24);
        Assert.Equal(PixelFormat.Rgb24, rgb.PixelFormat);

        opaque.GetRow<Rgba32>(2)[4] = new Rgba32(1, 2, 3, 254);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => opaque.Convert(PixelFormat.Rgb24));
        Assert.Contains("Flatten", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => opaque.Convert(PixelFormat.L8));
        using Image stillAlpha = opaque.Convert(PixelFormat.Rgba64); // keeping alpha is fine

        using Image wide = Sample(PixelFormat.Rgba64);
        wide.GetRow<Rgba64>(0)[0] = new Rgba64(0, 0, 0, 65534);
        Assert.Throws<InvalidOperationException>(() => wide.Convert(PixelFormat.Rgb48));
    }

    [Fact]
    public void Colour_space_is_kept_and_bad_calls_throw()
    {
        using Image source = Sample(PixelFormat.Rgb24);
        source.ColorSpace = ColorSpace.DisplayP3;
        using Image converted = source.Convert(PixelFormat.Rgba64);
        Assert.Same(ColorSpace.DisplayP3, converted.ColorSpace);
        using Image same = source.Convert(PixelFormat.Rgb24);
        Assert.NotSame(source, same);

        Assert.Throws<ArgumentOutOfRangeException>(() => source.Convert(PixelFormat.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Convert((PixelFormat)99));

        source.Dispose();
        Assert.Throws<ObjectDisposedException>(() => source.Convert(PixelFormat.Rgba32));
        Assert.Throws<ObjectDisposedException>(() => source.IsOpaque());
    }

    [Theory]
    [InlineData(PixelFormat.Rgba32, PixelFormat.Rgb24)]
    [InlineData(PixelFormat.Bgra32, PixelFormat.Rgb24)]
    [InlineData(PixelFormat.Rgb24, PixelFormat.Rgba32)]
    [InlineData(PixelFormat.Rgb24, PixelFormat.Bgra32)]
    [InlineData(PixelFormat.Rgba32, PixelFormat.Bgra32)]
    [InlineData(PixelFormat.Bgra32, PixelFormat.Rgba32)]
    public void Vector_paths_match_the_general_path(PixelFormat from, PixelFormat to)
    {
        for (int width = 1; width <= 40; width++)
        {
            using Image source = Sample(from, width, 2, seed: width);
            using Image fast = source.Convert(to);
            using var general = new Image(width, 2, to);
            PixelConverter.General(source, general);
            Assert.Equal(general.GetPixelBytes().ToArray(), fast.GetPixelBytes().ToArray());
        }
    }

    [Fact]
    public void Formats_without_alpha_are_opaque()
    {
        foreach (PixelFormat format in new[] { PixelFormat.Rgb24, PixelFormat.L8, PixelFormat.L16, PixelFormat.Rgb48 })
        {
            using var image = new Image(1, 1, format);
            Assert.True(image.IsOpaque());
        }
    }

    [Theory]
    [InlineData(PixelFormat.Rgba32)]
    [InlineData(PixelFormat.Bgra32)]
    [InlineData(PixelFormat.Rgba64)]
    public void One_translucent_pixel_anywhere_is_found(PixelFormat format)
    {
        for (int width = 1; width <= 40; width++)
        {
            using Image image = Sample(format, width, 3, seed: width);
            Assert.True(image.IsOpaque());
            SetAlpha(image, width - 1, 2);
            Assert.False(image.IsOpaque());
        }

        // Past the first scan chunk, and in it.
        using Image large = Sample(format, 300, 40);
        Assert.True(large.IsOpaque());
        SetAlpha(large, 299, 39);
        Assert.False(large.IsOpaque());
        using Image early = Sample(format, 300, 40);
        SetAlpha(early, 0, 0);
        Assert.False(early.IsOpaque());

        static void SetAlpha(Image image, int x, int y)
        {
            (ushort r, ushort g, ushort b, _) = Get(image, x, y);
            Put(image, x, y, (r, g, b, 65534 - 257)); // one 8-bit step below opaque
        }
    }
}
