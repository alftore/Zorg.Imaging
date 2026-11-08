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

// Colour: a 16-bit Display P3 image keeps its samples and its colour space through PNG.
{
    using var wide = new Image(5, 4, PixelFormat.Rgba64) { ColorSpace = ColorSpace.DisplayP3 };
    wide.GetRow<Rgba64>(3)[4] = new Rgba64(65535, 1234, 7, 40000);
    using var encoded = new MemoryStream();
    wide.SaveAsPng(encoded);
    using Image decoded = Image.Decode(encoded.ToArray());
    Check(
        "png 16-bit display p3 round trip",
        decoded.PixelFormat == PixelFormat.Rgba64
            && decoded.ColorSpace == ColorSpace.DisplayP3
            && decoded.GetRow<Rgba64>(3)[4] == new Rgba64(65535, 1234, 7, 40000));
    Check("colour space from cicp", ColorSpace.FromCicp(9, 16) == ColorSpace.Rec2020Pq && ColorSpace.Rec2020Pq.IsHdr);
}

// Resize: shrinking blends in linear light (black and white make sRGB 188), every filter and a 16-bit image.
{
    using var pair = new Image(2, 1, PixelFormat.Rgb24);
    pair.GetRow<Rgb24>(0)[1] = new Rgb24(255, 255, 255);
    using Image half = pair.Resize(1, 1);
    Check("resize linear light", half.GetRow<Rgb24>(0)[0] == new Rgb24(188, 188, 188));

    using var wide = new Image(30, 20, PixelFormat.Rgba64) { ColorSpace = ColorSpace.DisplayP3 };
    wide.GetPixelBytes().Fill(0x80);
    bool same = true;
    foreach (Resampler filter in new[] { Resampler.Nearest, Resampler.Bilinear, Resampler.CatmullRom, Resampler.Mitchell, Resampler.Lanczos3 })
    {
        using Image smaller = wide.Resize(7, 0, filter);
        using Image larger = wide.Resize(0, 45, filter);
        same &= smaller.Height == 5 && larger.Width == 68 && smaller.ColorSpace == ColorSpace.DisplayP3
            && smaller.GetRow<Rgba64>(4)[6] == new Rgba64(0x8080, 0x8080, 0x8080, 0x8080)
            && larger.GetRow<Rgba64>(44)[67] == new Rgba64(0x8080, 0x8080, 0x8080, 0x8080);
    }

    Check("resize every filter keeps a flat colour", same);
}

// Operations: crop, fit, fill, pad, rotate, flip and flatten.
{
    using var photo = new Image(40, 30, PixelFormat.Rgba32);
    photo.GetRow<Rgba32>(0)[0] = new Rgba32(9, 8, 7, 255);
    using Image corner = photo.Crop(0, 0, 2, 2);
    using Image fitted = photo.Fit(20, 20);
    using Image filled = photo.Fill(10, 10);
    using Image padded = photo.Pad(50, 50, new Rgba32(255, 255, 255));
    using Image turned = photo.Rotate(90);
    using Image across = photo.FlipHorizontal();
    using Image mirrored = across.FlipVertical();
    using Image flat = photo.Flatten(new Rgba32(255, 255, 255));
    Check(
        "operations",
        corner.GetRow<Rgba32>(0)[0] == new Rgba32(9, 8, 7, 255)
            && (fitted.Width, fitted.Height) == (20, 15)
            && (filled.Width, filled.Height) == (10, 10)
            && padded.GetRow<Rgba32>(0)[0] == new Rgba32(255, 255, 255)
            && turned.GetRow<Rgba32>(0)[29] == new Rgba32(9, 8, 7, 255)
            && mirrored.GetRow<Rgba32>(29)[39] == new Rgba32(9, 8, 7, 255)
            && flat.PixelFormat == PixelFormat.Rgb24 && flat.GetRow<Rgb24>(1)[1] == new Rgb24(255, 255, 255));
}

// Pipeline: load from a stream that can't seek, fit, flatten and save, then read the result back.
{
    using var photo = new Image(64, 48, PixelFormat.Rgba32) { ColorSpace = ColorSpace.DisplayP3 };
    photo.GetPixelBytes().Fill(0x60);
    using var original = new MemoryStream();
    photo.SaveAsPng(original);
    using var output = new MemoryStream();
    ImagePipeline.Load(new ForwardOnlyStream(original.ToArray()))
        .Fit(16, 16)
        .Flatten(new Rgba32(255, 255, 255))
        .SaveAsPngAsync(output)
        .AsTask().GetAwaiter().GetResult();
    using Image result = Image.Decode(output.ToArray());
    Check(
        "pipeline",
        result.PixelFormat == PixelFormat.Rgb24 && (result.Width, result.Height) == (16, 12)
            && result.ColorSpace == ColorSpace.DisplayP3 && result.GetRow<Rgb24>(11)[15] == new Rgb24(0xC3, 0xC3, 0xC3));
}

