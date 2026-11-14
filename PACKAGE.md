# Zorg.Imaging

Managed image decoding, resizing and encoding for .NET 10, written in C# only, with no native binaries and no dependencies. Native AOT and trimming work.

```csharp
using Zorg.Imaging;

await ImagePipeline.Load("photo.jpg")
    .Fit(1200, 1200)
    .SaveAsync("photo.webp");
```

**Early releases: the API may still change before 1.0.**

## Close to the C tools

Measured on one core over about 170 test images against the reference tools:

| Output | Size | Time | Quality |
|---|---|---|---|
| Lossy WebP vs cwebp | 3% smaller | about the same | 1.2 dB closer to the source at cwebp's size |
| JPEG vs cjpeg | 9% smaller | 3.4 times faster | the same |
| Lossless WebP vs cwebp | 1% smaller | 1.6 times faster | lossless |
| PNG vs libvips | 19% smaller | 7 times faster | lossless |
| 256-colour PNG vs pngquant | 2% larger | 2 times faster | 1.5 points better by SSIMULACRA 2 (perceptual) |

JPEG decoding gives the same pixels as libjpeg-turbo. The details are in [docs/performance.md](https://github.com/alftore/Zorg.Imaging/blob/main/docs/performance.md).

## What it does

- **Formats:** PNG, JPEG, WebP (lossy, lossless and near-lossless) and GIF, decoded and encoded. BMP and TIFF are decoded. AVIF, HEIC and JPEG XL are recognised but not decoded.
- **Pipeline:** `ImagePipeline` loads, fits and saves in one chain. JPEG shrinks on load, so a thumbnail never decodes every pixel.
- **Operations:** `Resize`, `Fit`, `Fill`, `Pad`, `Crop`, `Rotate`, `FlipHorizontal`, `FlipVertical` and `Flatten`. Resizing works in linear light.
- **Effects:** `Blur`, `Sharpen`, `DrawImage` for watermarks, `Trim`, and `ToBlurHash` for placeholders.
- **Animations:** `AnimationReader` and `AnimationWriter` read and write animated GIF and WebP one frame at a time.
- **Smaller PNGs:** `PngEncoderOptions.MaxColors` saves a palette PNG with full alpha.

## Safe defaults for the web

- **Untrusted files:** dimensions, frame counts and sizes are checked against limits before anything is allocated, and a bad file throws an `ImageException`, never something unrelated.
- **Metadata** is stripped unless you ask for it. It can be kept, or kept without GPS position and camera details.
- **Orientation** from EXIF is applied.
- **Colour** is never silently shifted: colour spaces are kept, and `ToSrgb` converts to sRGB.

## Documentation

- [The pipeline](https://github.com/alftore/Zorg.Imaging/blob/main/docs/pipeline.md)
- [Animations](https://github.com/alftore/Zorg.Imaging/blob/main/docs/animation.md)
- [Known limits](https://github.com/alftore/Zorg.Imaging/blob/main/docs/limits.md)
- [Changelog](https://github.com/alftore/Zorg.Imaging/blob/main/CHANGELOG.md)
- [Report a security problem](https://github.com/alftore/Zorg.Imaging/security/advisories/new)
