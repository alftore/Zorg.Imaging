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
var rest = new MemoryStream();
peek.Stream.CopyTo(rest);
Check("peeked stream whole after identify", rest.ToArray().AsSpan().SequenceEqual(png));
Check("identify not an image", Image.Identify("not an image"u8.ToArray()).Status == IdentifyStatus.NotAnImage);

// Core: the pixel buffer, typed rows for every pixel type, and the format check on GetRow.
using (var image = new Image(4, 3, PixelFormat.Rgba32))
{
    image.GetRow<Rgba32>(2)[3] = new Rgba32(1, 2, 3, 4);
    using Image copy = image.Clone();
    Check("image rgba32 row", copy.GetPixelBytes()[^4..].SequenceEqual(new byte[] { 1, 2, 3, 4 }) && copy.ToString() == "Rgba32 4x3");

    bool refused = false;
    try
    {
        _ = image.GetRow<Bgra32>(0);
    }
    catch (InvalidOperationException)
    {
        refused = true;
    }

    Check("image wrong pixel type refused", refused);
}

using (var bgra = new Image(1, 1, PixelFormat.Bgra32))
using (var rgb = new Image(1, 1, PixelFormat.Rgb24))
using (var grey = new Image(1, 1, PixelFormat.L8))
{
    bgra.GetRow<Bgra32>(0)[0] = new Bgra32(3, 2, 1, 255);
    rgb.GetRow<Rgb24>(0)[0] = new Rgb24(1, 2, 3);
    grey.GetRow<L8>(0)[0] = new L8(9);
    Check("image other pixel types", bgra.GetPixelBytes()[0] == 3 && rgb.Stride == 3 && grey.GetPixelBytes()[0] == 9);
}

// PNG: encode, decode synchronously and through a stream that can't seek, and refuse damage.
{
    using var picture = new Image(40, 30, PixelFormat.Rgba32);
    for (int y = 0; y < picture.Height; y++)
    {
        Span<Rgba32> row = picture.GetRow<Rgba32>(y);
        for (int x = 0; x < row.Length; x++)
        {
            row[x] = new Rgba32((byte)(x * 6), (byte)(y * 8), (byte)(x ^ y), (byte)(255 - x));
        }
    }

    using var encoded = new MemoryStream();
    picture.SaveAsPng(encoded);
    byte[] file = encoded.ToArray();
    using Image decoded = Image.Decode(file);
    Check("png round trip", decoded.PixelFormat == PixelFormat.Rgba32 && decoded.GetPixelBytes().SequenceEqual(picture.GetPixelBytes()));

    using Image streamed = Image.DecodeAsync(new ForwardOnlyStream(file), new DecodeOptions { PixelFormat = PixelFormat.Bgra32 }).AsTask().GetAwaiter().GetResult();
    Check("png decode stream bgra32", streamed.GetRow<Bgra32>(0)[1] == new Bgra32(1, 0, 6, 254));

    byte[] damaged = (byte[])file.Clone();
    damaged[^20] ^= 0xFF;
    bool refused = false;
    try
    {
        Image.Decode(damaged).Dispose();
    }
    catch (ImageFormatException)
    {
        refused = true;
    }

    Check("png damage refused", refused);
}

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
