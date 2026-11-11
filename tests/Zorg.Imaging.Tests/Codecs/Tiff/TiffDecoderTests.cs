using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Tests.Codecs.Jpeg;
using Zorg.Imaging.Tests.Codecs.Png;
using static Zorg.Imaging.Tests.Codecs.DecoderFuzz;

namespace Zorg.Imaging.Tests.Codecs.Tiff;

public class TiffDecoderTests
{
    /// <summary>
    /// Writes a little-endian TIFF: one strip per <paramref name="rowsPerStrip"/> rows of <paramref name="strips"/>,
    /// already compressed as <paramref name="compression"/> says, with the directory at the end as many writers put it.
    /// </summary>
    internal static byte[] Tiff(int width, int height, int bits, int samples, int photometric, IReadOnlyList<byte[]> strips, int rowsPerStrip, int compression = 1, int predictor = 1, int extraSamples = -1, ushort[]? colorMap = null)
    {
        using var file = new MemoryStream();
        file.Write("II"u8);
        file.Write([42, 0, 0, 0, 0, 0]);
        var offsets = new List<long>();
        foreach (byte[] strip in strips)
        {
            offsets.Add(file.Position);
            file.Write(strip);
        }

        // Values too large for an entry go here, before the directory.
        long bitsAt = file.Position;
        for (int s = 0; s < samples; s++)
        {
            file.Write(BitConverter.GetBytes((ushort)bits));
        }

        long offsetsAt = file.Position;
        foreach (long o in offsets)
        {
            file.Write(BitConverter.GetBytes((uint)o));
        }

        long countsAt = file.Position;
        foreach (byte[] strip in strips)
        {
            file.Write(BitConverter.GetBytes((uint)strip.Length));
        }

        long mapAt = file.Position;
        foreach (ushort v in colorMap ?? [])
        {
            file.Write(BitConverter.GetBytes(v));
        }

        if ((file.Position & 1) != 0)
        {
            file.WriteByte(0);
        }

        var entries = new List<(ushort Tag, ushort Type, uint Count, uint Value)>
        {
            (256, 4, 1, (uint)width),
            (257, 4, 1, (uint)height),
            (258, 3, (uint)samples, samples <= 2 ? (uint)(bits | (samples == 2 ? bits << 16 : 0)) : (uint)bitsAt),
            (259, 3, 1, (uint)compression),
            (262, 3, 1, (uint)photometric),
            (273, 4, (uint)strips.Count, strips.Count == 1 ? (uint)offsets[0] : (uint)offsetsAt),
            (277, 3, 1, (uint)samples),
            (278, 4, 1, (uint)rowsPerStrip),
            (279, 4, (uint)strips.Count, strips.Count == 1 ? (uint)strips[0].Length : (uint)countsAt),
        };
        if (predictor != 1)
        {
            entries.Add((317, 3, 1, (uint)predictor));
        }

        if (colorMap is not null)
        {
            entries.Add((320, 3, (uint)colorMap.Length, (uint)mapAt));
        }

        if (extraSamples >= 0)
        {
            entries.Add((338, 3, 1, (uint)extraSamples));
        }

        long ifd = file.Position;
        file.Write(BitConverter.GetBytes((ushort)entries.Count));
        foreach ((ushort tag, ushort type, uint count, uint value) in entries)
        {
            file.Write(BitConverter.GetBytes(tag));
            file.Write(BitConverter.GetBytes(type));
            file.Write(BitConverter.GetBytes(count));
            file.Write(BitConverter.GetBytes(value));
        }

        file.Write([0, 0, 0, 0]);
        byte[] bytes = file.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)ifd);
        return bytes;
    }

    private static byte[] Rgb(int width, int height)
    {
        byte[] data = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            (data[3 * i], data[(3 * i) + 1], data[(3 * i) + 2]) = ((byte)(i * 7), (byte)(i * 13), (byte)(255 - i));
        }

        return data;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    // PackBits as a simple writer makes it: literal runs of up to 128.
    private static byte[] PackBits(byte[] data)
    {
        using var output = new MemoryStream();
        for (int at = 0; at < data.Length; at += 128)
        {
            int n = Math.Min(128, data.Length - at);
            if (n >= 3 && data.AsSpan(at, n).IndexOfAnyExcept(data[at]) < 0)
            {
                output.WriteByte((byte)(1 - n));
                output.WriteByte(data[at]);
            }
            else
            {
                output.WriteByte((byte)(n - 1));
                output.Write(data, at, n);
            }
        }

        return output.ToArray();
    }

    private static byte[] Differenced(byte[] data, int width, int samples)
    {
        byte[] result = (byte[])data.Clone();
        for (int row = 0; row < data.Length / (width * samples); row++)
        {
            for (int i = (width * samples) - 1; i >= samples; i--)
            {
                int at = (row * width * samples) + i;
                result[at] = (byte)(data[at] - data[at - samples]);
            }
        }

        return result;
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(32773, 1)]
    [InlineData(8, 1)]
    [InlineData(8, 2)]
    [InlineData(32773, 2)]
    public void Rgb_strips_decode_with_each_compression_and_the_predictor(int compression, int predictor)
    {
        const int Width = 13;
        const int Height = 7;
        byte[] rgb = Rgb(Width, Height);
        var strips = new List<byte[]>();
        for (int top = 0; top < Height; top += 3)
        {
            int rows = Math.Min(3, Height - top);
            byte[] strip = rgb.AsSpan(top * Width * 3, rows * Width * 3).ToArray();
            strip = predictor == 2 ? Differenced(strip, Width, 3) : strip;
            strips.Add(compression switch { 8 => Deflate(strip), 32773 => PackBits(strip), _ => strip });
        }

        using Image image = Image.Decode(Tiff(Width, Height, 8, 3, 2, strips, 3, compression, predictor));
        Assert.Equal(PixelFormat.Rgb24, image.PixelFormat);
        Assert.Equal(rgb, image.GetPixelBytes().ToArray());
    }

    [Fact]
    public void Grey_palette_and_alpha_decode()
    {
        // 1-bit WhiteIsZero: set bits are black.
        using Image bilevel = Image.Decode(Tiff(10, 1, 1, 1, 0, [[0b1100_0000, 0b0100_0000]], 1));
        Assert.Equal(PixelFormat.L8, bilevel.PixelFormat);
        Assert.Equal([0, 0, 255, 255, 255, 255, 255, 255, 255, 0], bilevel.GetPixelBytes().ToArray());

        // 4-bit palette: the map holds red, green and blue values of 16 bits each.
        ushort[] map = new ushort[3 * 16];
        map[1] = 65535;
        map[16 + 2] = 65535;
        using Image palette = Image.Decode(Tiff(2, 1, 4, 1, 3, [[0x12]], 1, colorMap: map));
        Assert.Equal(PixelFormat.Rgb24, palette.PixelFormat);
        Assert.Equal([255, 0, 0, 0, 255, 0], palette.GetPixelBytes().ToArray());

        // Associated alpha is divided out; unassociated is kept as it is.
        using Image associated = Image.Decode(Tiff(1, 1, 8, 4, 2, [[64, 32, 0, 128]], 1, extraSamples: 1));
        Assert.Equal(PixelFormat.Rgba32, associated.PixelFormat);
        Assert.Equal([128, 64, 0, 128], associated.GetPixelBytes().ToArray());
        using Image unassociated = Image.Decode(Tiff(1, 1, 8, 4, 2, [[64, 32, 0, 128]], 1, extraSamples: 2));
        Assert.Equal([64, 32, 0, 128], unassociated.GetPixelBytes().ToArray());
    }

    [Fact]
    public void Identify_reads_the_directory_at_the_end()
    {
        byte[] tiff = Tiff(300, 200, 8, 3, 2, [new byte[300 * 200 * 3]], 200);
        IdentifyResult result = Image.Identify(tiff);
        Assert.Equal(IdentifyStatus.Ok, result.Status);
        Assert.Equal((300, 200), (result.Info!.Width, result.Info.Height));
        Assert.False(result.Info.HasAlpha);
    }

    [Fact]
    public void Limits_and_unsupported_kinds_are_reported()
    {
        Assert.Throws<ImageLimitException>(() => Image.Decode(Tiff(60000, 60000, 8, 3, 2, [[0]], 60000)));
        Assert.Throws<UnsupportedImageFormatException>(() => Image.Decode(Tiff(2, 2, 8, 3, 2, [new byte[12]], 2, compression: 7)));
        Assert.Throws<UnsupportedImageFormatException>(() => Image.Decode(Tiff(2, 2, 8, 3, 6, [new byte[12]], 2)));
        Assert.Throws<ImageFormatException>(() => Image.Decode(Tiff(2, 2, 8, 3, 2, [], 2)));
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
        byte[] rgb = Rgb(9, 4);
        return new Dictionary<string, byte[]>
        {
            ["none"] = Tiff(9, 4, 8, 3, 2, [rgb], 4),
            ["deflate-predictor"] = Tiff(9, 4, 8, 3, 2, [Deflate(Differenced(rgb, 9, 3))], 4, 8, 2),
            ["packbits"] = Tiff(9, 4, 8, 3, 2, [PackBits(rgb)], 4, 32773),
            ["bilevel"] = Tiff(10, 1, 1, 1, 0, [[0b1100_0000, 0b0100_0000]], 1),
        };
    }

    [Theory]
    [InlineData("-compress None")]
    [InlineData("-compress LZW")]
    [InlineData("-compress LZW -define tiff:predictor=2")]
    [InlineData("-compress Zip -define tiff:predictor=2")]
    [InlineData("-compress RLE")]
    [InlineData("-compress LZW -define tiff:tile-geometry=32x32")]
    [InlineData("-compress None -interlace plane")]
    [InlineData("-compress LZW -depth 16")]
    [InlineData("-compress None -type Grayscale")]
    [InlineData("-compress None -type Palette")]
    [InlineData("-compress None -monochrome")]
    [InlineData("-compress LZW -alpha set -define tiff:alpha=unassociated")]
    [InlineData("-compress LZW -alpha set -define tiff:alpha=associated")]
    [InlineData("-compress None -endian MSB -depth 16")]
    public async Task Tiffs_magick_writes_decode_as_magick_decodes_them(string options)
    {
        string magick = Oracle.Require("magick");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        bool alpha = options.Contains("alpha", StringComparison.Ordinal);
        using Image photo = JpegEncoderTests.Photo(77, 51, alpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24, seed: 9);
        await File.WriteAllBytesAsync(folder.File("in.png"), PngEncoderTests.Save(photo), TestContext.Current.CancellationToken);
        (int written, _, string errors) = await Oracle.RunAsync(magick, [folder.File("in.png"), .. options.Split(' ', StringSplitOptions.RemoveEmptyEntries), folder.File("out.tif")]);
        Assert.True(written == 0, errors);
        (int read, _, string readErrors) = await Oracle.RunAsync(magick, folder.File("out.tif"), "-depth", "8", "rgba:" + folder.File("out.rgba"));
        Assert.True(read == 0, readErrors);
        byte[] expected = await File.ReadAllBytesAsync(folder.File("out.rgba"), TestContext.Current.CancellationToken);
        using Image ours = Image.Decode(await File.ReadAllBytesAsync(folder.File("out.tif"), TestContext.Current.CancellationToken), new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        byte[] actual = ours.GetPixelBytes().ToArray();
        Assert.Equal(expected.Length, actual.Length);
        byte[] tif = await File.ReadAllBytesAsync(folder.File("out.tif"), TestContext.Current.CancellationToken);
        string diag = "ours " + Convert.ToHexString(actual.AsSpan(0, 16)) + " magick " + Convert.ToHexString(expected.AsSpan(0, 16)) + " tif " + Convert.ToBase64String(tif);
        for (int i = 0; i < actual.Length; i++)
        {
            // Associated alpha divides out with rounding that may differ by one.
            Assert.True(Math.Abs(expected[i] - actual[i]) <= (options.Contains("=associated", StringComparison.Ordinal) ? 1 : 0), $"byte {i}: {actual[i]}, expected {expected[i]}; {diag}");
        }
    }
}
