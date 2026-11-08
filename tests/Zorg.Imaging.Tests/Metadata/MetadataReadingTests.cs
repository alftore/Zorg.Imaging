using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;
using Zorg.Imaging.Metadata;
using Zorg.Imaging.Tests.Codecs.Jpeg;
using static Zorg.Imaging.Tests.Metadata.ExifTestFiles;

namespace Zorg.Imaging.Tests.Metadata;

public class MetadataReadingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_usual_facts_are_read_from_exif(bool little)
    {
        using Image photo = Decode(WithApp1(Plain(), [.. "Exif\0\0"u8, .. Located(little)]));
        ImageMetadata metadata = photo.Metadata!;
        Assert.Equal("Zorgcam", metadata.CameraMake);
        Assert.Equal("Z-1 Mark II", metadata.CameraModel);
        Assert.Equal("Zorg 24-70mm", metadata.LensModel);
        Assert.Equal("Alex Example", metadata.Artist);
        Assert.Equal("(c) 2026 Alex Example; Agency", metadata.Copyright);
        Assert.Equal("A quiet harbour", metadata.Description);
        Assert.Equal(new DateTime(2026, 11, 8, 10, 15, 30), metadata.DateTaken);
        Assert.Equal(DateTimeKind.Unspecified, metadata.DateTaken!.Value.Kind);
        Assert.Equal(TimeSpan.FromHours(-3.5), metadata.DateTakenOffset);
        Assert.Equal(6, metadata.Orientation);
        GeoLocation where = metadata.Location!;
        Assert.Equal(59.0 + (54.0 / 60) + (30.0 / 3600), where.Latitude, 9);
        Assert.Equal(-(10.0 + (45.0 / 60)), where.Longitude, 9);
        Assert.Equal(-12.5, where.Altitude);
        Assert.Equal("59.908333, -10.75", where.ToString());
        Assert.True(metadata.Iptc.IsEmpty && metadata.Xmp.IsEmpty);
        Assert.Empty(metadata.Keywords);
        Assert.Same(metadata, photo.Metadata);

        // Operations carry it.
        using Image turned = photo.Rotate(90);
        Assert.Equal("Zorgcam", turned.Metadata!.CameraMake);
    }

    [Fact]
    public void A_location_needs_both_latitude_and_longitude()
    {
        // The Camera fixture has a latitude only.
        using Image photo = Decode(WithApp1(Plain(), [.. "Exif\0\0"u8, .. Camera(true)]));
        Assert.Null(photo.Metadata!.Location);
        Assert.Equal(new DateTime(2026, 11, 8, 10, 0, 0), photo.Metadata.DateTaken);
        Assert.Equal(TimeSpan.FromHours(1), photo.Metadata.DateTakenOffset);
    }

    [Fact]
    public void Iptc_is_read_from_a_photoshop_segment_and_fills_what_exif_lacks()
    {
        using Image photo = Decode(WithIptc(Plain(), Records(utf8: true)));
        ImageMetadata metadata = photo.Metadata!;
        Assert.Equal("Harbour at dawn", metadata.Title);
        Assert.Equal(["harbour", "dawn", "Bjørnøya"], metadata.Keywords);
        Assert.Equal("Bjørn Fotograf; Second Shooter", metadata.Artist);
        Assert.Equal("© 2026 Bjørn Fotograf", metadata.Copyright);
        Assert.Equal("Boats leaving at first light", metadata.Description);
        Assert.Equal(new DateTime(2026, 11, 8, 6, 45, 0), metadata.DateTaken);
        Assert.Equal(TimeSpan.FromHours(1), metadata.DateTakenOffset);
        Assert.Null(metadata.CameraMake);
        Assert.False(metadata.Iptc.IsEmpty);

        // EXIF wins where it has a value.
        using Image both = Decode(WithIptc(WithApp1(Plain(), [.. "Exif\0\0"u8, .. Camera(true)]), Records(utf8: true)));
        Assert.Equal("Alex Example", both.Metadata!.Artist);
        Assert.Equal("Harbour at dawn", both.Metadata.Title);
    }

    [Fact]
    public void Iptc_without_a_character_set_is_read_as_latin_1()
    {
        using Image photo = Decode(WithIptc(Plain(), Records(utf8: false)));
        Assert.Equal("Bjørn Fotograf; Second Shooter", photo.Metadata!.Artist);
    }

    [Fact]
    public void Iptc_is_written_back_to_jpeg_only_as_the_options_keep_it()
    {
        using Image photo = Decode(WithIptc(Plain(), Records(utf8: true)));

        ImageMetadata kept = Reread(photo, MetadataOptions.Keep)!;
        Assert.Equal("Harbour at dawn", kept.Title);
        Assert.Contains("Oslo", Datasets(kept, 90));
        Assert.Equal(new DateTime(2026, 11, 8, 6, 45, 0), kept.DateTaken);

        ImageMetadata noPlace = Reread(photo, MetadataOptions.KeepNonPersonal)!;
        Assert.Empty(Datasets(noPlace, 90));
        Assert.Empty(Datasets(noPlace, 118));
        Assert.Equal("Bjørn Fotograf; Second Shooter", noPlace.Artist);
        Assert.Equal(["harbour", "dawn", "Bjørnøya"], noPlace.Keywords);

        ImageMetadata noDates = Reread(photo, MetadataOptions.Keep.Without(ExifGroup.Dates))!;
        Assert.Null(noDates.DateTaken);
        Assert.Contains("Oslo", Datasets(noDates, 90));

        ImageMetadata owner = Reread(photo, MetadataOptions.CopyrightOnly)!;
        Assert.Equal("© 2026 Bjørn Fotograf", owner.Copyright);
        Assert.Equal("Bjørn Fotograf; Second Shooter", owner.Artist);
        Assert.Null(owner.Title);
        Assert.Empty(owner.Keywords);

        Assert.Null(Reread(photo, MetadataOptions.Strip));

        // PNG and WebP have no standard place for IPTC.
        using var png = new MemoryStream();
        photo.SaveAsPng(png, new PngEncoderOptions { Metadata = MetadataOptions.Keep });
        using Image fromPng = Image.Decode(png.ToArray());
        Assert.True(fromPng.Metadata is null || fromPng.Metadata.Iptc.IsEmpty);
    }

    [Fact]
    public void Damaged_iptc_and_exif_give_no_facts_and_no_errors()
    {
        byte[] records = Records(utf8: true);
        for (int cut = 0; cut < records.Length; cut += 3)
        {
            byte[] damaged = (byte[])records.Clone();
            damaged[cut] ^= 0xA5;
            using Image photo = Decode(WithIptc(Plain(), damaged[..(records.Length - (cut % 7))]));
            _ = photo.Metadata?.Keywords;
        }

        byte[] exif = Located(true);
        for (int at = 0; at < exif.Length; at++)
        {
            byte[] damaged = (byte[])exif.Clone();
            damaged[at] = 0xFF;
            using Image photo = Decode(WithApp1(Plain(), [.. "Exif\0\0"u8, .. damaged]));
            ImageMetadata? metadata = photo.Metadata;
            _ = (metadata?.Location, metadata?.DateTaken, metadata?.CameraMake);
        }
    }

    [Fact]
    public void Images_made_in_code_have_no_metadata()
    {
        using var image = new Image(4, 4, PixelFormat.Rgb24);
        Assert.Null(image.Metadata);
        using Image plain = Decode(Plain());
        Assert.Null(plain.Metadata);
    }

    [Fact]
    public void A_location_is_checked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoLocation(91, 0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoLocation(0, -181, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoLocation(double.NaN, 0, null));
        Assert.Equal("-33.8, 151.2", new GeoLocation(-33.8, 151.2, 3).ToString());
    }

    /// <summary>A small JPEG with EXIF (a full GPS position) and IPTC, for the fuzz tests.</summary>
    internal static byte[] Rich() => WithIptc(WithApp1(Plain(), [.. "Exif\0\0"u8, .. Located(true)]), Records(utf8: true));

    private static byte[] Plain() => JpegEncoderTests.Save(JpegEncoderTests.Photo(16, 16));

    private static Image Decode(byte[] jpeg) => Image.Decode(jpeg, new DecodeOptions { ApplyOrientation = false });

    private static ImageMetadata? Reread(Image image, MetadataOptions options)
    {
        using var output = new MemoryStream();
        image.SaveAsJpeg(output, new JpegEncoderOptions { Metadata = options });
        using Image again = Image.Decode(output.ToArray());
        return again.Metadata;
    }

    private static List<string> Datasets(ImageMetadata metadata, byte number) => Iptc.Texts(Iptc.Read(metadata.Iptc.Span), number);

    // EXIF with a full GPS position (59°54'30" N, 10°45' W, 12.5 m below sea level), a description, two copyright
    // parts and an offset date.
    private static byte[] Located(bool little)
    {
        var b = new Builder(little);
        List<Tag> main =
        [
            b.Ascii(0x010E, "A quiet harbour"),
            b.Ascii(0x010F, "Zorgcam"),
            b.Ascii(0x0110, "Z-1 Mark II"),
            b.Short(0x0112, 6),
            b.Ascii(0x013B, "Alex Example"),
            new(0x8298, 2, 32, [.. Encoding.ASCII.GetBytes("(c) 2026 Alex Example\0Agency"), 0, 0, 0]),
        ];
        List<Tag> exif =
        [
            b.Ascii(0x9003, "2026:11:08 10:15:30"),
            b.Ascii(0x9011, "-03:30"),
            b.Ascii(0xA434, "Zorg 24-70mm"),
        ];
        List<Tag> gps =
        [
            b.Ascii(0x0001, "N"),
            b.Rationals(0x0002, (59, 1), (54, 1), (3000, 100)),
            b.Ascii(0x0003, "W"),
            b.Rationals(0x0004, (10, 1), (45, 1), (0, 1)),
            new(0x0005, 1, 1, [1]),
            b.Rational(0x0006, 25, 2),
        ];
        return b.Build(main, exif, gps, thumbnail: false);
    }

    // IIM records: character set, version, title, three keywords, two by-lines, city, contact, date and time created,
    // copyright and caption.
    private static byte[] Records(bool utf8)
    {
        Encoding text = utf8 ? Encoding.UTF8 : Encoding.Latin1;
        var data = new List<byte>();
        void Add(byte record, byte number, byte[] value)
        {
            data.AddRange([0x1C, record, number, (byte)(value.Length >> 8), (byte)value.Length]);
            data.AddRange(value);
        }

        if (utf8)
        {
            Add(1, 90, [0x1B, 0x25, 0x47]);
        }

        Add(2, 0, [0, 4]);
        Add(2, 5, text.GetBytes("Harbour at dawn"));
        Add(2, 25, text.GetBytes("harbour"));
        Add(2, 25, text.GetBytes("dawn"));
        Add(2, 25, text.GetBytes("Bjørnøya"));
        Add(2, 80, text.GetBytes("Bjørn Fotograf"));
        Add(2, 80, text.GetBytes("Second Shooter"));
        Add(2, 90, text.GetBytes("Oslo"));
        Add(2, 118, text.GetBytes("bjorn@example.com"));
        Add(2, 55, text.GetBytes("20261108"));
        Add(2, 60, text.GetBytes("064500+0100"));
        Add(2, 116, text.GetBytes("© 2026 Bjørn Fotograf"));
        Add(2, 120, text.GetBytes("Boats leaving at first light"));
        return [.. data];
    }

    // A JPEG with a Photoshop APP13 segment holding the records as resource 0x0404, after another resource first.
    private static byte[] WithIptc(byte[] jpeg, byte[] records)
    {
        byte[] other = [.. "8BIM"u8, 0x03, 0xED, 3, (byte)'a', (byte)'b', (byte)'c', 0, 0, 0, 2, 9, 9];
        byte[] iptc = [.. "8BIM"u8, 0x04, 0x04, 0, 0, (byte)(records.Length >> 24), (byte)(records.Length >> 16), (byte)(records.Length >> 8), (byte)records.Length, .. records, .. (records.Length % 2 == 1 ? new byte[] { 0 } : [])];
        byte[] payload = [.. "Photoshop 3.0\0"u8, .. other, .. iptc];
        int length = payload.Length + 2;
        return [.. jpeg[..2], 0xFF, 0xED, (byte)(length >> 8), (byte)length, .. payload, .. jpeg[2..]];
    }
}
