using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Codecs.Png;

namespace Zorg.Imaging.Tests;

/// <summary>End-to-end tests of the header check through the public API, one small file per built-in format.</summary>
public class IdentifyTests
{
    public static TheoryData<string> Samples => ["png", "jpeg", "gif", "webp"];

    [Theory]
    [MemberData(nameof(Samples))]
    public void Identify_reads_every_built_in_format(string name)
    {
        (byte[] file, ImageFormat format, int width, int height) = Sample(name);

        IdentifyResult result = Image.Identify(file);

        Assert.True(result.Succeeded, result.Message);
        Assert.Same(format, result.Format);
        Assert.Same(format, result.Info.Format);
        Assert.Equal(width, result.Info.Width);
        Assert.Equal(height, result.Info.Height);
        Assert.Null(result.Message);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task IdentifyAsync_restores_the_position_of_a_seekable_stream(string name)
    {
        (byte[] file, ImageFormat format, _, _) = Sample(name);
        byte[] padded = [1, 2, 3, .. file];
        using var stream = new MemoryStream(padded);
        stream.Position = 3;

        IdentifyResult result = await Image.IdentifyAsync(stream, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Message);
        Assert.Same(format, result.Format);
        Assert.Equal(3, stream.Position);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Peeked_request_body_can_be_identified_and_then_read_in_full(string name)
    {
        (byte[] file, ImageFormat format, _, _) = Sample(name);
        using var body = new ForwardOnlyStream(file);

        FormatPeek peek = await ImageFormats.PeekAsync(body, TestContext.Current.CancellationToken);
        Assert.Same(format, peek.Format);

        IdentifyResult result = await Image.IdentifyAsync(peek.Stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task IdentifyAsync_honours_cancellation()
    {
        using var stream = new ForwardOnlyStream(Sample("png").File);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Image.IdentifyAsync(stream, cancellationToken: cancelled.Token));
    }

    [Fact]
    public void Pixel_limit_is_applied()
    {
        var options = new IdentifyOptions { Limits = new ImageLimits { MaxPixels = 5 } };

        IdentifyResult result = Image.Identify(Sample("png").File, options);

        Assert.Equal(IdentifyStatus.ExceedsLimits, result.Status);
        Assert.Same(ImageFormat.Png, result.Format);
        Assert.Null(result.Info);
        Assert.Contains("pixels", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Huge_dimensions_in_a_tiny_file_are_rejected()
    {
        IdentifyResult result = Image.Identify(Png(50_000, 50_000));

        Assert.Equal(IdentifyStatus.ExceedsLimits, result.Status);
    }

    [Fact]
    public void Empty_input_is_not_an_image()
    {
        IdentifyResult result = Image.Identify([]);

        Assert.Equal(IdentifyStatus.NotAnImage, result.Status);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public void Limits_reject_values_that_make_no_sense()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageLimits { MaxPixels = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageLimits { MaxFrames = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageLimits { MaxHeaderBytes = ImageFormats.MaxSignatureLength - 1 });
        Assert.Throws<ArgumentNullException>(() => new IdentifyOptions { Limits = null! });
    }

    [Fact]
    public async Task Arguments_are_checked()
    {
        Assert.Throws<ArgumentNullException>(() => Image.Identify(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await Image.IdentifyAsync(null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    // Guarantee 1, fuzz tier 1: every truncation and single-byte corruption of every sample gives a result, never an
    // exception, both from memory and from a stream that can't seek.
    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Truncated_and_corrupted_files_never_throw(string name)
    {
        byte[] file = Sample(name).File;
        foreach (byte[] variant in Mutations(file))
        {
            IdentifyResult fromMemory = Image.Identify(variant);
            Assert.NotEqual(IdentifyStatus.None, fromMemory.Status);
            Assert.Equal(fromMemory.Succeeded, fromMemory.Message is null);

            using var stream = new ForwardOnlyStream(variant);
            IdentifyResult fromStream = await Image.IdentifyAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(fromMemory.Status, fromStream.Status);
        }
    }

    private static IEnumerable<byte[]> Mutations(byte[] file)
    {
        for (int length = 0; length < file.Length; length++)
        {
            yield return file[..length];
        }

        byte[] values = [0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF];
        for (int i = 0; i < file.Length; i++)
        {
            foreach (byte value in values)
            {
                byte[] copy = (byte[])file.Clone();
                copy[i] = value;
                yield return copy;
            }
        }
    }

    private static (byte[] File, ImageFormat Format, int Width, int Height) Sample(string name) => name switch
    {
        "png" => (Png(3, 2), ImageFormat.Png, 3, 2),
        "jpeg" => (Jpeg(5, 4), ImageFormat.Jpeg, 5, 4),
        "gif" => (Gif(7, 6), ImageFormat.Gif, 7, 6),
        "webp" => (Webp(9, 8), ImageFormat.Webp, 9, 8),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static byte[] Png(int width, int height)
    {
        var file = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        AddPngChunk(file, "IHDR"u8, ihdr);
        AddPngChunk(file, "IDAT"u8, [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        AddPngChunk(file, "IEND"u8, []);
        return [.. file];
    }

    private static void AddPngChunk(List<byte> file, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        byte[] chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        type.CopyTo(chunk.AsSpan(4));
        data.CopyTo(chunk.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32.Compute(chunk.AsSpan(4, 4 + data.Length)));
        file.AddRange(chunk);
    }

    private static byte[] Jpeg(int width, int height) =>
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0xFF, 0xC0, 0x00, 0x0B, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x01, 0x01, 0x11, 0x00,
        0xFF, 0xD9,
    ];

    private static byte[] Gif(int width, int height) =>
    [
        .. "GIF89a"u8,
        (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0x80, 0x00, 0x00,
        0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF,
        0x2C, 0x00, 0x00, 0x00, 0x00, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0x00,
        0x02, 0x02, 0x44, 0x01, 0x00,
        0x3B,
    ];

    private static byte[] Webp(int width, int height)
    {
        // A lossless (VP8L) header: signature 0x2F, then width - 1 and height - 1 in 14 bits each.
        uint bits = (uint)(width - 1) | ((uint)(height - 1) << 14);
        byte[] vp8l = [0x2F, (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24), 0x00];
        byte[] file = new byte[12 + 8 + vp8l.Length];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(file.Length - 8));
        "WEBP"u8.CopyTo(file.AsSpan(8));
        "VP8L"u8.CopyTo(file.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), (uint)vp8l.Length);
        vp8l.CopyTo(file.AsSpan(20));
        return file;
    }

    /// <summary>A stream that can't seek, like a request body.</summary>
    private sealed class ForwardOnlyStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
