# Zorg.Imaging

Managed image decoding, resizing and encoding for .NET 10: PNG, JPEG, GIF and WebP, written in C# only, with no native binaries and no dependencies.

**Early development. Nothing has been released. The public API so far is the format check (`ImageFormats.Detect`, `Image.Identify`), the pixel buffer (`Image`, `PixelFormat`, 8- and 16-bit pixels), colour spaces (`ColorSpace`), resizing in linear light (`Image.Resize`, `Resampler`), and PNG decode and encode, which keep wide-gamut and HDR colour information. JPEG, GIF and WebP decoding come next.**

See the [repository](https://github.com/alftore/Zorg.Imaging) for documentation.
