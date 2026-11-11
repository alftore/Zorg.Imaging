# Zorg.Imaging

Managed image decoding, resizing and encoding for .NET 10, written in C# only, with no native binaries and no dependencies.

**Early releases: the API may still change before 1.0.**

- **Formats:** PNG, JPEG, WebP (lossy and lossless) and GIF, decoded and encoded. BMP and TIFF are decoded.
- **Pipeline:** `ImagePipeline` loads, fits and saves in one chain. JPEG shrinks on load, so a thumbnail never decodes every pixel.
- **Operations:** `Resize`, `Fit`, `Fill`, `Pad`, `Crop`, `Rotate`, `FlipHorizontal`, `FlipVertical` and `Flatten`. Resizing works in linear light.
- **Effects:** `Blur`, `Sharpen`, `DrawImage` for watermarks, `Trim`, and `ToBlurHash` for placeholders.
- **Animations:** `AnimationReader` and `AnimationWriter` read and write animated GIF and WebP one frame at a time.
- **Smaller PNGs:** `PngEncoderOptions.MaxColors` saves a palette PNG with full alpha.
- **Colour:** colour spaces are kept, and `ToSrgb` converts to sRGB.
- **Metadata:** stripped by default. It can be kept, or kept without personal data.
- **Safe on untrusted files:** limits are checked before anything is allocated.

See the [repository](https://github.com/alftore/Zorg.Imaging) for documentation.
