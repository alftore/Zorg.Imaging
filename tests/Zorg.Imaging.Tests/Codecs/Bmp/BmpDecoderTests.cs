using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Tests.Codecs.Jpeg;
using Zorg.Imaging.Tests.Codecs.Png;
using static Zorg.Imaging.Tests.Codecs.DecoderFuzz;

namespace Zorg.Imaging.Tests.Codecs.Bmp;

public class BmpDecoderTests
{
    /// <summary>Writes a BMP: a 40-byte (or larger) DIB header, an optional palette and masks, then the rows given as stored.</summary>
    internal static byte[] Bmp(int width, int height, int bits, byte[] pixels, uint[]? palette = null, int compression = 0, uint[]? masks = null, int headerSize = 40)
    {
        int maskBytes = headerSize == 40 && masks is not null ? masks.Length * 4 : 0;
        int paletteBytes = (palette?.Length ?? 0) * 4;
        int offset = 14 + headerSize + maskBytes + paletteBytes;
        byte[] file = new byte[offset + pixels.Length];
        Span<byte> span = file;
        span[0] = (byte)'B';
        span[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(span[2..], file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[10..], offset);
        Span<byte> dib = span[14..];
        BinaryPrimitives.WriteInt32LittleEndian(dib, headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(dib[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(dib[8..], height);
        BinaryPrimitives.WriteInt16LittleEndian(dib[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(dib[14..], (short)bits);
        BinaryPrimitives.WriteInt32LittleEndian(dib[16..], compression);
        BinaryPrimitives.WriteInt32LittleEndian(dib[20..], pixels.Length);
        BinaryPrimitives.WriteInt32LittleEndian(dib[32..], palette?.Length ?? 0);
        int at = 14 + 40;
        if (masks is not null)
        {
            int start = headerSize == 40 ? at : 14 + 40;
            for (int i = 0; i < masks.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(span[(start + (4 * i))..], masks[i]);
            }

            at += maskBytes;
        }

        at = 14 + headerSize + maskBytes;
        foreach (uint colour in palette ?? [])
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[at..], colour);
            at += 4;
        }

        pixels.CopyTo(span[offset..]);
        return file;
    }

    private static Rgba32 Pixel(Image image, int x, int y) =>
        image.PixelFormat == PixelFormat.Rgba32 ? image.GetRow<Rgba32>(y)[x] : image.GetRow<Rgb24>(y)[x] is var p ? new Rgba32(p.R, p.G, p.B, 255) : default;

    [Fact]
    public void Twenty_four_bit_rows_read_bottom_up_with_padding()
    {
        // 2 x 2: bottom row blue, green; top row red, white. Rows padded to 8 bytes.
        byte[] pixels = [255, 0, 0, 0, 255, 0, 0, 0, 0, 0, 255, 255, 255, 255, 0, 0];
        using Image image = Image.Decode(Bmp(2, 2, 24, pixels));
        Assert.Equal(PixelFormat.Rgb24, image.PixelFormat);
        Assert.Equal(new Rgba32(255, 0, 0, 255), Pixel(image, 0, 0));
        Assert.Equal(new Rgba32(255, 255, 255, 255), Pixel(image, 1, 0));
        Assert.Equal(new Rgba32(0, 0, 255, 255), Pixel(image, 0, 1));
        Assert.Equal(new Rgba32(0, 255, 0, 255), Pixel(image, 1, 1));

        // A negative height stores the top row first.
        using Image topDown = Image.Decode(Bmp(2, -2, 24, pixels));
        Assert.Equal(new Rgba32(0, 0, 255, 255), Pixel(topDown, 0, 0));
    }

    [Fact]
    public void Palette_images_of_1_4_and_8_bits_read_through_the_palette()
    {
        uint[] palette = [0x000000, 0xFF0000, 0x00FF00, 0x0000FF];
        using Image one = Image.Decode(Bmp(9, 1, 1, [0b1010_0000, 0b1000_0000, 0, 0], palette[..2]));
        Assert.Equal(new Rgba32(255, 0, 0, 255), Pixel(one, 0, 0));
        Assert.Equal(new Rgba32(0, 0, 0, 255), Pixel(one, 1, 0));
        Assert.Equal(new Rgba32(255, 0, 0, 255), Pixel(one, 8, 0));

        using Image four = Image.Decode(Bmp(3, 1, 4, [0x23, 0x10, 0, 0], palette));
        Assert.Equal(new Rgba32(0, 255, 0, 255), Pixel(four, 0, 0));
        Assert.Equal(new Rgba32(0, 0, 255, 255), Pixel(four, 1, 0));
        Assert.Equal(new Rgba32(255, 0, 0, 255), Pixel(four, 2, 0));

        // An index past the palette is black.
        using Image eight = Image.Decode(Bmp(2, 1, 8, [3, 200, 0, 0], palette));
        Assert.Equal(new Rgba32(0, 0, 255, 255), Pixel(eight, 0, 0));
        Assert.Equal(new Rgba32(0, 0, 0, 255), Pixel(eight, 1, 0));
    }

    [Fact]
    public void Thirty_two_bit_pixels_keep_alpha_only_when_some_is_set()
    {
        byte[] empty = [10, 20, 30, 0, 40, 50, 60, 0];
        using Image opaque = Image.Decode(Bmp(2, 1, 32, empty));
        Assert.Equal(PixelFormat.Rgb24, opaque.PixelFormat);
        Assert.Equal(new Rgba32(30, 20, 10, 255), Pixel(opaque, 0, 0));

        byte[] translucent = [10, 20, 30, 128, 40, 50, 60, 255];
        using Image alpha = Image.Decode(Bmp(2, 1, 32, translucent, masks: [0xFF0000, 0xFF00, 0xFF, 0xFF000000], compression: 3, headerSize: 124));
        Assert.Equal(PixelFormat.Rgba32, alpha.PixelFormat);
        Assert.Equal(new Rgba32(30, 20, 10, 128), Pixel(alpha, 0, 0));
        Assert.Equal(new Rgba32(60, 50, 40, 255), Pixel(alpha, 1, 0));
    }

    [Fact]
    public void Sixteen_bit_pixels_read_through_their_masks()
    {
        // 5-6-5: pure red, green and blue, and white.
        ushort[] values = [0xF800, 0x07E0, 0x001F, 0xFFFF];
        byte[] pixels = new byte[8];
        for (int i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(2 * i), values[i]);
        }

        using Image image = Image.Decode(Bmp(4, 1, 16, pixels, masks: [0xF800, 0x07E0, 0x001F], compression: 3));
        Assert.Equal(new Rgba32(255, 0, 0, 255), Pixel(image, 0, 0));
        Assert.Equal(new Rgba32(0, 255, 0, 255), Pixel(image, 1, 0));
        Assert.Equal(new Rgba32(0, 0, 255, 255), Pixel(image, 2, 0));
        Assert.Equal(new Rgba32(255, 255, 255, 255), Pixel(image, 3, 0));

        // Without masks, 5-5-5.
        BinaryPrimitives.WriteUInt16LittleEndian(pixels, 0x7C00);
        using Image fives = Image.Decode(Bmp(4, 1, 16, pixels));
        Assert.Equal(new Rgba32(255, 0, 0, 255), Pixel(fives, 0, 0));
    }

    [Fact]
    public void Run_lengths_draw_runs_literals_and_moves_and_leave_skipped_pixels_transparent()
    {
        uint[] palette = [0x000000, 0xFF0000, 0x00FF00, 0x0000FF];

        // RLE8, 4 x 3, bottom up: a run of 3 red; end of row; literal green, blue, red (padded); move up one; end.
        byte[] rle8 = [3, 1, 0, 0, 0, 3, 2, 3, 1, 0, 0, 2, 0, 1, 0, 1];
        using Image eight = Image.Decode(Bmp(4, 3, 8, rle8, palette, compression: 1));
        Assert.Equal(PixelFormat.Rgba32, eight.PixelFormat);
        Assert.Equal(new Rgba32(255, 0, 0, 255), Pixel(eight, 2, 2));
        Assert.Equal(0, Pixel(eight, 3, 2).A);
        Assert.Equal(new Rgba32(0, 255, 0, 255), Pixel(eight, 0, 1));
        Assert.Equal(new Rgba32(255, 0, 0, 255), Pixel(eight, 2, 1));
        Assert.Equal(0, Pixel(eight, 0, 0).A);

        // RLE4: a run of 4 alternating blue and green, then a literal of 3 (red, green, blue), then the end.
        byte[] rle4 = [4, 0x32, 0, 3, 0x12, 0x30, 0, 1];
        using Image four = Image.Decode(Bmp(7, 1, 4, rle4, palette, compression: 2));
        Rgba32[] expected = [new(0, 0, 255, 255), new(0, 255, 0, 255), new(0, 0, 255, 255), new(0, 255, 0, 255), new(255, 0, 0, 255), new(0, 255, 0, 255), new(0, 0, 255, 255)];
        for (int x = 0; x < 7; x++)
        {
            Assert.Equal(expected[x], Pixel(four, x, 0));
        }
    }

    [Fact]
    public void Identify_reads_the_size_without_the_pixels()
    {
        byte[] bmp = Bmp(300, -200, 24, new byte[900 * 200]);
        IdentifyResult result = Image.Identify(bmp.AsSpan(0, 60).ToArray());
        Assert.Equal(IdentifyStatus.Ok, result.Status);
        Assert.Equal((300, 200), (result.Info!.Width, result.Info.Height));
        Assert.Same(ImageFormat.Bmp, result.Info.Format);
    }

    [Fact]
    public void Limits_are_checked_before_the_pixels()
    {
        byte[] huge = Bmp(60000, 60000, 24, []);
        Assert.Throws<ImageLimitException>(() => Image.Decode(huge));
        Assert.Throws<UnsupportedImageFormatException>(() => Image.Decode(Bmp(2, 2, 24, new byte[16], compression: 4)));
        Assert.Throws<ImageFormatException>(() => Image.Decode(Bmp(0, 2, 24, new byte[16])));
        Assert.Throws<ImageFormatException>(() => Image.Decode(Bmp(2, 2, 24, new byte[8])));
    }

    public static TheoryData<string> Samples => new(SampleFiles().Keys);

    [Theory]
    [MemberData(nameof(Samples))]
    public void Damage_throws_only_our_exceptions(string name)
    {
        byte[] file = SampleFiles()[name];
        void Decode(byte[] data)
        {
            using Image image = Image.Decode(data, new DecodeOptions { Limits = new ImageLimits { MaxPixels = 1 << 20 } });
        }

        Truncations(file, name, Decode);
        EdgeBytes(file, name, Decode);
        RandomDamage(file, name, 1000, Decode);
    }

    private static Dictionary<string, byte[]> SampleFiles()
    {
        uint[] palette = [0x000000, 0xFF0000, 0x00FF00, 0x0000FF];
        return new Dictionary<string, byte[]>
        {
            ["24"] = Bmp(5, 3, 24, new byte[16 * 3]),
            ["8"] = Bmp(5, 3, 8, [.. new byte[8], .. new byte[8], 1, 2, 3, 0, 1, 0, 0, 0], palette),
            ["rle8"] = Bmp(4, 3, 8, [3, 1, 0, 0, 0, 3, 2, 3, 1, 0, 0, 2, 0, 1, 0, 1], palette, compression: 1),
            ["rle4"] = Bmp(7, 1, 4, [4, 0x32, 0, 3, 0x12, 0x30, 0, 1], palette, compression: 2),
            ["32-v5"] = Bmp(2, 1, 32, [10, 20, 30, 128, 40, 50, 60, 255], masks: [0xFF0000, 0xFF00, 0xFF, 0xFF000000], compression: 3, headerSize: 124),
        };
    }

    [Theory]
    [InlineData("BMP3:", "")]
    [InlineData("BMP:", "")]
    [InlineData("BMP3:", "-type Palette")]
    [InlineData("BMP3:", "-colors 16")]
    [InlineData("BMP3:", "-monochrome")]
    [InlineData("BMP3:", "-type Palette -compress RLE")]
    [InlineData("BMP2:", "-type Palette")]
    public async Task Bmps_magick_writes_decode_as_magick_decodes_them(string type, string options)
    {
        string magick = Oracle.Require("magick");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        bool alpha = type == "BMP:";
        using Image photo = JpegEncoderTests.Photo(61, 37, alpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24, seed: 7);
        await File.WriteAllBytesAsync(folder.File("in.png"), PngEncoderTests.Save(photo), TestContext.Current.CancellationToken);
        (int written, _, string errors) = await Oracle.RunAsync(magick, [folder.File("in.png"), .. options.Split(' ', StringSplitOptions.RemoveEmptyEntries), type + folder.File("out.bmp")]);
        Assert.True(written == 0, errors);
        (int read, _, string readErrors) = await Oracle.RunAsync(magick, folder.File("out.bmp"), "-depth", "8", "rgba:" + folder.File("out.rgba"));
        Assert.True(read == 0, readErrors);
        byte[] expected = await File.ReadAllBytesAsync(folder.File("out.rgba"), TestContext.Current.CancellationToken);
        using Image ours = Image.Decode(await File.ReadAllBytesAsync(folder.File("out.bmp"), TestContext.Current.CancellationToken), new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        Assert.Equal(expected, ours.GetPixelBytes().ToArray());
    }
}
