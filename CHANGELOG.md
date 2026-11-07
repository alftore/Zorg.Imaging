# Changelog

## Unreleased

- Repository set up: solution, library, tests, AOT smoke sample, CI and the Linux gate.
- Added the PNG chunk CRC-32 (internal).
- Added the quick format check, so an upload can be refused before any real work:
  - `ImageFormats.Detect`, `IsSupported`, `DetectFile`, `DetectAsync` and `PeekAsync` recognise PNG, JPEG, GIF and WebP from their first bytes, and also AVIF, HEIF, JPEG XL, TIFF, BMP, ICO, PSD and QOI so they can be reported as unsupported.
  - `Image.Identify` and `Image.IdentifyAsync` read only the headers and return an `IdentifyResult` (never throwing for bad input) with the size, display size after EXIF orientation, alpha, animation, frame count, ICC and EXIF flags. The orientation is read from the start of the EXIF block only, so on a seekable stream a camera photo costs a few hundred bytes.
  - A stream that can't seek, such as a request body, goes through `PeekAsync` once; the stream it returns gets back every byte that `IdentifyAsync`, `DetectAsync` or another `PeekAsync` reads from it, whatever the outcome, so it still holds the whole file for decoding or storing.
  - `ImageLimits` (pixels, frames, header bytes) and `IdentifyOptions`.
- Added the pixel buffer: `Image` with a width, height, `PixelFormat` (`Rgba32`, `Bgra32`, `Rgb24`, `L8`) and pooled pixel memory that is cleared before use, typed rows through `GetRow<T>` (checked against the format), `GetRowBytes`, `GetPixelBytes`, `Clone` and `Dispose`. The pixel structs `Rgba32`, `Bgra32`, `Rgb24` and `L8`.
- Added the exception types: `ImageException`, and under it `ImageFormatException` (corrupt or truncated data), `UnsupportedImageFormatException` and `ImageLimitException`. The header readers already throw them, so decoders will fail with the same messages Identify reports.
- Added PNG decoding: `Image.Decode` and `Image.DecodeAsync` with `DecodeOptions`. Every colour type and bit depth, Adam7 interlacing, `tRNS` transparency, and an APNG's still image. The image comes out as `L8`, `Rgb24` or `Rgba32`, whichever holds the file without loss, or as `Rgba32` or `Bgra32` on request. 16-bit samples round to the nearest 8-bit value. The EXIF orientation is applied unless `ApplyOrientation` is off, so the decoded size matches Identify's display size. The image size is checked against `ImageLimits.MaxPixels` before anything is allocated, inflating stops once the image is full (so a zlib bomb costs no more than the image), and damage (bad CRC, bad filter, short data, missing IEND) throws `ImageFormatException`. Gamma and colour profiles aren't applied yet.
- Added PNG encoding: `image.SaveAsPng` and `SaveAsPngAsync` with `PngEncoderOptions.CompressionLevel` (0 to 9, default 6). 8-bit grey, RGB or RGBA as the image holds it, an adaptive filter per row, no metadata.
