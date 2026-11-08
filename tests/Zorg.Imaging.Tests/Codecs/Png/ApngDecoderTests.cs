using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using static Zorg.Imaging.Tests.Codecs.Png.PngTestFiles;

namespace Zorg.Imaging.Tests.Codecs.Png;

/// <summary>An APNG decodes to its first frame: the default image when it's part of the animation, else the first fdAT frame.</summary>
public class ApngDecoderTests
{
    private static readonly int[] DefaultSamples = RandomSamples(8, 6, 8, 6, seed: 1);

    [Fact]
    public void A_default_image_that_is_the_first_frame_is_decoded()
    {
        int[] other = RandomSamples(8, 6, 8, 6, seed: 2);
        byte[] png = Animation(defaultIsFrame: true, frame: (8, 6, 0, 0, other));
        using Image image = Image.Decode(png);
        Assert.Equal(ExpectedRgba(8, 6, 8, 6, DefaultSamples), Pixels(image));
    }

    [Fact]
    public void A_hidden_default_image_gives_way_to_the_first_frame()
    {
        int[] frame = RandomSamples(8, 6, 8, 6, seed: 3);
        using Image image = Image.Decode(Animation(defaultIsFrame: false, frame: (8, 6, 0, 0, frame)));
        Assert.Equal(PixelFormat.Rgba32, image.PixelFormat);
        Assert.Equal(ExpectedRgba(8, 6, 8, 6, frame), Pixels(image));
    }

    [Theory]
    [InlineData(6, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(4, true)]
    public void A_smaller_first_frame_sits_on_a_transparent_canvas(int colourType, bool interlaced)
    {
        int[] frame = RandomSamples(3, 2, 8, colourType, seed: 4);
        byte[] png = Animation(defaultIsFrame: false, frame: (3, 2, 4, 3, frame), colourType: colourType, interlaced: interlaced);
        using Image image = Image.Decode(png);
        Assert.Equal((8, 6, PixelFormat.Rgba32), (image.Width, image.Height, image.PixelFormat));
        byte[] expected = ExpectedRgba(3, 2, 8, colourType, frame);
        byte[] pixels = Pixels(image);
        for (int y = 0; y < 6; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                bool inside = x is >= 4 and < 7 && y is >= 3 and < 5;
                byte[] want = inside ? expected.AsSpan((((y - 3) * 3) + (x - 4)) * 4, 4).ToArray() : [0, 0, 0, 0];
                Assert.Equal(want, pixels.AsSpan(((y * 8) + x) * 4, 4).ToArray());
            }
        }
    }

    [Fact]
    public void A_sixteen_bit_smaller_frame_keeps_sixteen_bits()
    {
        int[] frame = RandomSamples(3, 2, 16, 2, seed: 5);
        using Image image = Image.Decode(Animation(defaultIsFrame: false, frame: (3, 2, 1, 1, frame), colourType: 2, depth: 16));
        Assert.Equal(PixelFormat.Rgba64, image.PixelFormat);
    }

