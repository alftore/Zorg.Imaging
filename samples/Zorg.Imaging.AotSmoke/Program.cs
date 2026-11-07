using System;
using System.Buffers.Binary;
using System.IO;
using Zorg.Imaging;
using Zorg.Imaging.Codecs.Png;

// Published with Native AOT and executed in CI. Trimming keeps only what's used, so every
// codec and operation has to be exercised here: a clean publish alone proves little.
int failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
    {
        failures++;
    }
}

Check("png crc-32 check value", Crc32.Compute("123456789"u8) == 0xCBF43926u);

// Format check, level 1: one signature per built-in format, and one recognised but unsupported.
Check("detect png", ImageFormats.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) == ImageFormat.Png);
Check("detect jpeg", ImageFormats.Detect([0xFF, 0xD8, 0xFF, 0xE0]) == ImageFormat.Jpeg);
Check("detect gif", ImageFormats.Detect("GIF89a"u8) == ImageFormat.Gif);
Check("detect webp", ImageFormats.Detect("RIFF\0\0\0\0WEBP"u8) == ImageFormat.Webp);
Check("detect tiff unsupported", ImageFormats.Detect("II*\0"u8) is { IsSupported: false } tiff && tiff == ImageFormat.Tiff);
Check("detect text is null", ImageFormats.Detect("hello, world"u8) is null);

// Format check, level 2: a 3x2 PNG header, from memory and from a stream that can't seek.
byte[] png = TinyPng(3, 2);
IdentifyResult fromBytes = Image.Identify(png);
Check("identify png bytes", fromBytes.Succeeded && fromBytes.Info.Width == 3 && fromBytes.Info.Height == 2);

FormatPeek peek = ImageFormats.PeekAsync(new ForwardOnlyStream(png)).AsTask().GetAwaiter().GetResult();
IdentifyResult fromStream = Image.IdentifyAsync(peek.Stream).AsTask().GetAwaiter().GetResult();
Check("identify png peeked stream", peek.Format == ImageFormat.Png && fromStream.Succeeded && fromStream.Info.Height == 2);
Check("identify not an image", Image.Identify("not an image"u8.ToArray()).Status == IdentifyStatus.NotAnImage);

return failures == 0 ? 0 : 1;

static byte[] TinyPng(int width, int height)
{
    byte[] file = new byte[8 + 25 + 12 + 12];
    Span<byte> span = file;
    ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    signature.CopyTo(span);
    WriteChunk(span[8..], "IHDR"u8, [0, 0, 0, (byte)width, 0, 0, 0, (byte)height, 8, 6, 0, 0, 0]);
    WriteChunk(span[33..], "IDAT"u8, []);
    WriteChunk(span[45..], "IEND"u8, []);
    return file;
}

static void WriteChunk(Span<byte> destination, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
{
    BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)data.Length);
    type.CopyTo(destination[4..]);
    data.CopyTo(destination[8..]);
    BinaryPrimitives.WriteUInt32BigEndian(destination[(8 + data.Length)..], Crc32.Compute(destination.Slice(4, 4 + data.Length)));
}

/// <summary>A stream that can't seek, like a request body.</summary>
internal sealed class ForwardOnlyStream(byte[] data) : MemoryStream(data)
{
    public override bool CanSeek => false;
}
