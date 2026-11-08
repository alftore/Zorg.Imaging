using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Codecs.Gif;
using Zorg.Imaging.Formats;
using Zorg.Imaging.Tests.Codecs.Jpeg;
using Zorg.Imaging.Tests.Codecs.Png;

namespace Zorg.Imaging.Tests.Codecs.Gif;

/// <summary>
/// ImageMagick as the oracle: GIFs it writes, still and animated with every disposal, must draw frame by frame as its
/// <c>-coalesce</c> draws them; GIFs we write must decode in it as in ours; and our reduction must be about as close as
/// its own.
/// </summary>
public class GifOracleTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("-interlace GIF", false)]
    [InlineData("-dither None -colors 64", true)]
    [InlineData("-colors 16", true)]
    public async Task Still_gifs_magick_writes_decode_as_magick_decodes_them(string options, bool alpha)
    {
        string magick = Oracle.Require("magick");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        using Image photo = JpegEncoderTests.Photo(131, 77, alpha ? PixelFormat.Rgba32 : PixelFormat.Rgb24, seed: 3);
        await File.WriteAllBytesAsync(folder.File("in.png"), PngEncoderTests.Save(photo), TestContext.Current.CancellationToken);
        string[] arguments = [folder.File("in.png"), .. options.Split(' ', StringSplitOptions.RemoveEmptyEntries), folder.File("out.gif")];
        (int written, _, string errors) = await Oracle.RunAsync(magick, arguments);
        Assert.True(written == 0, errors);

        byte[] gif = await File.ReadAllBytesAsync(folder.File("out.gif"), TestContext.Current.CancellationToken);
        byte[] expected = await RgbaAsync(magick, folder, "out.gif[0]");
        using Image decoded = Image.Decode(gif, new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        Assert.Equal(Normalise(expected), Normalise(decoded.GetPixelBytes().ToArray()));
    }

    [Fact]
    public async Task Animations_draw_frame_by_frame_as_magick_coalesces_them()
    {
        string magick = Oracle.Require("magick");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        string[] frames = new string[4];
        for (int i = 0; i < frames.Length; i++)
        {
            using Image frame = JpegEncoderTests.Photo(40 - (i * 7), 30 - (i * 5), PixelFormat.Rgba32, seed: 20 + i);
            frames[i] = folder.File($"f{i}.png");
            await File.WriteAllBytesAsync(frames[i], PngEncoderTests.Save(frame), TestContext.Current.CancellationToken);
        }

        // Frames of different sizes at offsets, with each disposal.
        string[] arguments =
        [
            "-dispose", "None", frames[0],
            "-dispose", "Background", "-page", "+3+4", frames[1],
            "-dispose", "Previous", "-page", "+10+2", frames[2],
            "-dispose", "None", "-page", "+5+5", frames[3],
            "-dispose", "None", "-page", "+0+0", frames[3],
            "-colors", "128", folder.File("anim.gif"),
        ];
        (int written, _, string errors) = await Oracle.RunAsync(magick, arguments);
        Assert.True(written == 0, errors);

        byte[] gif = await File.ReadAllBytesAsync(folder.File("anim.gif"), TestContext.Current.CancellationToken);
        GifReader reader = await GifReader.OpenAsync(DataReader.FromMemory(gif), ImageLimits.Default, TestContext.Current.CancellationToken);
        byte[] coalesced = await RgbaAsync(magick, folder, "anim.gif", "-coalesce");
        int index = 0;
        while (await reader.NextFrameAsync(TestContext.Current.CancellationToken))
        {
            int size = reader.Canvas.Length * 4;
            byte[] expected = coalesced[(index * size)..((index + 1) * size)];
            byte[] ours = new byte[reader.Canvas.Length * 4];
            for (int i = 0; i < reader.Canvas.Length; i++)
            {
                uint p = reader.Canvas[i];
                (ours[4 * i], ours[(4 * i) + 1], ours[(4 * i) + 2], ours[(4 * i) + 3]) = ((byte)(p >> 16), (byte)(p >> 8), (byte)p, (byte)(p >> 24));
            }

            Assert.True(Normalise(expected).AsSpan().SequenceEqual(Normalise(ours)), $"frame {index}");
            index++;
        }

        Assert.Equal(5, index);
        Assert.Equal(5 * reader.Canvas.Length * 4, coalesced.Length);
    }

    [Theory]
    [InlineData(256, 1.0)]
    [InlineData(256, 0.0)]
    [InlineData(32, 1.0)]
    public async Task Magick_decodes_our_gifs_as_we_do(int colours, double dithering)
    {
        string magick = Oracle.Require("magick");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        using Image photo = JpegEncoderTests.Photo(131, 77, PixelFormat.Rgba32, seed: 5);
        byte[] gif = GifEncoderTests.Save(photo, new GifEncoderOptions { MaxColors = colours, Dithering = dithering });
        await File.WriteAllBytesAsync(folder.File("ours.gif"), gif, TestContext.Current.CancellationToken);
        byte[] expected = await RgbaAsync(magick, folder, "ours.gif");
        using Image decoded = Image.Decode(gif, new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        Assert.Equal(Normalise(expected), Normalise(decoded.GetPixelBytes().ToArray()));
    }

    [Theory]
    [InlineData(256)]
    [InlineData(64)]
    [InlineData(16)]
    public async Task Our_reduction_is_about_as_close_as_magicks(int colours)
    {
        string magick = Oracle.Require("magick");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        using Image photo = JpegEncoderTests.Photo(300, 200, PixelFormat.Rgb24, seed: 11);
        await File.WriteAllBytesAsync(folder.File("in.png"), PngEncoderTests.Save(photo), TestContext.Current.CancellationToken);
        (int written, _, string errors) = await Oracle.RunAsync(magick, folder.File("in.png"), "-dither", "None", "-colors", $"{colours}", folder.File("magick.gif"));
        Assert.True(written == 0, errors);
        byte[] theirs = await File.ReadAllBytesAsync(folder.File("magick.gif"), TestContext.Current.CancellationToken);
        using Image theirImage = Image.Decode(theirs);
        using Image ourImage = Image.Decode(GifEncoderTests.Save(photo, new GifEncoderOptions { MaxColors = colours, Dithering = 0 }));
        double theirPsnr = JpegEncoderTests.Psnr(theirImage.GetPixelBytes(), photo.GetPixelBytes());
        double ourPsnr = JpegEncoderTests.Psnr(ourImage.GetPixelBytes(), photo.GetPixelBytes());
        Assert.True(ourPsnr >= theirPsnr - 1.0, $"ours {ourPsnr:0.00} dB, magick {theirPsnr:0.00} dB");
    }

    private static async Task<byte[]> RgbaAsync(string magick, Oracle.TempFolder folder, string input, params string[] operations)
    {
        string output = folder.File("out.rgba");
        (int read, _, string errors) = await Oracle.RunAsync(magick, [folder.File(input.Split('[')[0]) + (input.Contains('[', StringComparison.Ordinal) ? input[input.IndexOf('[', StringComparison.Ordinal)..] : string.Empty), .. operations, "-depth", "8", "rgba:" + output]);
        Assert.True(read == 0, errors);
        return await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken);
    }

    // Transparent pixels as zeros, whatever colour they carry.
    private static byte[] Normalise(byte[] rgba)
    {
        byte[] copy = (byte[])rgba.Clone();
        for (int i = 0; i < copy.Length; i += 4)
        {
            if (copy[i + 3] == 0)
            {
                copy.AsSpan(i, 4).Clear();
            }
        }

        return copy;
    }
}
