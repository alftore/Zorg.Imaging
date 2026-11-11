using System;
using Xunit;
using Zorg.Imaging.OsCodecs.Heif;

namespace Zorg.Imaging.OsCodecs.Tests;

public sealed class HeifReaderTests
{
    private static readonly ImageLimits Limits = ImageLimits.Default;

    [Fact]
    public void A_single_HEIC_image()
    {
        HeifHeader header = HeifReader.Read(HeifFile.Heic(4032, 3024).Build(), Limits);
        Assert.Same(ImageFormat.Heif, header.Format);
        Assert.Equal((4032, 3024), (header.Width, header.Height));
        Assert.Equal(1, header.Orientation);
        Assert.False(header.HasAlpha);
        Assert.False(header.HasExif);
        Assert.Equal(8, header.BitDepth);
        Assert.Equal(0, header.Tiles);
        Assert.Null(header.ColorSpace);
    }

    [Fact]
    public void A_single_AVIF_image()
    {
        HeifHeader header = HeifReader.Read(HeifFile.Avif(64, 48).Build(), Limits);
        Assert.Same(ImageFormat.Avif, header.Format);
        Assert.Equal((64, 48), (header.Width, header.Height));
    }

    [Fact]
    public void A_grid_counts_its_tiles()
    {
        HeifHeader header = HeifReader.Read(HeifFile.Grid(4032, 3024, 48, 512, 512).Build(), Limits);
        Assert.Same(ImageFormat.Heif, header.Format);
        Assert.Equal((4032, 3024), (header.Width, header.Height));
        Assert.Equal(48, header.Tiles);

        Assert.Same(ImageFormat.Avif, HeifReader.Read(HeifFile.Grid(1024, 512, 2, 512, 512, "av01").Build(), Limits).Format);
    }

    [Fact]
    public void Too_many_tiles_or_tile_pixels_exceed_the_limits()
    {
        Assert.Throws<ImageLimitException>(() => HeifReader.Read(HeifFile.Grid(64, 64, HeifReader.MaxTiles + 1, 2, 2).Build(), Limits));
        var small = new ImageLimits { MaxPixels = 10_000 };
        Assert.Throws<ImageLimitException>(() => HeifReader.Read(HeifFile.Grid(100, 100, 4, 100, 100).Build(), small));
    }

    [Theory]
    [InlineData(0, -1, 1)]
    [InlineData(1, -1, 8)]
    [InlineData(2, -1, 3)]
    [InlineData(3, -1, 6)]
    [InlineData(0, 0, 2)]
    [InlineData(0, 1, 4)]
    [InlineData(1, 0, 7)]
    [InlineData(1, 1, 5)]
    [InlineData(2, 0, 4)]
    [InlineData(2, 1, 2)]
    [InlineData(3, 0, 5)]
    [InlineData(3, 1, 7)]
    public void Rotation_and_mirror_become_an_EXIF_orientation(int anticlockwise, int mirror, int orientation)
    {
        HeifFile file = HeifFile.Heic(40, 30).Property(1, HeifFile.Irot(anticlockwise));
        if (mirror >= 0)
        {
            file.Property(1, HeifFile.Imir(mirror));
        }

        Assert.Equal(orientation, HeifReader.Read(file.Build(), Limits).Orientation);
    }

    [Fact]
    public void Alpha_EXIF_bit_depth_and_colour_are_read()
    {
        byte[] file = HeifFile.Heic(40, 30)
            .Property(1, HeifFile.Pixi(10, 10, 10))
            .Property(1, HeifFile.Nclx(1, 13, 6))
            .WithAlpha()
            .WithExif()
            .Build();
        HeifHeader header = HeifReader.Read(file, Limits);
        Assert.True(header.HasAlpha);
        Assert.True(header.HasExif);
        Assert.Equal(10, header.BitDepth);
        Assert.NotNull(header.ColorSpace);
    }

    [Fact]
    public void A_damaged_file_is_a_format_error()
    {
        Assert.Throws<ImageFormatException>(() => HeifReader.Read(new HeifFile().Item(1, "hvc1").Build(), Limits)); // no ispe
        Assert.Throws<ImageFormatException>(() => HeifReader.Read(new HeifFile { WithHandler = false }.Item(1, "hvc1").Property(1, HeifFile.Ispe(2, 2)).Build(), Limits));
        HeifFile wrongPrimary = HeifFile.Heic(2, 2);
        wrongPrimary.Primary = 7;
        Assert.Throws<ImageFormatException>(() => HeifReader.Read(wrongPrimary.Build(), Limits));
        Assert.Throws<ImageFormatException>(() => HeifReader.Read(HeifFile.Heic(0, 10).Build(), Limits));
        Assert.Throws<ImageFormatException>(() => HeifReader.Read(HeifFile.Box("free", new byte[4]), Limits));
    }

    [Fact]
    public void An_unknown_coding_is_unsupported()
    {
        byte[] file = new HeifFile().Item(1, "jpeg").Property(1, HeifFile.Ispe(8, 8)).Build();
        Assert.Throws<UnsupportedImageFormatException>(() => HeifReader.Read(file, Limits));
    }

    [Fact]
    public void A_meta_box_past_the_header_budget_is_a_limit()
    {
        byte[] file = HeifFile.Heic(8, 8).Build();
        int metaEnd = file.AsSpan().IndexOf("mdat"u8) - 4;
        Assert.Throws<ImageLimitException>(() => HeifReader.Read(file.AsSpan(0, metaEnd - 10), Limits));
        Assert.Throws<ImageFormatException>(() => HeifReader.Read(file.AsSpan(0, 20), Limits));
    }

    [Fact]
    public void Every_truncation_and_damaged_byte_fails_cleanly()
    {
        byte[][] files =
        [
            HeifFile.Heic(4032, 3024).Property(1, HeifFile.Irot(1)).Property(1, HeifFile.Pixi(8, 8, 8)).WithAlpha().WithExif().Build(),
            HeifFile.Grid(1024, 768, 6, 512, 512).Build(),
            HeifFile.Avif(64, 64).Property(1, HeifFile.Nclx(9, 16, 9)).Build(),
        ];
        byte[] edges = [0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF];
        foreach (byte[] file in files)
        {
            for (int length = 0; length <= file.Length; length++)
            {
                ReadsOrFailsCleanly(file[..length]);
            }

            for (int at = 0; at < file.Length; at++)
            {
                foreach (byte value in edges)
                {
                    byte[] damaged = (byte[])file.Clone();
                    damaged[at] = value;
                    ReadsOrFailsCleanly(damaged);
                }
            }

            var random = new Random(file.Length);
            for (int round = 0; round < 2000; round++)
            {
                byte[] damaged = (byte[])file.Clone();
                for (int n = random.Next(1, 6); n > 0; n--)
                {
                    damaged[random.Next(damaged.Length)] = (byte)random.Next(256);
                }

                ReadsOrFailsCleanly(damaged);
            }
        }
    }

    private static void ReadsOrFailsCleanly(byte[] file)
    {
        try
        {
            HeifHeader header = HeifReader.Read(file, Limits);
            Assert.True(header.Width > 0 && header.Height > 0);
            Assert.InRange(header.Orientation, 1, 8);
        }
        catch (ImageException)
        {
        }
    }
}
