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
