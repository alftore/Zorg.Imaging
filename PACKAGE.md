# Zorg.Imaging

Managed image decoding, resizing and encoding for .NET 10: PNG, JPEG, GIF and WebP, written in C# only, with no native binaries and no dependencies.

**Early development. Nothing has been released. The main API is `ImagePipeline`: load, fit, save, in one chain. The public API so far is the format check (`ImageFormats.Detect`, `Image.Identify`), the pixel buffer (`Image`, `PixelFormat`, 8- and 16-bit pixels), colour spaces (`ColorSpace`), resizing in linear light (`Image.Resize`, `Resampler`), the everyday operations (`Fit`, `Fill`, `Pad`, `Crop`, `Rotate`, `FlipHorizontal`, `FlipVertical`, `Flatten`), PNG decode and encode, JPEG decode (with shrink-on-load in the pipeline) and encode, WebP decode and encode (lossy and lossless), which keep wide-gamut and HDR colour information. GIF comes next.**

See the [repository](https://github.com/alftore/Zorg.Imaging) for documentation.
