using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Codecs.Png;
using Zorg.Imaging.Tests.Formats;

namespace Zorg.Imaging.Tests;

/// <summary>
/// The promises of the format check beyond reading the right numbers: other kinds of file are turned away after the
/// signature, and only the headers are read. <c>FormatSignaturesTests</c> proves the signature check allocates nothing.
/// </summary>
public class HeaderOnlyTests
{
    /// <summary>A 60 KB block after IFD0, like a camera's thumbnail and maker notes.</summary>
    private const int BulkyExifTail = 60 * 1024;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string> NonImageNames { get; } = [.. SignatureSamples.NonImages.Keys];

    [Theory]
    [MemberData(nameof(NonImageNames))]
    public async Task Other_kinds_of_file_are_not_images(string name)
    {
        byte[] file = SignatureSamples.NonImages[name];

        Assert.Null(ImageFormats.Detect(file));
        Assert.False(ImageFormats.IsSupported(file));
        Assert.Equal(IdentifyStatus.NotAnImage, Image.Identify(file).Status);

        using var stream = new CountingStream(file, canSeek: false);
        Assert.Equal(IdentifyStatus.NotAnImage, (await Image.IdentifyAsync(stream, cancellationToken: Token)).Status);
        Assert.True(stream.BytesRead <= ImageFormats.MaxSignatureLength, $"Read {stream.BytesRead} bytes to turn it away.");
    }

    public static TheoryData<string> BulkyExifNames { get; } = ["jpeg", "png", "webp"];

    [Theory]
    [MemberData(nameof(BulkyExifNames))]
    public async Task A_bulky_exif_block_costs_a_few_hundred_bytes_when_the_stream_can_seek(string name)
    {
        byte[] file = WithBulkyExif(name, width: 40, height: 30, orientation: 6);

        using var stream = new CountingStream(file, canSeek: true);
        IdentifyResult result = await Image.IdentifyAsync(stream, cancellationToken: Token);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(6, result.Info.Orientation);
        Assert.True(result.Info.HasExif);
        Assert.Equal(30, result.Info.DisplayWidth);
        Assert.True(file.Length > BulkyExifTail);
        Assert.True(stream.BytesRead < 1024, $"Read {stream.BytesRead} of {file.Length} bytes.");
    }

    [Theory]
    [MemberData(nameof(BulkyExifNames))]
    public async Task A_bulky_exif_block_reads_the_same_from_memory_and_from_a_stream_that_cant_seek(string name)
    {
        byte[] file = WithBulkyExif(name, width: 40, height: 30, orientation: 8);

        IdentifyResult fromMemory = Image.Identify(file);
        using var stream = new CountingStream(file, canSeek: false);
        IdentifyResult fromStream = await Image.IdentifyAsync(stream, cancellationToken: Token);

        Assert.True(fromMemory.Succeeded, fromMemory.Message);
        Assert.True(fromStream.Succeeded, fromStream.Message);
        Assert.Equal(8, fromMemory.Info.Orientation);
        Assert.Equal(8, fromStream.Info.Orientation);
    }

    [Fact]
    public async Task An_ifd_far_into_the_block_is_still_found()
    {
        // IFD0 placed after 2 KB of padding: past the first read, so the reader has to read on to reach it.
        byte[] exif = Exif(orientation: 3, ifdOffset: 2048, tail: 0);
        byte[] file = Jpeg(40, 30, exif);

        using var stream = new CountingStream(file, canSeek: true);
        IdentifyResult result = await Image.IdentifyAsync(stream, cancellationToken: Token);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(3, result.Info.Orientation);
    }

    private static byte[] WithBulkyExif(string name, int width, int height, int orientation)
    {
        byte[] exif = Exif(orientation, ifdOffset: 8, tail: BulkyExifTail);
        return name switch
        {
            "jpeg" => Jpeg(width, height, exif),
            "png" => Png(width, height, exif),
            "webp" => Webp(width, height, exif),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
    }

    /// <summary>A little-endian TIFF block whose IFD0 holds only the orientation, followed by <paramref name="tail"/> bytes.</summary>
    private static byte[] Exif(int orientation, int ifdOffset, int tail)
    {
        byte[] block = new byte[ifdOffset + 2 + 12 + 4 + tail];
        "II*\0"u8.CopyTo(block);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), (uint)ifdOffset);
        Span<byte> ifd = block.AsSpan(ifdOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(ifd, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(ifd[2..], 0x0112);
        BinaryPrimitives.WriteUInt16LittleEndian(ifd[4..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(ifd[6..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(ifd[10..], (ushort)orientation);
        block.AsSpan(ifdOffset + 18).Fill(0xA5);
        return block;
    }

    private static byte[] Jpeg(int width, int height, byte[] exif)
    {
        var file = new List<byte> { 0xFF, 0xD8 };
        int app1Length = 2 + 6 + exif.Length;
        file.AddRange([0xFF, 0xE1, (byte)(app1Length >> 8), (byte)app1Length]);
        file.AddRange("Exif\0\0"u8.ToArray());
        file.AddRange(exif);
        file.AddRange([0xFF, 0xC0, 0x00, 0x0B, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x01, 0x01, 0x11, 0x00]);
        file.AddRange([0xFF, 0xD9]);
        return [.. file];
    }

    private static byte[] Png(int width, int height, byte[] exif)
    {
        var file = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        AddPngChunk(file, "IHDR"u8, ihdr);
        AddPngChunk(file, "eXIf"u8, exif);
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

    /// <summary>An extended (VP8X) WebP: the lossless image data, then the EXIF chunk, as most writers order them.</summary>
    private static byte[] Webp(int width, int height, byte[] exif)
    {
        var body = new List<byte>();
        byte[] vp8x = new byte[10];
        vp8x[0] = 0x08; // EXIF flag
        WriteUInt24(vp8x.AsSpan(4), width - 1);
        WriteUInt24(vp8x.AsSpan(7), height - 1);
        AddRiffChunk(body, "VP8X"u8, vp8x);

        uint bits = (uint)(width - 1) | ((uint)(height - 1) << 14);
        AddRiffChunk(body, "VP8L"u8, [0x2F, (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24), 0x00]);
        AddRiffChunk(body, "EXIF"u8, exif);

        byte[] file = new byte[12 + body.Count];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(file.Length - 8));
        "WEBP"u8.CopyTo(file.AsSpan(8));
        body.CopyTo(file, 12);
        return file;
    }

    private static void AddRiffChunk(List<byte> body, ReadOnlySpan<byte> fourCc, ReadOnlySpan<byte> data)
    {
        body.AddRange(fourCc.ToArray());
        byte[] size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
        body.AddRange(size);
        body.AddRange(data.ToArray());
        if (data.Length % 2 == 1)
        {
            body.Add(0);
        }
    }

    private static void WriteUInt24(Span<byte> destination, int value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
    }

    /// <summary>
    /// A stream that counts the bytes handed out, seekable or not. <see cref="MemoryStream"/>'s async reads go through
    /// the sync ones, so counting those counts everything once.
    /// </summary>
    private sealed class CountingStream(byte[] data, bool canSeek) : MemoryStream(data, writable: false)
    {
        public long BytesRead { get; private set; }

        public override bool CanSeek => canSeek && base.CanSeek;

        public override int Read(byte[] buffer, int offset, int count) => Count(base.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(base.Read(buffer));

        public override int ReadByte()
        {
            int value = base.ReadByte();
            if (value >= 0)
            {
                BytesRead++;
            }

            return value;
        }

        private int Count(int read)
        {
            BytesRead += read;
            return read;
        }
    }
}
