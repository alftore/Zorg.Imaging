# Codecs from other packages

Zorg.Imaging recognises more formats than it decodes: AVIF, HEIC and JPEG XL are recognised (`ImageFormats.Detect`,
`Image.Identify`) but not decoded. Another package can add a decoder for one of them by subclassing `ImageCodec`. The
app then adds it, either for one call or for the whole app:

```csharp
// For one call.
var options = new DecodeOptions { Codecs = ImageCodecs.Empty.Add(heicCodec) };
using Image photo = Image.Decode(bytes, options);

// For the whole app, once at start-up, before the first image is read.
ImageDefaults.Codecs = ImageCodecs.Empty.Add(heicCodec, avifCodec);
```

Nothing is discovered automatically: a codec runs only if the app adds it. That keeps trimming and Native AOT working,
and means an app never runs a decoder it didn't choose.

`DecodeOptions.Codecs` and `IdentifyOptions.Codecs` default to `ImageDefaults.Codecs`. `ImageDefaults.Codecs` can be set
until the first image is read with it, then it's fixed. A library should pass the options instead of setting it.

`ImagePipeline` and `AnimationReader` use the codecs in the decode options. An animation from a codec reads as one
still frame.

## Zorg.Imaging.SystemCodecs

The first such package decodes AVIF and HEIC with the operating system's own decoders, for desktop apps on Windows and
macOS:

```csharp
ImageDefaults.Codecs = SystemCodecs.All; // SystemCodecs.Heif and SystemCodecs.Avif
```

- **Windows:** the Windows Imaging Component's HEIF decoder, from the Microsoft Store's HEIF Image Extension, with HEVC
  Video Extensions for HEIC or the AV1 Video Extension for AVIF. It's created by its class ID, so no other installed
  WIC codec sees the file.
- **macOS:** ImageIO; HEIC from 10.13, AVIF from 13. The pixels keep the file's colour space.
- **Elsewhere,** and on Windows Server, the codecs report themselves unavailable.

It reads the file's boxes in managed code first (the primary image's size, crop, rotation, mirror, alpha, bit depth,
colour and the number of tiles), so the limits apply before the system decoder runs: both the stored size and the size as
shown. HEIF's crop (`clap`), turn (`irot`) and mirror (`imir`) are part of the image, so `Identify` reports the size as
shown with orientation 1, and decoding returns the picture that way (WIC applies them itself; on macOS the codec applies
what ImageIO leaves). It decodes at full size, and the first image only.

Alpha comes from the file's alpha plane. If the system can't read that plane (Windows can't for some small AVIF test
files), decoding fails with `UnsupportedImageFormatException` rather than returning an opaque picture.

### Sample files

None are in the repository. `scripts/fetch-heif-samples.sh` fetches a few into `.cache/corpora/heif` (gitignored),
checked against pinned SHA-256s, and the tests in `tests/Zorg.Imaging.SystemCodecs.Tests` decode them when they're there:

- [libavif's test images](https://github.com/AOMediaCodec/libavif/tree/main/tests/data): many AVIF variants (grids,
  alpha, transforms, ICC, 10-bit), mostly under libavif's BSD-2-Clause licence; its
  [README](https://github.com/AOMediaCodec/libavif/blob/main/tests/data/README.md) gives each file's licence.
- [libheif's examples](https://github.com/strukturag/libheif/tree/master/examples): one HEIC and one AVIF photo, MIT.
- [Link-U's AVIF samples](https://github.com/link-u/avif-sample-images): a larger AVIF set, mostly CC BY-SA 4.0.
- [Nokia's HEIF conformance files](https://github.com/nokiatech/heif_conformance): the reference HEIF set; check its
  licence before using it.
- A phone: iPhones and many Android phones save photos as HEIC.

`ZORG_HEIF_SAMPLES` points the tests at another folder, and `ZORG_REQUIRE_HEIF_SAMPLES=1` turns a missing file into a
failure (the macOS CI job sets it).

## What the library does around a codec

These checks run for every codec, so the [guarantees](../README.md) hold as far as managed code can make them:

1. The format is recognised from the first bytes by the library itself. A codec is used only for a format the library
   doesn't decode; `ImageCodecs.Add` refuses one for PNG, JPEG, GIF, WebP, BMP or TIFF. For a format, the first codec
   whose `Availability` says it can run here wins.
2. `ReadHeader` gets at most `ImageLimits.MaxHeaderBytes` from the start of the file. The size it returns is checked
   against `ImageLimits.MaxPixels` before anything else happens.
3. To decode, the library reads the whole file, at most `ImageLimits.MaxFileBytes` (256 MiB by default), and calls
   `Decode` with it and the header.
4. The image must be the size the header gave (or smaller, when `CodecContext.ShrinkTo` allowed it) and in the pixel
   format asked for. Otherwise the call fails with `ImageCodecException`.
5. The library applies the orientation from the header, so codecs return pixels as stored.
6. Any exception from a codec other than an `ImageException` or `OperationCanceledException` becomes an
   `ImageCodecException` naming the codec, with the original as its inner exception. `Image.Identify` reports it as
   `IdentifyStatus.CodecFailed` and still never throws.
7. A codec that isn't available makes decoding fail with `UnsupportedImageFormatException` carrying its reason, such
   as "HEIC decoding needs the HEIF Image Extension and HEVC Video Extensions from the Microsoft Store."

## Writing a codec

```csharp
public sealed class MyHeicCodec : ImageCodec
{
    public override ImageFormat Format => ImageFormat.Heif;

    public override string Name => "HEIC (my decoder)";

    public override bool UsesNativeCode => true;

    public override CodecAvailability Availability =>
        OperatingSystem.IsMacOS() ? CodecAvailability.Available : CodecAvailability.Unavailable("HEIC decoding needs macOS.");

    public override ImageInfo ReadHeader(ReadOnlySpan<byte> head, CodecContext context)
    {
        (int width, int height, int orientation) = ParseBoxes(head); // throw ImageFormatException if damaged
        return new ImageInfo(ImageFormat.Heif, width, height) { Orientation = orientation };
    }

    public override Image Decode(ReadOnlyMemory<byte> data, ImageInfo header, CodecContext context)
    {
        PixelFormat format = context.PixelFormat == PixelFormat.None ? PixelFormat.Rgba32 : context.PixelFormat;
        var image = new Image(header.Width, header.Height, format);
        // Fill image.GetRow<Rgba32>(y) ... and set image.ColorSpace from the file's colour profile.
        return image;
    }
}
```

The rules for a codec:

- **Synchronous.** `Image.Decode(byte[])` must never wait on another thread, so both methods are synchronous. Don't
  block on async work inside them; on a desktop app's UI thread that can deadlock.
- **Thread-safe and stateless.** One instance is shared by every call.
- **Within the limits.** Allocate nothing beyond `context.Limits` (for example, check tile counts against them), and
  check `context.CancellationToken` between steps.
- **Read the header from the file's own boxes,** not by asking a native decoder to open the whole file, so a hostile
  file is refused before native code sees it.
- **Damaged files throw `ImageFormatException`** (or `ImageLimitException` for a limit). Anything else is treated as a
  bug in the codec.
- **Keep the colour.** Set `image.ColorSpace` from the file's profile rather than converting to sRGB.

## Native code

A codec that calls native code, such as the operating system's decoder, should say so with `UsesNativeCode`. The
library's checks run before the native code sees the file, but they can't make the native code itself safe: a memory
bug in it can crash the process, and a native call can't be cancelled or timed out. Document that for your users.