    [Fact]
    public async Task A_stream_decodes_as_memory_does()
    {
        byte[] png = Animation(defaultIsFrame: false, frame: (5, 4, 2, 1, RandomSamples(5, 4, 8, 6, seed: 6)));
        using Image memory = Image.Decode(png);
        using Image stream = await Image.DecodeAsync(new ForwardOnlyStream(png), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(Pixels(memory), Pixels(stream));
    }

    [Fact]
    public void Broken_frames_are_corrupt()
    {
        int[] samples = RandomSamples(8, 6, 8, 6, seed: 7);

        // Outside the canvas.
        Assert.Throws<ImageFormatException>(() => Image.Decode(Animation(false, (8, 6, 1, 0, samples))).Dispose());

        // No fdAT after the fcTL.
        byte[] png = Animation(false, (8, 6, 0, 0, samples));
        byte[] noData = Without(png, "fdAT");
        Assert.Throws<ImageFormatException>(() => Image.Decode(noData).Dispose());

        // No fcTL at all.
        Assert.Throws<ImageFormatException>(() => Image.Decode(Without(Without(png, "fdAT"), "fcTL")).Dispose());

        // A damaged fdAT CRC.
        int at = png.AsSpan().IndexOf("fdAT"u8);
        int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at - 4));
        png[at + 4 + length] ^= 1;
        Assert.Throws<ImageFormatException>(() => Image.Decode(png).Dispose());
    }

    [Fact]
    public void Damage_throws_only_our_exceptions()
    {
        byte[] png = Animation(false, (5, 4, 2, 1, RandomSamples(5, 4, 8, 6, seed: 8)));
        var random = new Random(8);
        for (int length = 0; length < png.Length; length++)
        {
            Exception? cut = Record.Exception(() => Image.Decode(png[..length]).Dispose());
            Assert.True(cut is ImageException, $"cut to {length}: {cut}");
        }

        for (int round = 0; round < 2000; round++)
        {
            byte[] copy = (byte[])png.Clone();
            copy[random.Next(copy.Length)] ^= (byte)(1 << random.Next(8));
            Exception? error = Record.Exception(() => Image.Decode(copy).Dispose());
            Assert.True(error is null or ImageException, $"round {round}: {error}");
        }
    }

    // An 8 x 6 APNG: acTL, an fcTL before IDAT when the default image is a frame, the default image, then the given
    // frame as fcTL and fdAT (split over two fdAT chunks), then a second frame that is never shown.
    private static byte[] Animation(
        bool defaultIsFrame, (int Width, int Height, int Left, int Top, int[] Samples) frame, int colourType = 6, int depth = 8, bool interlaced = false)
    {
        int[] defaults = colourType == 6 && depth == 8 ? DefaultSamples : RandomSamples(8, 6, depth, colourType, seed: 1);
        using var file = new MemoryStream();
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        file.Write(Chunk("IHDR", Ihdr(8, 6, depth, colourType, interlaced)));
        byte[] actl = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(actl, defaultIsFrame ? 3u : 2u);
        file.Write(Chunk("acTL", actl));
        uint sequence = 0;
        if (defaultIsFrame)
        {
            file.Write(Chunk("fcTL", Fctl(sequence++, 8, 6, 0, 0)));
        }

        file.Write(Chunk("IDAT", Compress(ImageData(8, 6, depth, colourType, defaults, interlaced))));
        byte[] data = Compress(ImageData(frame.Width, frame.Height, depth, colourType, frame.Samples, interlaced));
        file.Write(Chunk("fcTL", Fctl(sequence++, frame.Width, frame.Height, frame.Left, frame.Top)));
        int half = data.Length / 2;
        file.Write(Chunk("fdAT", [.. Sequence(sequence++), .. data[..half]]));
        file.Write(Chunk("fdAT", [.. Sequence(sequence++), .. data[half..]]));
        file.Write(Chunk("fcTL", Fctl(sequence++, 2, 2, 0, 0)));
        file.Write(Chunk("fdAT", [.. Sequence(sequence++), .. Compress(ImageData(2, 2, depth, colourType, new int[2 * 2 * Channels(colourType)], interlaced))]));
        file.Write(Chunk("IEND", []));
        return file.ToArray();
    }

    private static byte[] Fctl(uint sequence, int width, int height, int left, int top)
    {
        byte[] fctl = new byte[26];
        BinaryPrimitives.WriteUInt32BigEndian(fctl, sequence);
        BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(8), (uint)height);
        BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(12), (uint)left);
        BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(16), (uint)top);
        BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(22), 10);
        return fctl;
    }

    private static byte[] Sequence(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    // The file without the first chunk of a type.
    private static byte[] Without(byte[] png, string type)
    {
        foreach ((int offset, string found) in Chunks(png))
        {
            if (found == type)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
                return [.. png[..offset], .. png[(offset + 12 + length)..]];
            }
        }

        return png;
    }
}
