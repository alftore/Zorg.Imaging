using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Tests.Codecs.Jpeg;
using Zorg.Imaging.Tests.Codecs.Png;

namespace Zorg.Imaging.Tests;

public class AnimationTests
{
    // A few-colour scene: a background with a band, and a square that moves; transparent around the edge when asked.
    internal static Image Scene(int width, int height, int step, bool transparentEdge = false)
    {
        var image = new Image(width, height, PixelFormat.Rgba32);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba32> row = image.GetRow<Rgba32>(y);
            for (int x = 0; x < width; x++)
            {
                bool edge = transparentEdge && (x < 3 || y < 3 || x >= width - 3 || y >= height - 3);
                bool square = x >= 5 + (3 * step) && x < 15 + (3 * step) && y >= 6 + step && y < 14 + step;
                row[x] = edge && !square ? new Rgba32(0, 0, 0, 0)
                    : square ? new Rgba32(220, 40, (byte)(30 * step), 255)
                    : y < height / 2 ? new Rgba32(20, 90, 160, 255)
                    : new Rgba32(240, 230, 200, 255);
            }
        }

        return image;
    }

    internal static byte[] Write(Func<Stream, AnimationWriter> create, IReadOnlyList<(Image Image, int Milliseconds)> frames)
    {
        using var stream = new MemoryStream();
        AnimationWriter writer = create(stream);
        foreach ((Image image, int milliseconds) in frames)
        {
            writer.WriteFrame(image, TimeSpan.FromMilliseconds(milliseconds));
        }

        writer.Complete();
        return stream.ToArray();
    }

    internal static List<AnimationFrame> ReadAll(byte[] file, out AnimationReader reader)
    {
        reader = AnimationReader.Open(file);
        var frames = new List<AnimationFrame>();
        while (reader.ReadFrame() is { } frame)
        {
            frames.Add(frame);
        }

        return frames;
    }

    private static void AssertSamePixels(Image expected, Image actual, bool binaryAlpha)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        for (int y = 0; y < expected.Height; y++)
        {
            ReadOnlySpan<Rgba32> e = expected.GetRow<Rgba32>(y);
            ReadOnlySpan<Rgba32> a = actual.GetRow<Rgba32>(y);
            for (int x = 0; x < expected.Width; x++)
            {
                bool clear = binaryAlpha ? e[x].A < 128 : e[x].A == 0;
                if (clear)
                {
                    Assert.True(a[x].A == 0, $"({x}, {y}) should be transparent: {a[x]}");
                }
                else
                {
                    Assert.True(e[x] == a[x], $"({x}, {y}): {a[x]}, expected {e[x]}");
                }
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void A_gif_of_few_colours_reads_back_exactly(int loops)
    {
        var frames = new List<(Image, int)>();
        for (int step = 0; step < 5; step++)
        {
            frames.Add((Scene(48, 32, step, transparentEdge: step % 2 == 0), 40 + (step * 10)));
        }

        try
        {
            byte[] gif = Write(stream => AnimationWriter.CreateGif(stream, loops), frames);
            List<AnimationFrame> read = ReadAll(gif, out AnimationReader reader);
            Assert.Equal(ImageFormat.Gif, reader.Format);
            Assert.True(reader.IsAnimated);
            Assert.Equal(loops, reader.LoopCount);
            Assert.Equal(frames.Count, read.Count);
            for (int i = 0; i < read.Count; i++)
            {
                AssertSamePixels(frames[i].Item1, read[i].Image, binaryAlpha: true);
                Assert.Equal(frames[i].Item2, read[i].Duration.TotalMilliseconds);
                read[i].Dispose();
            }
        }
        finally
        {
            frames.ForEach(f => f.Item1.Dispose());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void A_lossless_webp_reads_back_exactly(int loops)
    {
        var frames = new List<(Image, int)>();
        for (int step = 0; step < 4; step++)
        {
            Image frame = Scene(37, 29, step, transparentEdge: step != 1);

            // Partial alpha too, which a WebP keeps.
            frame.GetRow<Rgba32>(20)[30] = new Rgba32(10, 200, 30, (byte)(60 + step));
            frames.Add((frame, 33 * (step + 1)));
        }

        try
        {
            byte[] webp = Write(stream => AnimationWriter.CreateWebp(stream, loops, new WebpEncoderOptions { Lossless = true }), frames);
            List<AnimationFrame> read = ReadAll(webp, out AnimationReader reader);
            Assert.Equal(ImageFormat.Webp, reader.Format);
            Assert.Equal((37, 29), (reader.Width, reader.Height));
            Assert.Equal(loops, reader.LoopCount);
            Assert.Equal(frames.Count, read.Count);
            for (int i = 0; i < read.Count; i++)
            {
                AssertSamePixels(frames[i].Item1, read[i].Image, binaryAlpha: false);
                Assert.Equal(frames[i].Item2, read[i].Duration.TotalMilliseconds);
                read[i].Dispose();
            }

            // The first frame is also what a still decode gives.
            using Image still = Image.Decode(webp, new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
            AssertSamePixels(frames[0].Item1, still, binaryAlpha: false);
        }
        finally
        {
            frames.ForEach(f => f.Item1.Dispose());
        }
    }

    [Fact]
    public void A_lossy_webp_reads_back_close()
    {
        var frames = new List<(Image, int)>();
        for (int step = 0; step < 3; step++)
        {
            frames.Add((JpegEncoderTests.Photo(64, 48, PixelFormat.Rgba32, seed: 40 + step), 100));
        }

        try
        {
            byte[] webp = Write(stream => AnimationWriter.CreateWebp(stream, 0, new WebpEncoderOptions { Quality = 90 }), frames);
            List<AnimationFrame> read = ReadAll(webp, out _);
            Assert.Equal(frames.Count, read.Count);
            for (int i = 0; i < read.Count; i++)
            {
                using Image rgb = read[i].Image;
                double psnr = JpegEncoderTests.Psnr(rgb.GetPixelBytes(), frames[i].Item1.GetPixelBytes());
                Assert.True(psnr > 30, $"frame {i}: {psnr:0.0} dB");
            }
        }
        finally
        {
            frames.ForEach(f => f.Item1.Dispose());
        }
    }

    [Fact]
    public void Identical_frames_are_joined_and_only_the_change_is_coded()
    {
        using Image a = Scene(64, 64, 0);
        using Image b = Scene(64, 64, 1);
        byte[] gif = Write(stream => AnimationWriter.CreateGif(stream), [(a, 50), (a, 70), (b, 100), (b, 100)]);
        List<AnimationFrame> read = ReadAll(gif, out _);
        Assert.Equal([120.0, 200.0], read.ConvertAll(f => f.Duration.TotalMilliseconds));
        read.ForEach(f => f.Dispose());

        // The second frame covers only the square's old and new places.
        byte[] two = Write(stream => AnimationWriter.CreateWebp(stream, 0, new WebpEncoderOptions { Lossless = true }), [(a, 50), (b, 50)]);
        byte[] one = Write(stream => AnimationWriter.CreateWebp(stream, 0, new WebpEncoderOptions { Lossless = true }), [(a, 50)]);
        Assert.True(two.Length - one.Length < one.Length / 2, $"{two.Length} against {one.Length}");
    }

    [Fact]
    public void A_still_image_reads_as_one_frame()
    {
        using Image photo = JpegEncoderTests.Photo(20, 10, PixelFormat.Rgb24, seed: 1);
        AnimationReader reader = AnimationReader.Open(PngEncoderTests.Save(photo));
        Assert.False(reader.IsAnimated);
        Assert.Equal(1, reader.LoopCount);
        using (AnimationFrame? frame = reader.ReadFrame())
        {
            Assert.NotNull(frame);
            Assert.Equal(TimeSpan.Zero, frame.Duration);
            Assert.Equal(photo.GetPixelBytes().ToArray(), frame.Image.GetPixelBytes().ToArray());
        }

        Assert.Null(reader.ReadFrame());
    }

    [Fact]
    public async Task Frames_read_from_a_stream_one_at_a_time()
    {
        using Image a = Scene(30, 20, 0);
        using Image b = Scene(30, 20, 2);
        byte[] webp = Write(stream => AnimationWriter.CreateWebp(stream, 0, new WebpEncoderOptions { Lossless = true }), [(a, 10), (b, 20)]);
        using var input = new MemoryStream(webp);
        AnimationReader reader = await AnimationReader.OpenAsync(input, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => reader.ReadFrame());
        int count = 0;
        while (await reader.ReadFrameAsync(TestContext.Current.CancellationToken) is { } frame)
        {
            frame.Dispose();
            count++;
        }

        Assert.Equal(2, count);
    }

    [Fact]
    public void The_frame_limit_is_kept()
    {
        using Image a = Scene(16, 16, 0);
        using Image b = Scene(16, 16, 1);
        var frames = new List<(Image, int)>();
        for (int i = 0; i < 6; i++)
        {
            frames.Add((i % 2 == 0 ? a : b, 10));
        }

        var options = new DecodeOptions { Limits = new ImageLimits { MaxFrames = 5 } };
        foreach (byte[] file in new[]
        {
            Write(stream => AnimationWriter.CreateGif(stream), frames),
            Write(stream => AnimationWriter.CreateWebp(stream, 0, new WebpEncoderOptions { Lossless = true }), frames),
        })
        {
            AnimationReader reader = AnimationReader.Open(file, options);
            Assert.Throws<ImageLimitException>(() =>
            {
                while (reader.ReadFrame() is { } frame)
                {
                    frame.Dispose();
                }
            });
        }
    }

    [Fact]
    public void The_writer_checks_its_input()
    {
        using Image a = Scene(16, 16, 0);
        using Image other = Scene(17, 16, 0);
        using var stream = new MemoryStream();
        AnimationWriter writer = AnimationWriter.CreateGif(stream);
        Assert.Throws<InvalidOperationException>(writer.Complete);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.WriteFrame(a, TimeSpan.FromMilliseconds(-1)));
        writer.WriteFrame(a, TimeSpan.Zero);
        Assert.Throws<ArgumentException>(() => writer.WriteFrame(other, TimeSpan.Zero));
        writer.Complete();
        Assert.Throws<InvalidOperationException>(() => writer.WriteFrame(a, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnimationWriter.CreateWebp(stream, 65536));
    }
}
