# Known limits

These are the formats and features Zorg.Imaging doesn't handle yet. A file that needs one fails with
`UnsupportedImageFormatException`, whose message names what's missing. It never fails with an unrelated exception type.

## Formats

- **AVIF, HEIC and JPEG XL** are recognised (`ImageFormats.Detect`, `Image.Identify`) but not decoded. HEIC may come
  later as an opt-in native add-on, because of HEVC patents.
- **BMP and TIFF** are decoded, not encoded.
- **Animations:** `Image.Decode` reads the first frame. Animated GIF and WebP are read and written frame by frame with
  `AnimationReader` and `AnimationWriter`. APNG gives its first frame only.

## JPEG

- Decoded: baseline and progressive, 8-bit, greyscale, YCbCr, RGB, CMYK and YCCK (converted to sRGB), with any
  whole-number chroma subsampling.
- Not decoded: arithmetic coding, 12-bit, lossless and hierarchical JPEG, a height given after the image data (DNL
  marker), and chroma sampling that isn't a whole fraction of the image size.
- Encoded: 8-bit baseline or progressive.

## TIFF

- Decoded: the first page; strips or tiles, samples together or in planes; no compression, LZW, Deflate or PackBits,
  with or without the horizontal predictor; 8 or 16 bits per sample (1, 2 or 4 for greyscale and palette);
  greyscale, palette, RGB and CMYK; associated or unassociated alpha.
- Not decoded: BigTIFF, JPEG, CCITT fax, JPEG 2000 and WebP compression, the floating-point predictor, YCbCr and
  CIE Lab, signed or floating-point samples, other bit depths, and different bit depths per sample. Further pages are
  ignored.

## BMP and PNG

- BMP: 64-bit pixels aren't supported.
- PNG: Apple's CgBI variant (from iOS app bundles) isn't supported.

## Limits on input

Every decoder checks these against the headers before it allocates. A file over a limit fails with
`ImageLimitException`. Set them with `ImageLimits` on `DecodeOptions` or `IdentifyOptions`.

| Limit | Default |
|---|---|
| `MaxPixels` (width × height, or the animation canvas) | 268,402,689 (16,383 × 16,383, as sharp) |
| `MaxFrames` | 10,000 |
| `MaxHeaderBytes` (headers read before the pixels) | 1 MiB |

A server that takes uploads should lower `MaxPixels` to what it actually needs.