// JPEG: decode a 4:2:0 file as libjpeg-turbo does (within rounding), at full size and shrunk on load by the pipeline.
{
    using Stream resource = typeof(ForwardOnlyStream).Assembly.GetManifestResourceStream("s420.jpg")!;
    using var copy = new MemoryStream();
    resource.CopyTo(copy);
    byte[] jpeg = copy.ToArray();
    using Image decoded = Image.Decode(jpeg);
    Rgb24 first = decoded.GetRow<Rgb24>(0)[0];
    Rgb24 last = decoded.GetRow<Rgb24>(44)[66];
    static bool Near(Rgb24 a, int r, int g, int b) => Math.Abs(a.R - r) <= 4 && Math.Abs(a.G - g) <= 4 && Math.Abs(a.B - b) <= 4;
    Check("jpeg decode", (decoded.Width, decoded.Height) == (67, 45) && Near(first, 125, 204, 87) && Near(last, 19, 60, 46));

    using Image thumb = ImagePipeline.Load(new ForwardOnlyStream(jpeg)).Fit(16, 16).ToImageAsync().AsTask().GetAwaiter().GetResult();
    Check("jpeg shrink on load", (thumb.Width, thumb.Height) == (16, 11) && thumb.PixelFormat == PixelFormat.Rgb24);

    // Encode: re-encode with a Display P3 profile, read it back, and check the colours and the profile survive.
    decoded.ColorSpace = ColorSpace.DisplayP3;
    using var encoded = new MemoryStream();
    decoded.SaveAsJpeg(encoded, new JpegEncoderOptions { Quality = 95, ChromaSubsampling = JpegChromaSubsampling.Yuv444 });
    using Image again = Image.Decode(encoded.ToArray());
    Rgb24 a = again.GetRow<Rgb24>(20)[30];
    Rgb24 b = decoded.GetRow<Rgb24>(20)[30];
    Check(
        "jpeg encode",
        (again.Width, again.Height) == (67, 45) && Math.Abs(a.R - b.R) <= 8 && Math.Abs(a.G - b.G) <= 8 && Math.Abs(a.B - b.B) <= 8
            && again.ColorSpace.IccProfile.Length > 128);
}

// WebP: a lossless file with alpha decodes exactly as libwebp's dwebp decodes it.
{
    using Stream resource = typeof(ForwardOnlyStream).Assembly.GetManifestResourceStream("lossless-alpha.webp")!;
    using var copy = new MemoryStream();
    resource.CopyTo(copy);
    using Image webp = Image.Decode(copy.ToArray());
    Check(
        "webp lossless decode",
        webp.PixelFormat == PixelFormat.Rgba32 && (webp.Width, webp.Height) == (33, 17)
            && webp.GetRow<Rgba32>(0)[0] == new Rgba32(171, 216, 49, 0) && webp.GetRow<Rgba32>(16)[32] == new Rgba32(107, 15, 226, 247));

    // Lossy with alpha, also exactly as dwebp decodes it.
    using Stream lossyResource = typeof(ForwardOnlyStream).Assembly.GetManifestResourceStream("lossy-alpha.webp")!;
    using var lossyCopy = new MemoryStream();
    lossyResource.CopyTo(lossyCopy);
    using Image lossy = Image.Decode(lossyCopy.ToArray());
    Check(
        "webp lossy decode",
        lossy.PixelFormat == PixelFormat.Rgba32 && (lossy.Width, lossy.Height) == (33, 17)
            && lossy.GetRow<Rgba32>(5)[7] == new Rgba32(131, 206, 147, 54) && lossy.GetRow<Rgba32>(16)[32] == new Rgba32(114, 13, 214, 247));

    // Encode lossless at every effort and read it back exactly, with a Display P3 profile.
    webp.ColorSpace = ColorSpace.DisplayP3;
    bool exact = true;
    for (int effort = 0; effort <= 6; effort++)
    {
        using var written = new MemoryStream();
        webp.SaveAsWebp(written, new WebpEncoderOptions { Lossless = true, Effort = effort });
        using Image back = Image.Decode(written.ToArray());
        exact &= back.GetPixelBytes().SequenceEqual(webp.GetPixelBytes()) && back.ColorSpace.IccProfile.Length > 128;
    }

    Check("webp lossless encode", exact);

    // Lossy, the default: near the original, with the alpha exact.
    using var lossyWritten = new MemoryStream();
    webp.SaveAsWebp(lossyWritten);
    using Image lossyBack = Image.Decode(lossyWritten.ToArray());
    Rgba32 original = webp.GetRow<Rgba32>(8)[16];
    Rgba32 coded = lossyBack.GetRow<Rgba32>(8)[16];
    Check(
        "webp lossy encode",
        Math.Abs(original.R - coded.R) <= 40 && Math.Abs(original.G - coded.G) <= 40 && Math.Abs(original.B - coded.B) <= 40 && original.A == coded.A);
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
