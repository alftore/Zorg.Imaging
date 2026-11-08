using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Tests.Codecs.Jpeg;
using Zorg.Imaging.Tests.Codecs.Png;

namespace Zorg.Imaging.Tests;

/// <summary>
/// ImageMagick as the oracle: animated WebPs it writes (through libwebp's animation encoder, which picks sub-rectangles,
/// blending and disposal itself) must read frame by frame as its <c>-coalesce</c> draws them, and animations we write
/// must read in it as in ours.
/// </summary>
public class AnimationOracleTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Animated_webps_magick_writes_read_as_magick_coalesces_them(bool lossless)
    {
        string magick = Oracle.Require("magick");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        var arguments = new List<string> { "-delay", "7" };
        for (int step = 0; step < 5; step++)
        {
            // Moving shapes over a partly transparent, partly translucent background, so the encoder uses blending,
            // disposal and sub-rectangles.
            using Image frame = AnimationTests.Scene(60, 44, step, transparentEdge: step % 2 == 0);
            using Image noise = JpegEncoderTests.Photo(8, 8, PixelFormat.Rgba32, seed: step);
            for (int y = 0; y < 8; y++)
            {
                Span<Rgba32> row = frame.GetRow<Rgba32>(30 + y);
                ReadOnlySpan<Rgba32> source = noise.GetRow<Rgba32>(y);
                for (int x = 0; x < 8; x++)
                {
                    row[40 + x] = source[x] with { A = (byte)(100 + (step * 30)) };
                }
            }

            string path = folder.File($"f{step}.png");
            await File.WriteAllBytesAsync(path, PngEncoderTests.Save(frame), TestContext.Current.CancellationToken);
            arguments.Add(path);
        }

        arguments.AddRange(["-loop", "3", "-define", lossless ? "webp:lossless=true" : "webp:quality=90", folder.File("anim.webp")]);
        (int written, _, string errors) = await Oracle.RunAsync(magick, [.. arguments]);
        Assert.True(written == 0, errors);

        byte[] webp = await File.ReadAllBytesAsync(folder.File("anim.webp"), TestContext.Current.CancellationToken);
        byte[] coalesced = await RgbaAsync(magick, folder, "anim.webp", "-coalesce");
        List<AnimationFrame> frames = AnimationTests.ReadAll(webp, out AnimationReader reader);
        Assert.Equal(3, reader.LoopCount);
        CompareFrames(coalesced, frames, tolerance: 1);
        Assert.All(frames, f => Assert.Equal(70, f.Duration.TotalMilliseconds));
        frames.ForEach(f => f.Dispose());
    }

    [Theory]
    [InlineData("gif")]
    [InlineData("webp")]
    public async Task Magick_reads_our_animations_as_we_do(string format)
    {
        string magick = Oracle.Require("magick");
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        var frames = new List<(Image, int)>();
        for (int step = 0; step < 5; step++)
        {
            frames.Add((AnimationTests.Scene(50, 36, step, transparentEdge: step is 1 or 2), 60));
        }

        try
        {
            byte[] file = format == "gif"
                ? AnimationTests.Write(stream => AnimationWriter.CreateGif(stream), frames)
                : AnimationTests.Write(stream => AnimationWriter.CreateWebp(stream, 0, new WebpEncoderOptions { Lossless = true }), frames);
            await File.WriteAllBytesAsync(folder.File("ours." + format), file, TestContext.Current.CancellationToken);
            byte[] coalesced = await RgbaAsync(magick, folder, "ours." + format, "-coalesce");
            List<AnimationFrame> read = AnimationTests.ReadAll(file, out _);
            CompareFrames(coalesced, read, tolerance: 0);
            read.ForEach(f => f.Dispose());
        }
        finally
        {
            frames.ForEach(f => f.Item1.Dispose());
        }
    }

    private static void CompareFrames(byte[] coalesced, List<AnimationFrame> frames, int tolerance)
    {
        int size = frames[0].Image.Width * frames[0].Image.Height * 4;
        Assert.Equal(coalesced.Length / size, frames.Count);
        for (int f = 0; f < frames.Count; f++)
        {
            ReadOnlySpan<byte> expected = coalesced.AsSpan(f * size, size);
            ReadOnlySpan<byte> ours = frames[f].Image.GetPixelBytes();
            for (int i = 0; i < size; i += 4)
            {
                if (expected[i + 3] == 0 && ours[i + 3] == 0)
                {
                    continue;
                }

                for (int c = 0; c < 4; c++)
                {
                    Assert.True(
                        Math.Abs(expected[i + c] - ours[i + c]) <= tolerance,
                        $"frame {f}, pixel {i / 4}, channel {c}: {ours[i + c]}, expected {expected[i + c]}");
                }
            }
        }
    }

    private static async Task<byte[]> RgbaAsync(string magick, Oracle.TempFolder folder, string input, params string[] operations)
    {
        string output = folder.File("out.rgba");
        (int read, _, string errors) = await Oracle.RunAsync(magick, [folder.File(input), .. operations, "-depth", "8", "rgba:" + output]);
        Assert.True(read == 0, errors);
        return await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken);
    }
}
