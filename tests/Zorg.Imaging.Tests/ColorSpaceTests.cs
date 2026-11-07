using System;
using Xunit;
using Zorg.Imaging.Tests.Codecs.Png;

namespace Zorg.Imaging.Tests;

public class ColorSpaceTests
{
    [Fact]
    public void Built_ins_have_their_code_points_and_FromCicp_finds_them()
    {
        (ColorSpace Space, int Primaries, int Transfer, bool Hdr)[] cases =
        [
            (ColorSpace.Srgb, 1, 13, false),
            (ColorSpace.DisplayP3, 12, 13, false),
            (ColorSpace.Rec2020, 9, 1, false),
            (ColorSpace.Rec2020Pq, 9, 16, true),
            (ColorSpace.Rec2020Hlg, 9, 18, true),
            (ColorSpace.LinearSrgb, 1, 8, false),
        ];
        foreach ((ColorSpace space, int primaries, int transfer, bool hdr) in cases)
        {
            Assert.Equal((primaries, transfer), (space.CicpPrimaries, space.CicpTransfer));
            Assert.Same(space, ColorSpace.FromCicp(primaries, transfer));
            Assert.Equal(hdr, space.IsHdr);
            Assert.True(space.IccProfile.IsEmpty);
            Assert.Equal(space.Name, space.ToString());
        }

        Assert.True(ColorSpace.Srgb.IsSrgb);
        Assert.False(ColorSpace.DisplayP3.IsSrgb);
    }

    [Fact]
    public void Other_code_points_make_a_new_colour_space_that_compares_by_value()
    {
        ColorSpace a = ColorSpace.FromCicp(9, 14);
        ColorSpace b = ColorSpace.FromCicp(9, 14);

        Assert.Equal("CICP 9/14", a.Name);
        Assert.NotSame(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a != ColorSpace.Rec2020);
        Assert.False(a.Equals(null));
        Assert.False(a == null);
        Assert.True((ColorSpace?)null == null);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(256, 1)]
    [InlineData(1, -1)]
    [InlineData(1, 256)]
    public void FromCicp_checks_its_range(int primaries, int transfer) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ColorSpace.FromCicp(primaries, transfer));

    [Fact]
    public void FromIccProfile_copies_the_profile_and_checks_it()
    {
        byte[] profile = PngColourTests.FakeProfile(400, seed: 5);

        ColorSpace space = ColorSpace.FromIccProfile(profile);
        profile[100] ^= 0xFF;

        Assert.Equal("ICC profile", space.Name);
        Assert.NotEqual(profile, space.IccProfile.ToArray());
        Assert.Equal(space, ColorSpace.FromIccProfile(space.IccProfile.Span));
        Assert.NotEqual(space, ColorSpace.FromIccProfile(profile));
        Assert.Throws<ArgumentException>(() => ColorSpace.FromIccProfile(new byte[127]));
        Assert.Throws<ArgumentException>(() => ColorSpace.FromIccProfile(new byte[400]));
        Assert.Throws<ArgumentException>(() => ColorSpace.FromIccProfile(PngColourTests.FakeProfile(ColorSpace.MaxIccProfileBytes + 1, seed: 1)));
    }

    [Fact]
    public void Images_start_as_sRGB_and_can_be_relabelled()
    {
        using var image = new Image(2, 2, PixelFormat.Rgba64);

        Assert.Same(ColorSpace.Srgb, image.ColorSpace);
        image.ColorSpace = ColorSpace.DisplayP3;
        Assert.Same(ColorSpace.DisplayP3, image.ColorSpace);
        Assert.Throws<ArgumentNullException>(() => image.ColorSpace = null!);
    }
}
