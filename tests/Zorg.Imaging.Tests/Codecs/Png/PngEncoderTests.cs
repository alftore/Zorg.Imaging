using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static Zorg.Imaging.Tests.Codecs.Png.PngTestFiles;

namespace Zorg.Imaging.Tests.Codecs.Png;

public class PngEncoderTests
{
    public static TheoryData<PixelFormat, int> Cases()
    {
        var data = new TheoryData<PixelFormat, int>();
        foreach (PixelFormat format in new[] { PixelFormat.L8, PixelFormat.Rgb24, PixelFormat.Rgba32, PixelFormat.Bgra32, PixelFormat.L16, PixelFormat.Rgb48, PixelFormat.Rgba64 })
        {
            foreach (int level in new[] { 0, 1, 6, 9 })
            {
                data.Add(format, level);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_layout_and_level_round_trips_exactly(PixelFormat format, int level)
    {
        using Image image = Smooth(37, 23, format, seed: level);

        byte[] png = Save(image, new PngEncoderOptions { CompressionLevel = level });
        using Image decoded = Image.Decode(png, new DecodeOptions { PixelFormat = format == PixelFormat.Bgra32 ? PixelFormat.Bgra32 : PixelFormat.None });

        Assert.Equal(format, decoded.PixelFormat);
        Assert.Equal(Pixels(image), Pixels(decoded));
    }

    [Fact]
    public void The_file_is_8_bit_grey_RGB_or_RGBA_with_no_metadata()
    {
        foreach ((PixelFormat format, byte colourType) in new[] { (PixelFormat.L8, (byte)0), (PixelFormat.Rgb24, (byte)2), (PixelFormat.Rgba32, (byte)6), (PixelFormat.Bgra32, (byte)6) })
        {
            using var image = new Image(3, 2, format);
            byte[] png = Save(image);

            Assert.Equal(8, png[24]);
            Assert.Equal(colourType, png[25]);
            Assert.Equal(0, png[28]);
            Assert.Equal(["IHDR", "IDAT", "IEND"], Chunks(png).Select(c => c.Type));
        }
    }

    [Fact]
    public void Sixteen_bit_images_are_written_as_16_bit_big_endian_samples()
    {
        foreach ((PixelFormat format, byte colourType) in new[] { (PixelFormat.L16, (byte)0), (PixelFormat.Rgb48, (byte)2), (PixelFormat.Rgba64, (byte)6) })
        {
            using var image = new Image(3, 2, format);
            image.GetPixelBytes()[0] = 0x34;
            image.GetPixelBytes()[1] = 0x12;
            byte[] png = Save(image, new PngEncoderOptions { CompressionLevel = 0 });

            Assert.Equal(16, png[24]);
            Assert.Equal(colourType, png[25]);
            Assert.Equal(["IHDR", "IDAT", "IEND"], Chunks(png).Select(c => c.Type));

            // Stored deflate: zlib header (2), block header (5), filter byte, then the first sample, big-endian.
            int data = Chunks(png).Single(c => c.Type == "IDAT").Offset + 8;
            Assert.Equal([0, 0x12, 0x34], png[(data + 7)..(data + 10)]);
        }
    }

    [Fact]
    public void Large_images_are_split_into_64_KiB_IDAT_chunks()
    {
        using Image image = Smooth(300, 300, PixelFormat.Rgba32, seed: 1);

        byte[] png = Save(image, new PngEncoderOptions { CompressionLevel = 0 });
        using Image decoded = Image.Decode(png);

        int idats = Chunks(png).Count(c => c.Type == "IDAT");
        Assert.True(idats >= 5, $"{idats} IDAT chunks");
        Assert.Equal(Pixels(image), Pixels(decoded));
    }

    [Fact]
    public void Filtering_makes_smooth_images_smaller()
    {
        using Image image = Smooth(200, 200, PixelFormat.Rgb24, seed: 2);

        int stored = Save(image, new PngEncoderOptions { CompressionLevel = 0 }).Length;
        int compressed = Save(image).Length;

        Assert.True(compressed < stored / 2, $"{compressed} vs {stored}");
    }

    [Fact]
    public async Task Async_writes_the_same_bytes_as_sync()
    {
        using Image image = Smooth(64, 40, PixelFormat.Rgba32, seed: 3);
        using var async = new MemoryStream();

        await image.SaveAsPngAsync(async, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Save(image), async.ToArray());
    }

    [Fact]
    public void The_file_is_written_at_the_stream_position_and_the_stream_stays_open()
    {
        using var image = new Image(2, 2, PixelFormat.L8);
        using var stream = new MemoryStream();
        stream.Write("head"u8);

        image.SaveAsPng(stream);
        stream.Write("tail"u8);

        byte[] all = stream.ToArray();
        Assert.Equal(Save(image), all[4..^4]);
    }

    [Fact]
    public void Arguments_are_checked()
    {
        var image = new Image(2, 2, PixelFormat.L8);
        using var readOnly = new MemoryStream([], writable: false);

        Assert.Throws<ArgumentNullException>(() => image.SaveAsPng(null!));
        Assert.Throws<NotSupportedException>(() => image.SaveAsPng(readOnly));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PngEncoderOptions { CompressionLevel = 10 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PngEncoderOptions { CompressionLevel = -1 });
        image.Dispose();
        Assert.Throws<ObjectDisposedException>(() => image.SaveAsPng(new MemoryStream()));
    }

    internal static byte[] Save(Image image, PngEncoderOptions? options = null)
    {
        using var stream = new MemoryStream();
        image.SaveAsPng(stream, options);
        return stream.ToArray();
    }

    /// <summary>Gradients with noise: realistic for filters, and every byte value shows up.</summary>
    internal static Image Smooth(int width, int height, PixelFormat format, int seed)
    {
        var random = new Random(seed);
        var image = new Image(width, height, format);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = image.GetRowBytes(y);
            for (int i = 0; i < row.Length; i++)
            {
                int channel = i % image.BytesPerPixel;
                row[i] = (byte)((((i / image.BytesPerPixel) * (channel + 1)) + (y * 3) + random.Next(4)) & 0xFF);
            }
        }

        return image;
    }
}
