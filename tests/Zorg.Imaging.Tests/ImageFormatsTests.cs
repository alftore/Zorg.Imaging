#pragma warning disable CA2213 // Test stream wrappers leave the wrapped stream to the test.
#pragma warning disable CA1849, CA1835 // The read helpers exercise every read overload on purpose.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Tests.Formats;

namespace Zorg.Imaging.Tests;

public class ImageFormatsTests
{
    public static TheoryData<string> SignatureNames { get; } = [.. SignatureSamples.Signatures.Keys];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Known_has_twelve_distinct_formats()
    {
        Assert.Equal(12, ImageFormats.Known.Count);
        Assert.Equal(12, ImageFormats.Known.Distinct().Count());
        Assert.Equal(12, ImageFormats.Known.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(12, ImageFormats.Known.Select(f => f.MimeType).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void MaxSignatureLength_is_64() => Assert.Equal(64, ImageFormats.MaxSignatureLength);

    [Fact]
    public void Only_the_four_built_in_formats_are_supported()
    {
        ImageFormat[] supported = [.. ImageFormats.Known.Where(f => f.IsSupported)];

        Assert.Equal(4, supported.Length);
        Assert.Contains(ImageFormat.Png, supported);
        Assert.Contains(ImageFormat.Jpeg, supported);
        Assert.Contains(ImageFormat.Gif, supported);
        Assert.Contains(ImageFormat.Webp, supported);
    }

    [Theory]
    [MemberData(nameof(SignatureNames))]
    public void IsSupported_is_true_only_for_the_built_in_formats(string name)
    {
        (ImageFormat format, byte[] bytes) = SignatureSamples.Signatures[name];
        bool builtIn = format == ImageFormat.Png || format == ImageFormat.Jpeg || format == ImageFormat.Gif || format == ImageFormat.Webp;

        Assert.Equal(builtIn, ImageFormats.IsSupported(bytes));
        Assert.Equal(builtIn, format.IsSupported);
    }

    [Fact]
    public void IsSupported_is_false_for_non_images()
    {
        Assert.False(ImageFormats.IsSupported([]));
        Assert.False(ImageFormats.IsSupported("hello"u8));
    }

    [Fact]
    public void Every_known_format_has_a_name_a_mime_type_and_extensions()
    {
        foreach (ImageFormat format in ImageFormats.Known)
        {
            Assert.False(string.IsNullOrWhiteSpace(format.Name));
            Assert.StartsWith("image/", format.MimeType, StringComparison.Ordinal);
            Assert.NotEmpty(format.FileExtensions);
            Assert.All(format.FileExtensions, e => Assert.True(e.StartsWith('.')));
            Assert.Equal(format.Name, format.ToString());
        }
    }

    [Theory]
    [MemberData(nameof(SignatureNames))]
    public void Detect_returns_the_format_for_every_signature(string name)
    {
        (ImageFormat expected, byte[] bytes) = SignatureSamples.Signatures[name];
        Assert.Same(expected, ImageFormats.Detect(bytes));
    }

    [Fact]
    public void Every_known_format_has_a_signature_sample() =>
        Assert.Equal(
            ImageFormats.Known.OrderBy(f => f.Name, StringComparer.Ordinal),
            SignatureSamples.Signatures.Values.Select(v => v.Format).Distinct().OrderBy(f => f.Name, StringComparer.Ordinal));

    [Fact]
    public void Detect_of_empty_input_is_null() => Assert.Null(ImageFormats.Detect([]));

    [Fact]
    public async Task DetectFile_reads_the_signature_from_a_file()
    {
        string path = Path.GetTempFileName();
        try
        {
            byte[] file = new byte[5000];
            SignatureSamples.Signatures["png"].Bytes.CopyTo(file, 0);
            await File.WriteAllBytesAsync(path, file, Token);

            Assert.Same(ImageFormat.Png, ImageFormats.DetectFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(SignatureNames))]
    public async Task DetectFile_works_for_files_shorter_than_64_bytes(string name)
    {
        (ImageFormat expected, byte[] bytes) = SignatureSamples.Signatures[name];
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, bytes, Token);

            Assert.Same(expected, ImageFormats.DetectFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DetectFile_returns_null_for_an_empty_file_and_for_text()
    {
        string path = Path.GetTempFileName();
        try
        {
            Assert.Null(ImageFormats.DetectFile(path));

            await File.WriteAllTextAsync(path, "just some text", Token);
            Assert.Null(ImageFormats.DetectFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DetectFile_rejects_a_missing_path_argument()
    {
        Assert.Throws<ArgumentNullException>(() => ImageFormats.DetectFile(null!));
        Assert.Throws<ArgumentException>(() => ImageFormats.DetectFile(string.Empty));
    }

    [Fact]
    public void DetectFile_throws_for_a_file_that_does_not_exist()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        Assert.Throws<FileNotFoundException>(() => ImageFormats.DetectFile(path));
    }

    [Fact]
    public async Task DetectAsync_restores_the_position()
    {
        using MemoryStream stream = new(WithPrefix(0, "png"));

        Assert.Same(ImageFormat.Png, await ImageFormats.DetectAsync(stream, Token));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task DetectAsync_starts_at_the_current_position_and_restores_it()
    {
        using MemoryStream stream = new(WithPrefix(37, "jpeg"))
        {
            Position = 37,
        };

        Assert.Same(ImageFormat.Jpeg, await ImageFormats.DetectAsync(stream, Token));
        Assert.Equal(37, stream.Position);
    }

    [Fact]
    public async Task DetectAsync_restores_the_position_when_nothing_is_detected()
    {
        using MemoryStream stream = new(new byte[200])
        {
            Position = 100,
        };

        Assert.Null(await ImageFormats.DetectAsync(stream, Token));
        Assert.Equal(100, stream.Position);
    }

    [Fact]
    public async Task DetectAsync_on_a_stream_shorter_than_the_signature_is_null_and_restores_the_position()
    {
        using MemoryStream stream = new([0x89, 0x50]);

        Assert.Null(await ImageFormats.DetectAsync(stream, Token));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task DetectAsync_on_an_empty_stream_is_null()
    {
        using MemoryStream stream = new();

        Assert.Null(await ImageFormats.DetectAsync(stream, Token));
    }

    [Fact]
    public async Task DetectAsync_restores_the_position_when_cancelled()
    {
        using MemoryStream stream = new(WithPrefix(5, "png"))
        {
            Position = 5,
        };
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ImageFormats.DetectAsync(stream, cts.Token));
        Assert.Equal(5, stream.Position);
    }

    [Fact]
    public async Task DetectAsync_throws_for_a_non_seekable_stream()
    {
        using NonSeekableStream stream = new(new MemoryStream(WithPrefix(0, "png")), 100);

        await Assert.ThrowsAsync<NotSupportedException>(async () => await ImageFormats.DetectAsync(stream, Token));
    }

    [Fact]
    public async Task DetectAsync_rejects_a_null_stream() =>
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await ImageFormats.DetectAsync(null!, Token));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(64)]
    [InlineData(100)]
    public async Task PeekAsync_on_a_non_seekable_stream_returns_a_stream_with_every_original_byte(int chunk)
    {
        byte[] original = Sequence(1000, "png");

        foreach (string mode in new[] { "sync", "async", "byte", "memory" })
        {
            using NonSeekableStream source = new(new MemoryStream(original), 7);
            FormatPeek peek = await ImageFormats.PeekAsync(source, Token);

            Assert.Same(ImageFormat.Png, peek.Format);
            Assert.NotSame(source, peek.Stream);
            Assert.False(peek.Stream.CanSeek);
            Assert.Equal(original, await ReadAllAsync(peek.Stream, mode, chunk));
        }
    }

    [Fact]
    public async Task PeekAsync_on_a_non_seekable_stream_shorter_than_the_signature_still_yields_every_byte()
    {
        byte[] original = [0x89, 0x50, 0x4E];
        using NonSeekableStream source = new(new MemoryStream(original), 1);

        FormatPeek peek = await ImageFormats.PeekAsync(source, Token);

        Assert.Null(peek.Format);
        Assert.Equal(original, await ReadAllAsync(peek.Stream, "sync", 2));
    }

    [Fact]
    public async Task PeekAsync_on_an_empty_non_seekable_stream_yields_nothing()
    {
        using NonSeekableStream source = new(new MemoryStream(), 1);

        FormatPeek peek = await ImageFormats.PeekAsync(source, Token);

        Assert.Null(peek.Format);
        Assert.Empty(await ReadAllAsync(peek.Stream, "async", 10));
    }

    [Fact]
    public async Task PeekAsync_on_a_non_seekable_stream_of_exactly_64_bytes_yields_every_byte()
    {
        byte[] original = Sequence(64, "qoi");
        using NonSeekableStream source = new(new MemoryStream(original), 64);

        FormatPeek peek = await ImageFormats.PeekAsync(source, Token);

        Assert.Same(ImageFormat.Qoi, peek.Format);
        Assert.Equal(original, await ReadAllAsync(peek.Stream, "memory", 13));
    }

    [Fact]
    public async Task PeekAsync_on_a_non_seekable_stream_does_not_dispose_it_when_the_result_is_disposed()
    {
        byte[] original = Sequence(100, "gif89a");
        using MemoryStream inner = new(original);
        using NonSeekableStream source = new(inner, 100);

        FormatPeek peek = await ImageFormats.PeekAsync(source, Token);
        await peek.Stream.DisposeAsync();

        Assert.True(inner.CanRead);
    }

    [Fact]
    public async Task PeekAsync_on_a_seekable_stream_returns_the_same_stream_at_its_starting_position()
    {
        using MemoryStream stream = new(WithPrefix(11, "webp"))
        {
            Position = 11,
        };

        FormatPeek peek = await ImageFormats.PeekAsync(stream, Token);

        Assert.Same(ImageFormat.Webp, peek.Format);
        Assert.Same(stream, peek.Stream);
        Assert.Equal(11, stream.Position);
    }

    [Fact]
    public async Task PeekAsync_rejects_a_null_stream() =>
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await ImageFormats.PeekAsync(null!, Token));

    [Fact]
    public void Identify_of_non_image_bytes_is_NotAnImage()
    {
        IdentifyResult result = Image.Identify("This is not an image."u8.ToArray());

        Assert.Equal(IdentifyStatus.NotAnImage, result.Status);
        Assert.False(result.Succeeded);
        Assert.Null(result.Format);
        Assert.Null(result.Info);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public void Identify_of_empty_bytes_is_NotAnImage()
    {
        IdentifyResult result = Image.Identify([]);

        Assert.Equal(IdentifyStatus.NotAnImage, result.Status);
        Assert.Null(result.Format);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Theory]
    [InlineData("tiff-le")]
    [InlineData("tiff-be")]
    public void Identify_of_a_tiff_is_UnsupportedFormat(string name)
    {
        IdentifyResult result = Image.Identify(SignatureSamples.Signatures[name].Bytes);

        Assert.Equal(IdentifyStatus.UnsupportedFormat, result.Status);
        Assert.Same(ImageFormat.Tiff, result.Format);
        Assert.Null(result.Info);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Theory]
    [InlineData("avif", "AVIF")]
    [InlineData("heic", "HEIF")]
    [InlineData("jxl-codestream", "JPEG XL")]
    [InlineData("bmp", "BMP")]
    [InlineData("ico", "ICO")]
    [InlineData("psd", "PSD")]
    [InlineData("qoi", "QOI")]
    public void Identify_of_other_known_formats_is_UnsupportedFormat_and_names_the_format(string name, string formatName)
    {
        IdentifyResult result = Image.Identify(SignatureSamples.Signatures[name].Bytes);

        Assert.Equal(IdentifyStatus.UnsupportedFormat, result.Status);
        Assert.NotNull(result.Format);
        Assert.Equal(formatName, result.Format.Name);
        Assert.Contains(formatName, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdentifyAsync_of_non_image_bytes_on_a_non_seekable_stream_is_NotAnImage()
    {
        using NonSeekableStream stream = new(new MemoryStream("<html>not an image</html>"u8.ToArray()), 4);

        IdentifyResult result = await Image.IdentifyAsync(stream, cancellationToken: Token);

        Assert.Equal(IdentifyStatus.NotAnImage, result.Status);
        Assert.Null(result.Format);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task IdentifyAsync_of_a_tiff_on_a_non_seekable_stream_is_UnsupportedFormat()
    {
        using NonSeekableStream stream = new(new MemoryStream(Sequence(500, "tiff-le")), 3);

        IdentifyResult result = await Image.IdentifyAsync(stream, cancellationToken: Token);

        Assert.Equal(IdentifyStatus.UnsupportedFormat, result.Status);
        Assert.Same(ImageFormat.Tiff, result.Format);
    }

    [Fact]
    public async Task IdentifyAsync_of_a_tiff_on_a_seekable_stream_is_UnsupportedFormat()
    {
        using MemoryStream stream = new(Sequence(500, "tiff-be"));

        IdentifyResult result = await Image.IdentifyAsync(stream, cancellationToken: Token);

        Assert.Equal(IdentifyStatus.UnsupportedFormat, result.Status);
        Assert.Same(ImageFormat.Tiff, result.Format);
    }

    [Fact]
    public async Task IdentifyAsync_of_an_empty_non_seekable_stream_is_NotAnImage()
    {
        using NonSeekableStream stream = new(new MemoryStream(), 3);

        IdentifyResult result = await Image.IdentifyAsync(stream, cancellationToken: Token);

        Assert.Equal(IdentifyStatus.NotAnImage, result.Status);
        Assert.Null(result.Format);
    }

    private static byte[] WithPrefix(int junk, string signature)
    {
        byte[] bytes = new byte[junk + 200];
        SignatureSamples.Signatures[signature].Bytes.CopyTo(bytes, junk);
        return bytes;
    }

    /// <summary>A buffer that starts with a signature and continues with a recognisable pattern.</summary>
    private static byte[] Sequence(int length, string signature)
    {
        byte[] sig = SignatureSamples.Signatures[signature].Bytes;
        byte[] bytes = new byte[Math.Max(length, sig.Length)];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 31) + 7);
        }

        sig.CopyTo(bytes, 0);
        return bytes;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, string mode, int chunk)
    {
        using MemoryStream result = new();
        byte[] buffer = new byte[chunk];
        while (true)
        {
            int read;
            if (mode == "byte")
            {
                int value = stream.ReadByte();
                if (value < 0)
                {
                    return result.ToArray();
                }

                result.WriteByte((byte)value);
                continue;
            }

            read = mode switch
            {
                "sync" => stream.Read(buffer, 0, buffer.Length),
                "async" => await stream.ReadAsync(buffer, 0, buffer.Length, Token),
                _ => await stream.ReadAsync(buffer.AsMemory(), Token),
            };

            if (read == 0)
            {
                return result.ToArray();
            }

            result.Write(buffer, 0, read);
        }
    }

    /// <summary>A read-only stream that can't seek and returns at most <c>maxChunk</c> bytes per read.</summary>
    private sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _maxChunk;

        public NonSeekableStream(Stream inner, int maxChunk)
        {
            _inner = inner;
            _maxChunk = maxChunk;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, _maxChunk));

        public override int Read(Span<byte> buffer) => _inner.Read(buffer[..Math.Min(buffer.Length, _maxChunk)]);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer[..Math.Min(buffer.Length, _maxChunk)], cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
