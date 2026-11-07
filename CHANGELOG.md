# Changelog

## Unreleased

- Repository set up: solution, library, tests, AOT smoke sample, CI and the Linux gate.
- Added the PNG chunk CRC-32 (internal).
- Added the quick format check, so an upload can be refused before any real work:
  - `ImageFormats.Detect`, `IsSupported`, `DetectFile`, `DetectAsync` and `PeekAsync` recognise PNG, JPEG, GIF and WebP from their first bytes, and also AVIF, HEIF, JPEG XL, TIFF, BMP, ICO, PSD and QOI so they can be reported as unsupported.
  - `Image.Identify` and `Image.IdentifyAsync` read only the headers and return an `IdentifyResult` (never throwing for bad input) with the size, display size after EXIF orientation, alpha, animation, frame count, ICC and EXIF flags.
  - `ImageLimits` (pixels, frames, header bytes) and `IdentifyOptions`.
