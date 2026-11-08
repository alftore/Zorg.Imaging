using System;
using System.Collections.Generic;
using Xunit;
using Zorg.Imaging.Metadata;
using Zorg.Imaging.Tests.Codecs.Jpeg;
using static Zorg.Imaging.Tests.Metadata.IccTestProfiles;

namespace Zorg.Imaging.Tests.Metadata;

public class IccLutTests
{
    public static TheoryData<TagType, bool, int, int> SrgbProfiles => new()
    {
        // Tag type, Lab PCS, grid points, largest difference allowed.
        { TagType.Lut16, false, 2, 1 },
        { TagType.Lut16, true, 17, 2 },
        { TagType.Lut8, true, 17, 4 },
        { TagType.AToB, false, 2, 1 },
        { TagType.AToB, true, 17, 2 },
    };

    [Theory]
    [MemberData(nameof(SrgbProfiles))]
    public void A_lookup_table_profile_for_srgb_converts_to_the_same_pixels(TagType type, bool lab, int points, int tolerance)
    {
        byte[] profile = SrgbLut(type, lab, points);
        IccProfileReader.Model model = IccProfileReader.Read(profile)!;
        Assert.NotNull(model.Lut);
        Assert.Equal(3, model.LutChannels);

        using Image photo = JpegEncoderTests.Photo(64, 48, seed: points);
        photo.ColorSpace = ColorSpace.FromIccProfile(profile);
        using Image converted = photo.ToSrgb();
        Assert.True(converted.ColorSpace.IsSrgb);
        (int max, double mean) = Difference(photo.GetPixelBytes(), converted.GetPixelBytes());
        Assert.True(max <= tolerance && mean < 0.3, $"largest {max}, mean {mean:0.###}");
    }

    [Fact]
    public void Sixteen_bit_and_alpha_images_convert_too()
    {
        byte[] profile = SrgbLut(TagType.AToB, lab: false, points: 2);
        using var wide = new Image(9, 7, PixelFormat.Rgba64) { ColorSpace = ColorSpace.FromIccProfile(profile) };
        var random = new Random(5);
        random.NextBytes(wide.GetPixelBytes());
        using Image converted = wide.ToSrgb();
        ReadOnlySpan<ushort> before = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(wide.GetPixelBytes());
        ReadOnlySpan<ushort> after = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(converted.GetPixelBytes());
        for (int i = 0; i < before.Length; i++)
        {
            Assert.True(Math.Abs(before[i] - after[i]) <= (i % 4 == 3 ? 0 : 160), $"sample {i}: {before[i]} became {after[i]}");
        }
    }

    [Fact]
    public void Encoders_convert_a_lookup_table_profile_when_asked()
    {
        using Image photo = JpegEncoderTests.Photo(32, 24);
        photo.ColorSpace = ColorSpace.FromIccProfile(SrgbLut(TagType.Lut16, lab: false, points: 2));
        byte[] jpeg = JpegEncoderTests.Save(photo, new JpegEncoderOptions { ColorSpaceHandling = ColorSpaceHandling.Srgb });
        using Image decoded = Image.Decode(jpeg);
        Assert.True(decoded.ColorSpace.IsSrgb);

        // Every colour of sRGB fits sRGB, so the lossless-only default converts it as well.
        byte[] byDefault = JpegEncoderTests.Save(photo);
        using Image again = Image.Decode(byDefault);
        Assert.True(again.ColorSpace.IsSrgb);
    }

    [Fact]
    public void A_cmyk_profile_is_read_with_four_inputs_and_isnt_applied_to_rgb_pixels()
    {
        byte[] profile = NaiveCmyk();
        IccProfileReader.Model model = IccProfileReader.Read(profile)!;
        Assert.Equal(4, model.LutChannels);

        // Paper white, full black, and pure cyan ink.
        Assert.Equal((1.0, 1.0, 1.0), Srgb(model.Lut!, [0, 0, 0, 0]), Close);
        Assert.Equal((0.0, 0.0, 0.0), Srgb(model.Lut!, [0, 0, 0, 1]), Close);
        Assert.Equal((0.0, 1.0, 1.0), Srgb(model.Lut!, [1, 0, 0, 0]), Close);

        using var rgb = new Image(4, 4, PixelFormat.Rgb24) { ColorSpace = ColorSpace.FromIccProfile(profile) };
        Assert.Throws<NotSupportedException>(() => rgb.ToSrgb().Dispose());
    }

    [Fact]
    public void Damaged_profiles_give_no_model_or_a_working_one_and_never_throw()
    {
        foreach (byte[] profile in (byte[][])[SrgbLut(TagType.Lut16, true, 5), SrgbLut(TagType.AToB, true, 5), SrgbLut(TagType.Lut8, true, 5), NaiveCmyk(TagType.AToB, 3)])
        {
            var random = new Random(profile.Length);
            for (int round = 0; round < 3000; round++)
            {
                byte[] copy = (byte[])profile.Clone();
                for (int f = 0; f < 1 + random.Next(3); f++)
                {
                    int at = 128 + random.Next(copy.Length - 128);
                    copy[at] = random.Next(2) == 0 ? (byte)random.Next(256) : (byte)(copy[at] ^ (1 << random.Next(8)));
                }

                IccProfileReader.Model? model = IccProfileReader.Read(copy);
                if (model?.Lut is { } lut)
                {
                    double[] device = new double[lut.Inputs];
                    Array.Fill(device, 0.5);
                    _ = lut.Evaluate(device);
                }
            }
        }
    }

    private static readonly IEqualityComparer<(double, double, double)> Close = new Tolerance();

    private static (double R, double G, double B) Srgb(IccLut lut, double[] device)
    {
        (double x, double y, double z) = lut.Evaluate(device);
        double[] linear = ColourMath.Apply(ColourMath.Invert(ColourMath.RgbToPcs(ColourMath.Chromaticities(1)!)), [x, y, z]);
        return (Encode(linear[0]), Encode(linear[1]), Encode(linear[2]));

        static double Encode(double v) => v <= 0.0031308 ? v * 12.92 : (1.055 * Math.Pow(Math.Max(v, 0), 1 / 2.4)) - 0.055;
    }

    private static (int Max, double Mean) Difference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int max = 0;
        long total = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]);
            max = Math.Max(max, d);
            total += d;
        }

        return (max, (double)total / a.Length);
    }

    private sealed class Tolerance : IEqualityComparer<(double, double, double)>
    {
        public bool Equals((double, double, double) x, (double, double, double) y) =>
            Math.Abs(x.Item1 - y.Item1) < 0.01 && Math.Abs(x.Item2 - y.Item2) < 0.01 && Math.Abs(x.Item3 - y.Item3) < 0.01;

        public int GetHashCode((double, double, double) obj) => 0;
    }
}
