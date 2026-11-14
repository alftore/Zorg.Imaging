# CMYK

A CMYK file (a four-component JPEG, YCCK included, or a separated TIFF) normally decodes to sRGB: `Image.Decode`
converts the inks through the file's CMYK profile, or by the device formula when it has none. That's what the web
wants, and every operation and encoder works on the result.

Print work wants the inks themselves, for example to write a PDF image as `DeviceCMYK`. `Image.DecodeCmyk` gives
them as a `CmykImage`: four `L8` planes, cyan, magenta, yellow and black, each from 0 (no ink) to 255 (full ink),
with the file's CMYK profile. It returns `null` for an image that isn't CMYK.

```csharp
using CmykImage? inks = Image.DecodeCmyk(jpeg, new DecodeOptions { ShrinkTo = (1200, 1200) });
if (inks is not null)
{
    using CmykImage fitted = inks.Map(plane => plane.Fit(1200, 1200));
    byte[] deviceCmyk = fitted.ToInterleaved();      // C, M, Y, K per pixel, as a PDF image stream holds them
    ReadOnlyMemory<byte> profile = fitted.IccProfile; // for /ICCBased with /N 4, or empty for device CMYK
}
```

- The orientation is applied and `DecodeOptions.ShrinkTo` works as for `Decode`; `DecodeOptions.PixelFormat` doesn't
  apply.
- `Map` runs one operation on each plane: `Fit`, `Resize`, `Crop`, `Rotate`, `Flip...`. The planes are in
  `ColorSpace.LinearSrgb`, so resizing blends ink amounts directly, as print software does, with no gamma.
- `ToSrgb` converts back for the screen, through the profile when there is one: the same pixels `Decode` gives.

There is no CMYK pixel format: the effects and encoders work on RGB, so a CMYK picture stays four planes until it's
written elsewhere (as a PDF does) or converted with `ToSrgb`.

A 4000 x 3000 CMYK JPEG decodes to its inks in the same time as to sRGB (103 against 99 ms, one core of the
development machine); with shrink-on-load, a fit to 1200 pixels and interleaving, 122 ms.
