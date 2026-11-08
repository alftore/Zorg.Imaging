using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Metadata;
using Zorg.Imaging.Tests.Codecs.Jpeg;
using static Zorg.Imaging.Tests.Metadata.ExifTestFiles;

namespace Zorg.Imaging.Tests.Metadata;

public class MetadataTests
{
    private static readonly byte[] XmpPacket = Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"/></x:xmpmeta>");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Keep_rebuilds_exif_true_to_the_saved_image(bool little)
    {
        byte[] rebuilt = ExifWriter.Rewrite(Camera(little), 300, 200, resetOrientation: true, MetadataOptions.Keep)!;
        (Dictionary<ushort, Tag> main, Dictionary<ushort, Tag> exif, Dictionary<ushort, Tag> gps, bool ifd1) = Read(rebuilt);
        Assert.Equal(little ? (byte)'I' : (byte)'M', rebuilt[0]);
        Assert.False(ifd1);
        Assert.Equal(1u, Value(main[0x0112], little));
        Assert.Equal("Zorgcam", Text(main[0x010F]));
        Assert.Equal("Alex Example", Text(main[0x013B]));
        Assert.Equal(300u, Value(exif[0xA002], little));
        Assert.Equal(200u, Value(exif[0xA003], little));
        Assert.False(exif.ContainsKey(0x927C));
        Assert.Equal("Zorg 24-70mm", Text(exif[0xA434]));
        Assert.Equal(Camera(little).AsSpan().IndexOf("2026:11:08 10:00:00"u8) >= 0, Text(exif[0x9003]) == "2026:11:08 10:00:00");
        Assert.Equal(16, exif[0x829A].Value.Length > 0 ? 16 : 0);
        Assert.Equal("N", Text(gps[0x0001]));
        Assert.Equal(24, gps[0x0002].Value.Length);
    }

    [Fact]
    public void Orientation_is_kept_when_the_pixels_were_not_turned()
    {
        byte[] rebuilt = ExifWriter.Rewrite(Camera(true, orientation: 8), 10, 10, resetOrientation: false, MetadataOptions.Keep)!;
        Assert.Equal(8u, Value(Read(rebuilt).Main[0x0112], true));
    }

    [Fact]
    public void Groups_are_left_out()
    {
        byte[] noGps = ExifWriter.Rewrite(Camera(true), 1, 1, true, MetadataOptions.Keep.Without(ExifGroup.Location))!;
        Assert.Empty(Read(noGps).Gps);
        Assert.False(Read(noGps).Main.ContainsKey(0x8825));

        var noCamera = Read(ExifWriter.Rewrite(Camera(false), 1, 1, true, MetadataOptions.Keep.Without(ExifGroup.Camera))!);
        Assert.False(noCamera.Main.ContainsKey(0x010F) || noCamera.Main.ContainsKey(0x0110));
        Assert.False(noCamera.Exif.ContainsKey(0xA431) || noCamera.Exif.ContainsKey(0xA434));
        Assert.True(noCamera.Main.ContainsKey(0x8298));

        var noDates = Read(ExifWriter.Rewrite(Camera(true), 1, 1, true, MetadataOptions.Keep.Without(ExifGroup.Dates))!);
        Assert.False(noDates.Main.ContainsKey(0x0132) || noDates.Exif.ContainsKey(0x9003) || noDates.Exif.ContainsKey(0x9011) || noDates.Gps.ContainsKey(0x001D));
        Assert.True(noDates.Gps.ContainsKey(0x0002));

        var copyright = Read(ExifWriter.Rewrite(Camera(true), 1, 1, true, MetadataOptions.CopyrightOnly)!);
        Assert.Equal(new[] { (ushort)0x013B, (ushort)0x8298 }, new SortedSet<ushort>(copyright.Main.Keys));
        Assert.Empty(copyright.Exif);
        Assert.Empty(copyright.Gps);
    }

    [Fact]
    public void Keep_non_personal_drops_location_and_device_but_keeps_the_photography()
    {
        var kept = Read(ExifWriter.Rewrite(Camera(true), 1, 1, true, MetadataOptions.KeepNonPersonal)!);
        Assert.Empty(kept.Gps);
        Assert.False(kept.Main.ContainsKey(0x010F) || kept.Main.ContainsKey(0x0110));
        Assert.False(kept.Exif.ContainsKey(0xA431) || kept.Exif.ContainsKey(0xA434) || kept.Exif.ContainsKey(0x927C));
        Assert.True(kept.Exif.ContainsKey(0x829A));
        Assert.True(kept.Exif.ContainsKey(0x9003));
        Assert.True(kept.Main.ContainsKey(0x8298));
        Assert.False(MetadataOptions.KeepNonPersonal.KeepsXmp);
        Assert.Equal("Keep without Location, Camera", MetadataOptions.KeepNonPersonal.ToString());
    }

    [Fact]
    public void Options_say_what_they_keep()
    {
        Assert.Equal("Strip", MetadataOptions.Strip.ToString());
        Assert.Equal("Keep without Location, Camera", MetadataOptions.Keep.Without(ExifGroup.Location).Without(ExifGroup.Camera).ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => MetadataOptions.Keep.Without((ExifGroup)8));
        Assert.Throws<ArgumentNullException>(() => new JpegEncoderOptions { Metadata = null! });
    }

    [Fact]
    public void Damaged_exif_never_throws()
    {
        byte[] exif = Camera(true);
        var random = new Random(3);
        for (int round = 0; round < 20_000; round++)
        {
            byte[] copy = (byte[])exif.Clone();
            for (int f = random.Next(1, 6); f > 0; f--)
            {
                copy[random.Next(copy.Length)] = (byte)random.Next(256);
            }

            byte[]? rebuilt = ExifWriter.Rewrite(copy.AsSpan(0, random.Next(copy.Length + 1)), 5, 5, true, MetadataOptions.Keep);
            if (rebuilt is not null)
            {
                _ = ExifWriter.Rewrite(rebuilt, 5, 5, false, MetadataOptions.Keep);
            }
        }
    }

    [Fact]
    public void A_jpeg_photo_keeps_its_metadata_through_every_encoder_and_strips_by_default()
    {
        using Image photo = JpegEncoderTests.Photo(48, 32, PixelFormat.Rgb24, seed: 1);
        byte[] jpeg = Save(photo, image => s => image.SaveAsJpeg(s));
        jpeg = WithApp1(WithApp1(jpeg, [.. "http://ns.adobe.com/xap/1.0/\0"u8, .. XmpPacket]), [.. "Exif\0\0"u8, .. Camera(true, orientation: 6)]);

        using Image decoded = Image.Decode(jpeg);
        Assert.Equal((32, 48), (decoded.Width, decoded.Height));

        var keep = MetadataOptions.Keep;
        foreach (byte[] file in new[]
        {
            Save(decoded, image => s => image.SaveAsJpeg(s, new JpegEncoderOptions { Metadata = keep })),
            Save(decoded, image => s => image.SaveAsPng(s, new PngEncoderOptions { Metadata = keep })),
            Save(decoded, image => s => image.SaveAsWebp(s, new WebpEncoderOptions { Metadata = keep, Lossless = true })),
            Save(decoded, image => s => image.SaveAsWebp(s, new WebpEncoderOptions { Metadata = keep })),
        })
        {
            using Image again = Image.Decode(file);
            Assert.Equal((32, 48), (again.Width, again.Height));
            ImageMetadata metadata = again.Metadata!;
            var (main, exif, gps, _) = Read(metadata.Exif.ToArray());
            Assert.Equal(1u, Value(main[0x0112], true));
            Assert.Equal(32u, Value(exif[0xA002], true));
            Assert.Equal("N", Text(gps[0x0001]));
            Assert.Equal(XmpPacket, metadata.Xmp.ToArray());
        }

        foreach (byte[] file in new[] { Save(decoded, image => s => image.SaveAsJpeg(s)), Save(decoded, image => s => image.SaveAsPng(s)), Save(decoded, image => s => image.SaveAsWebp(s)) })
        {
            using Image again = Image.Decode(file);
            Assert.Null(again.Metadata);
        }

        // A resized copy carries it too, with its own size.
        using Image small = decoded.Resize(16, 0);
        using Image back = Image.Decode(Save(small, image => s => image.SaveAsJpeg(s, new JpegEncoderOptions { Metadata = MetadataOptions.Keep.Without(ExifGroup.Location) })));
        var (_, smallExif, smallGps, _) = Read(back.Metadata!.Exif.ToArray());
        Assert.Equal(16u, Value(smallExif[0xA002], true));
        Assert.Empty(smallGps);
        Assert.True(back.Metadata.Xmp.IsEmpty);
    }

    [Fact]
    public void Images_made_in_code_have_no_metadata_to_keep()
    {
        using Image photo = JpegEncoderTests.Photo(8, 8);
        using Image back = Image.Decode(Save(photo, image => s => image.SaveAsJpeg(s, new JpegEncoderOptions { Metadata = MetadataOptions.Keep })));
        Assert.Null(back.Metadata);
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("png")]
    [InlineData("webp")]
    public async Task Exiftool_reads_what_we_keep(string format)
    {
        string exiftool = Oracle.Require("exiftool");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        using Image photo = JpegEncoderTests.Photo(48, 32, PixelFormat.Rgb24, seed: 2);
        byte[] jpeg = WithApp1(Save(photo, image => s => image.SaveAsJpeg(s)), [.. "Exif\0\0"u8, .. Camera(false, orientation: 6)]);
        using Image decoded = Image.Decode(jpeg);
        var keep = MetadataOptions.Keep.Without(ExifGroup.Camera);
        byte[] file = format switch
        {
            "jpg" => Save(decoded, image => s => image.SaveAsJpeg(s, new JpegEncoderOptions { Metadata = keep })),
            "png" => Save(decoded, image => s => image.SaveAsPng(s, new PngEncoderOptions { Metadata = keep })),
            _ => Save(decoded, image => s => image.SaveAsWebp(s, new WebpEncoderOptions { Metadata = keep })),
        };
        await File.WriteAllBytesAsync(folder.File("out." + format), file, TestContext.Current.CancellationToken);
        (int exit, byte[] output, string errors) = await Oracle.RunAsync(exiftool, "-s", "-n", "-Orientation", "-Artist", "-Model", "-ExifImageWidth", "-GPSLatitude", "-ExposureTime", "-Warning", "-Error", folder.File("out." + format));
        Assert.True(exit == 0, errors);
        string text = Encoding.UTF8.GetString(output);
        Assert.Contains("Orientation", text, StringComparison.Ordinal);
        Assert.Matches(@"Orientation\s*:\s*1\b", text);
        Assert.Matches(@"Artist\s*:\s*Alex Example", text);
        Assert.Matches(@"ExifImageWidth\s*:\s*32\b", text);
        Assert.Matches(@"GPSLatitude\s*:\s*59\.9", text);
        Assert.Matches(@"ExposureTime\s*:\s*0\.004", text);
        Assert.DoesNotContain("Model", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Warning", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Error", text, StringComparison.Ordinal);
    }

    private static byte[] Save(Image image, Func<Image, Action<Stream>> save)
    {
        using var output = new MemoryStream();
        save(image)(output);
        return output.ToArray();
    }
}
