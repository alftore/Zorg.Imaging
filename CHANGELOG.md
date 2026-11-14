# Changelog

## 0.3.0

256-colour PNG and GIF look better: colours are chosen the way the eye judges them, and scored with SSIMULACRA 2.1, a perceptual metric, they are now ahead of pngquant and libvips, at about the same speed as 0.2.0. AVIF and HEIC through `Zorg.Imaging.SystemCodecs` gain thumbnails decoded at reduced size, and their EXIF and XMP.

Added:

- `Zorg.Imaging.SystemCodecs` decodes thumbnails at 1/2, 1/4 or 1/8 size when the pipeline shrinks the picture anyway, as JPEG does: a 48 MP phone photo no longer needs its full-size pixels to make a thumbnail.
- `Zorg.Imaging.SystemCodecs` passes the file's EXIF and XMP on: `image.Metadata` reads them, and encoders keep them only when `MetadataOptions` asks (stripped by default).
- `ImageCodec.AttachMetadata`, for codecs to pass a file's EXIF and XMP on.

Changed:

- 256-colour PNG (`MaxColors`) and GIF choose and match colours in Oklab, a space where distance follows what the eye sees, instead of plain sRGB; colours with transparency are compared as they show over black and over white. Photos score 2 to 3 points better and cut-outs about 1, now ahead of pngquant (+1.5 on average over 168 test images) and libvips (+1.75). PSNR is about 1 dB lower and files 1 to 2% larger. Encoding takes the same time as before on average with dithering and a quarter less without: opaque pictures are faster, pictures with transparency up to a quarter slower (small icons more). The before-and-after numbers are in [docs/performance.md](docs/performance.md).
- Lossless WebP leaves out the image mapping tiles to codes when every tile uses the same ones: a few bytes smaller, same pixels.

Fixed:

- An AVIF or HEIC file whose colour is marked "unspecified" (CICP 2) is read as sRGB; before, saving it as JPEG or WebP failed.

Other:

- Lossy output is now also compared with the C tools by SSIMULACRA 2.1, built into the Linux test container's `metric` target; it agrees with CID22's human ratings as published. Lossy WebP is 9% smaller than cwebp at equal SSIMULACRA 2.

## 0.2.0

Other packages can now add codecs for the formats Zorg.Imaging recognises but doesn't decode, and the first one is here: `Zorg.Imaging.SystemCodecs` decodes AVIF and HEIC through Windows' and macOS's own decoders, for desktop apps. Image output is the same as 0.1.0, and the same on .NET 11 as on .NET 10.

Added:

- Codecs from other packages, for formats the library recognises but doesn't decode (AVIF, HEIC, JPEG XL): subclass `ImageCodec`, and add it through `DecodeOptions.Codecs`, `IdentifyOptions.Codecs` or, once at start-up, `ImageDefaults.Codecs`. The library checks the header against the limits before the codec decodes, checks the result, applies the orientation, and reports a codec's own failures as `ImageCodecException` (`IdentifyStatus.CodecFailed`). See [docs/codecs.md](docs/codecs.md).
- `ImageLimits.MaxFileBytes` (256 MiB by default), for codecs that need the whole file.
- A new package, `Zorg.Imaging.SystemCodecs`: AVIF and HEIC decoding through the operating system (Windows Imaging Component on Windows, ImageIO on macOS), for desktop apps. It reads the file's boxes itself and checks them against the limits before the system decoder sees the file, returns the picture cropped, turned and mirrored as the file says, and keeps its alpha and colour space. Tested on real AVIF and HEIC files on Windows 11 (with the Store extensions) and macOS.
- `ImageInfo` has a public constructor, and its `Orientation`, `HasAlpha`, `HasIccProfile`, `ColorSpace` and `HasExif` can be set when it's created.

Other:

- The package readme on nuget.org has a short example, the comparison with the C tools and links to the documentation.
- Tested on .NET 11: the library still targets .NET 10, and every test passes and every output is byte-identical on the .NET 11 runtime. CI now runs the tests on .NET 11 too.
- GitHub Actions are locked with a workflow lockfile (`.github/workflows/actions.lock`) instead of SHA pins in the workflows.

## 0.1.0

The first release outside preview. The library is the same as 0.1.0-preview.7, which wasn't published on nuget.org, so this release also has everything in preview.7's notes below. What's new is around it:

- The repository is public. Issues are welcome through the issue forms. Pull requests are open to collaborators only, as [CONTRIBUTING.md](CONTRIBUTING.md) explains.
- [docs/limits.md](docs/limits.md) lists what isn't supported yet and the default input limits.
- Packages are published from GitHub Actions with NuGet trusted publishing, and each release has a build provenance attestation. [SECURITY.md](SECURITY.md) says how to verify one.

## 0.1.0-preview.7

Lossy WebP encoding is faster with the same quality: 0.95 times cwebp's time on one core over 167 test images (0.99 in preview.6), 1.16 dB closer to the source at cwebp's file size.

- WebP TM-mode block prediction runs a row at a time in vector lanes, in the decoder and the encoder: the same output, lossy encoding 1.5 to 3% faster.
- Lossy WebP makes its ten 4 x 4 predictions from one table of edge averages with byte shuffles: the same output, about 1.5% faster on photos.
- Lossy WebP gives photos one round of its colour fitting instead of two: 3 to 5% less time, quality within 0.02 dB either way at equal size. Graphics keep two.
- Lossy WebP skips the subblock search for macroblocks a whole-block mode already rebuilds exactly: game screenshots encode 13% faster and cut-outs 3 to 7%, with the same quality.

## 0.1.0-preview.6

Faster and closer to the C tools. Against cwebp at quality 80 on 167 test images, lossy files are 1.15 dB closer to the source at the same size and encode in 0.99 times cwebp's time on one core; lossless files are 0.6% smaller than cwebp's. JPEG decoding now gives the same pixels as libjpeg-turbo's djpeg at full size. Lossy WebP, lossless WebP, GIF and lossy PNG output differs from preview.5's.

Lossy WebP:

- Mode choices weigh how much of each block's texture they keep, not only squared error: at equal file size 0.04 to 0.1 dB higher PSNR and 0.03 to 0.09% higher luma SSIM in every category, for up to 4% more time. Files are up to 1% larger at the same quality.
- Whether a flat graphic is worth trying lossless is decided from an eighth of its rows first: screenshots encode 10 to 30% faster, with the same files.
- Flat areas no longer take on a slight tint from what they are predicted from: the chroma DC errors of flat, opaque blocks are carried to their neighbours until they are coded. A cut-out with large flat grey areas is 1.1 to 2 dB closer to the source at equal size at qualities 50 to 80, icons 0.09 to 0.13 dB (luma SSIM 0.01% lower, as the colour costs a few bytes); photos are unchanged.
- Decoding is 24% faster on one core (a 12-megapixel photo: 279 to 211 ms; 156 to 134 ms on 16 cores), with the same pixels.

Lossless WebP:

- Faster on small images: 256 x 256 textures encode 45% faster and 64 x 64 icons 22 to 36% faster on one core; photos 9%. Large cut-outs are as before.
- The copy search passes over runs of one colour, and code headers are priced more closely when tiles are grouped: icons, textures and cut-outs 0.2 to 1% smaller.
- A copy of 16 pixels or more from the left or the rows just above is taken without searching further back: icons, screenshots, cut-outs and the alpha of lossy cut-outs encode 2 to 18% faster, sizes within 0.25%.
- Smaller files: icons and cut-outs 2.7 to 4.3%, game screenshots 1.5%, photos 0.2%, and the alpha of lossy cut-outs 3 to 3.7%: against cwebp, 512-pixel icons and cut-outs are now 1.6 and 2.4% smaller, small icons and plant cut-outs within 0.6%. Tiles of opaque and soft alpha get their own codes, the colour cache is kept only where it beats the groups alone, and copies are priced from a better first parse; encoding takes 5 to 12% longer (64-pixel icons 25%, 0.5 ms).
- Images of 17 to 256 colours that clearly suit a colour table are coded only that way up to effort 4: up to twice as fast (a 129-colour 1784 x 1862 cut-out 646 to 366 ms, game textures 42% faster), and at most 0.2% larger.

GIF and lossy PNG:

- Colours are 0.1 to 0.16 dB closer to the source on photos and screenshots, from 12 rounds of k-means instead of 4, and encoding is 20 to 43% faster on screenshots, cut-outs and icons (up to 10% slower on photos): the colour search cache is built as needed instead of 8 MB up front, and colours are compared eight at a time.

PNG and JPEG:

- PNG's default filter choice also tries no filter: game and web screenshots and cut-outs 1.5 to 2% smaller, others unchanged.
- JPEG decoding uses libjpeg's integer inverse DCT and rounds chroma upsampling as libjpeg does, so a full-size decode is byte for byte what libjpeg-turbo's djpeg gives (before, 7 to 8% of bytes were 1 to 4 levels off), and is faster: a 12-megapixel photo 70 to 54 ms on one core, 34 to 28 ms on 16; at 1/2 scale 70 to 44 ms.
- JPEG decoding clears coefficients as it goes and reads luma rows in place: the same pixels, with steadier one-core times (median 102 to 70 ms on a 12-megapixel photo). WebP and GIF decoding write 8-bit output four pixels at a time.

## 0.1.0-preview.5

Mostly WebP. Against cwebp at quality 80 on 167 test images, lossy files are 0.78 dB closer to the source at the same size and encode in about the same time on one core; lossless files are within 1% of cwebp's size (see `docs/performance.md`). Lossy WebP, GIF and lossy PNG output differs from preview.4.

Added:

- `WebpEncoderOptions.NearLossless`, as cwebp's `-near_lossless`: lossless coding that lets colours move by up to half a step where the picture is busy, with `Quality` setting the step. Flat areas, gradients and alpha stay exact. Files are 17 to 34% smaller than lossless; screenshots are smaller than cwebp's at 10 dB higher PSNR.
- `WebpEncoderOptions.Exact`, as cwebp's `-exact`. By default WebP no longer spends bytes on the colour of fully transparent pixels: an 800 x 600 cut-out is 12% smaller lossless.
- `GifEncoderOptions.AlphaThreshold`, `AlphaDithering` and `Matte`, to control how soft alpha becomes GIF's one-bit transparency.

Lossy WebP:

- Colour is converted for what a decoder will show, as cwebp's `-sharp_yuv`, from effort 2 and quality 50: sharper colour edges and coloured text, 0.25 to 0.6 dB on photos at equal size, for about 10% more time.
- Four segments with their own quantisers, from effort 1, as cwebp's noise shaping: smooth areas are coded finer and busy ones coarser, so smooth backgrounds lose their blotches (luma SSIM 0.1 to 0.3% higher at equal size).
- Opaque screenshots and flat graphics are coded lossless when that is no larger, from effort 2: every pixel exact, and 7 of 40 game screenshots came out smaller this way.
- The loop filter strength is chosen per picture (up to 0.45 dB on photos).
- The quality scale matches cwebp's file sizes on photos within 2% from quality 1 to 90. Pictures with transparency are coded as finely as opaque ones, so about 12% larger at a given quality than before.
- Pictures of tens of megapixels are coded once instead of twice, and 10% smaller at the same quality.
- Faster: about 25% on one core and more on many cores, with alpha coded beside the picture and at a lower effort.

Lossless WebP:

- Transforms are chosen by an entropy analysis, and the copy search prefers nearby copies: files went from 4.4% larger than cwebp's to 0.6%, and encoding is up to 15% faster.
- Fixed: alpha masks could be up to 30% larger than needed.

GIF and PNG:

- GIF uses lossy PNG's quantiser, and both count colours exactly when there are at most 65,536 and dither more gently: at 256 colours a photo is 38 dB from the source instead of 33 to 34 (pngquant 37.7). Encoding takes up to twice as long.
- Lossy PNG weighs alpha errors as they show over black and white: a soft cut-out is 2.5 dB closer.
- PNG's default filter choice tries four filters per band of rows: files 1.5 to 10% smaller at the same speed.

## 0.1.0-preview.4

- Fixed lossless WebP encoding writing a file that no decoder can read. It happened at efforts 2 to 4 (4 is the default) on some images that mix photo and flat areas. When every block of the image moved out of one group of codes, the encoder still wrote that group's codes, and the decoder read the rest of the file out of step. Files written by 0.1.0-preview.3 and earlier may have this fault.

- Added TIFF decoding (`Image.Decode`, `Image.Identify`) for a file's first image: strips or tiles, PackBits, LZW or Deflate compression, the horizontal predictor, grey, RGB, palette and CMYK of up to 16 bits, alpha, orientation and ICC profiles. `ImageFormat.Tiff.IsSupported` is now true. JPEG-compressed, YCbCr, floating-point and BigTIFF files report `UnsupportedImageFormatException`.
- Added BMP decoding (`Image.Decode`, `Image.Identify`): Windows and OS/2 bitmaps of 1 to 32 bits per pixel, with palettes, bit fields, RLE8 and RLE4, top-down rows and V5 embedded ICC profiles. `ImageFormat.Bmp.IsSupported` is now true.
- Added lossy PNG: `PngEncoderOptions.MaxColors` (2 to 256) reduces an image to a palette with full alpha, and `Dithering` sets how much dithering to use. On an 800 x 600 photo crop with 256 colours, the file is 38% of the lossless size at 36 dB, or 14% for a cut-out with soft alpha.
- Added effects, on `Image` and as pipeline steps: `Blur` (Gaussian, in linear light), `Sharpen` (unsharp mask), `DrawImage` (draw one image over another, for watermarks, with opacity) and `Trim` (crop away a uniform border). Also `Image.ToBlurHash` for BlurHash placeholder strings.
- Added animations: `AnimationReader` reads every frame of an animated GIF or WebP, already composited, one at a time. `AnimationWriter` writes animated GIF or WebP from whole frames, coding only what changed. Any other image reads as one frame. See docs/animation.md.
- Faster WebP encoding, measured on a 12-megapixel photo on one core:
  - Lossy encoding takes 1.4 s, down from 2.3 s, the same as cwebp. The output is unchanged.
  - Lossless encoding at the default effort takes 3.0 s, down from 5.9 s, and makes a file 1.6% smaller.
  - Lossless effort 2 now makes smaller files than effort 1. Before, its files were larger.
- Faster JPEG encoding on several cores: the 12-megapixel photo takes 27 ms on 16 cores, down from 77 ms. Symbol counting and the entropy-coded scan now run in parallel and produce the same bytes. A cheaper forward DCT rounds rare ties differently, so files differ by a few bytes at the same quality.

## 0.1.0-preview.3

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
- Added colour spaces and 16-bit pixels, so wide-gamut and HDR photos keep their colours:
  - `ColorSpace`: CICP code points (`FromCicp`), an ICC profile kept byte for byte (`FromIccProfile`), and the built-ins `Srgb`, `DisplayP3`, `Rec2020`, `Rec2020Pq`, `Rec2020Hlg` and `LinearSrgb`, with `IsSrgb` and `IsHdr`. Every `Image` has one (`Image.ColorSpace`, sRGB unless a decoder says otherwise); `Clone` and orientation keep it.
  - `PixelFormat.L16`, `Rgb48` and `Rgba64`, with the pixel structs `L16`, `Rgb48` and `Rgba64` (16 bits a channel, machine byte order).
  - PNG decoding reads `cICP`, `iCCP`, `sRGB`, `gAMA` and `cHRM` into the colour space, with the PNG specification's precedence. An ICC profile may inflate to 4 MiB at most. 16-bit files now decode to `L16`, `Rgb48` or `Rgba64` without loss (they rounded to 8 bits before), and `Rgba64` can be asked for.
  - PNG encoding writes 16-bit images as 16-bit PNG, and writes the colour space back: nothing for sRGB, `cICP`, `iCCP`, or `gAMA` and `cHRM` as they were read.
- Added resizing: `image.Resize(width, height, resampler)`, 0 for one side to keep the aspect ratio, with `Resampler.Nearest`, `Bilinear`, `CatmullRom`, `Mitchell` and `Lanczos3` (Lanczos3 when shrinking and Mitchell when enlarging by default). Shrinking blends in linear light with the image's own transfer curve (sRGB, P3, Rec.2020, PQ, HLG) and premultiplied alpha; a flat colour stays exactly the same. Every pixel format, 8 and 16 bits. Memory is a few rows of the new width, whatever the shrink factor. Vectorised, and split into bands of rows that run in parallel on large images.
- Added the everyday operations, each returning a new image with the same colour space: `Crop`; `Fit` (shrink inside a box, never enlarge), `Fill` (cover a box exactly, cropping the centre) and `Pad` (fit, then centre on a background colour); `Rotate` by multiples of 90 degrees, `FlipHorizontal` and `FlipVertical`, all lossless; `Flatten` over a background colour, giving `Rgb24` or `Rgb48`.
- Added `ImagePipeline`, the main API: `ImagePipeline.Load(bytes | stream | path)` or `From(image)`, then `Resize`, `Fit`, `Fill`, `Pad`, `Crop`, `Rotate`, `FlipHorizontal`, `FlipVertical` and `Flatten` as steps, then `ToImageAsync` or `SaveAsPngAsync`. Nothing is read until it runs; each step returns a new, reusable pipeline and checks its arguments at once; images along the way are disposed as soon as they're used. `WithDecodeOptions` sets the limits. A stream is read when the pipeline runs, so that pipeline runs once.
- Added JPEG decoding: baseline, extended sequential and progressive Huffman-coded files, grey, YCbCr and RGB, any whole-number chroma subsampling, restart markers, EXIF orientation and ICC profiles (reassembled from their APP2 chunks, at most 4 MiB). Within a level or two of libjpeg-turbo's own decode. 12-bit, lossless, hierarchical, arithmetic-coded and CMYK files throw `UnsupportedImageFormatException`. At most 256 scans.
- Added shrink-on-load: when a pipeline's first step is `Fit`, `Fill`, `Pad` or `Resize`, a JPEG decodes at 1/2, 1/4 or 1/8 of its size while staying at least twice the target. The reduced transform is the exact area average of the full decode, done in the DCT domain. A 12-megapixel photo becomes a 400-pixel thumbnail in about 40 ms.
- Added JPEG encoding: `image.SaveAsJpeg`, `SaveAsJpegAsync` and the pipeline's `SaveAsJpegAsync`, with `JpegEncoderOptions` (`Quality`, 80 by default on libjpeg's scale; `ChromaSubsampling`, 4:2:0 or 4:4:4; `Background` for transparency, white by default). Baseline JFIF with Huffman tables built for each image, no larger than `cjpeg -optimize`'s files. Grey images become grey files; 16-bit samples are rounded.
- JPEG files carry the colour space as an ICC profile: the image's own, or a generated ICC v4 profile (about 600 bytes) for Display P3, Rec.2020 and other spaces known by their primaries. HDR (PQ, HLG) and unknown spaces throw `NotSupportedException` instead of shifting colours.
- Added lossless WebP decoding (VP8L, RFC 9649): all four transforms, the colour cache, backward references and grouped prefix codes; byte for byte as libwebp decodes it. The extended container too: ICC profile as the colour space, EXIF orientation, and an animation's first frame on a transparent canvas. 
- Added lossy WebP decoding (VP8 key frames, RFC 6386): segments, every intra mode, the normal and simple loop filters, and alpha from an ALPH chunk in every method and filter; byte for byte as libwebp decodes it. The fixed tables are extracted from the RFC's text by `scripts/extract-vp8-tables.py`.
- Added lossless WebP encoding: `image.SaveAsWebp`, `SaveAsWebpAsync` and the pipeline's `SaveAsWebpAsync`, with `WebpEncoderOptions` (`Lossless`, `Effort` 0 to 6 as cwebp's `-m`, `Quality` for lossy). Every pixel format (16-bit samples rounded, grey stored as RGB), alpha kept exactly. Images of 256 colours or fewer use a colour table; others get the predictor and cross-colour transforms, backward references chosen by cost, the colour cache and grouped prefix codes. At the default effort, files are within 0.2% of `cwebp -lossless` on a 12-megapixel photo and smaller on grey and few-colour images. The colour space is written as an ICC profile in a `VP8X` file.
- Added lossy WebP encoding (VP8 key frames), the default for `SaveAsWebp`: quality 80 on a scale fitted to cwebp's, so a quality gives about cwebp's file size, at the same or better PSNR. Each macroblock tries the whole-block and subblock prediction modes by rate and distortion, with token costs from the frame's own probabilities, which are then fitted and written where they pay. Transparency is kept exactly, coded losslessly in an `ALPH` chunk. Macroblock rows are coded as a wavefront across cores; the file is the same on any number of them. A 12-megapixel photo takes about 0.7 s on 16 cores and 3 s on one.
- Added GIF decoding: GIF87a and GIF89a, the first frame drawn on its canvas as a browser shows it (offset or smaller frames, transparency, interlacing, local colour tables, a 0 x 0 screen), as `Rgb24`, or `Rgba32` when any pixel is transparent. The colour space comes from an `ICCRGBG1012` profile. Image data that runs short or goes wrong leaves the rest of the frame transparent, as browsers do; the canvas, each frame and the frame count are checked against `ImageLimits` first. Frames after the first are drawn with their disposal (none, background, previous) by the internal reader that the animation work will use; they match ImageMagick's `-coalesce`.
- Added GIF encoding: `image.SaveAsGif`, `SaveAsGifAsync` and the pipeline's `SaveAsGifAsync`, with `GifEncoderOptions` (`MaxColors`, 2 to 256; `Dithering`, 0 to 1, full by default). Images with few enough colours are stored exactly; others are reduced with Wu's quantiser and Floyd–Steinberg dithering. Alpha below 128 becomes transparent. Non-sRGB colour spaces are written as an ICC profile. A 12-megapixel photo takes 0.3 s (ImageSharp 2.8 s, ImageMagick 3.8 s).
- An APNG now decodes to its first frame as browsers show it: when its default image (IDAT) isn't part of the animation, that image is checked and passed over, and the first `fcTL`/`fdAT` frame is decoded instead, on a transparent canvas when it's smaller.
- Added colour conversion to sRGB, and `ColorSpaceHandling` on every encoder's options (decision D12): `SrgbWhenLossless`, the default for JPEG, WebP and GIF, converts an image in another colour space to sRGB and writes no profile when every pixel fits inside sRGB, and keeps the colour space otherwise, so saturated colours aren't clipped; `Srgb` always converts, clipping; `Keep`, PNG's default, never does. `image.ToSrgb()` converts on request. Display P3, Rec. 2020 and other CICP spaces with an SDR curve, PNG gamma and chromaticities, and ICC matrix/TRC and grey profiles (v2 and v4: `XYZ`, `curv`, `para`) are understood; HDR and LUT-based profiles are kept as they are. Within one step of LittleCMS. A 12-megapixel photo converts in 28 ms on 16 cores, 144 ms on one.
- 8-bit encoding through a transfer curve (resizing in linear light, colour conversion) is faster: one table lookup and one comparison instead of a search, with the same exactly rounded result.
- Added metadata handling: images carry the EXIF and XMP of the file they were decoded from (JPEG APP1, PNG `eXIf` and uncompressed `iTXt` XMP before the image data, WebP `EXIF` and `XMP `), and the JPEG, PNG and WebP encoders write them back only when asked, through `Metadata` on their options. `MetadataOptions.Strip` is the default, because EXIF can reveal where a photo was taken and with which device; `Keep` writes everything; `KeepNonPersonal` drops location and camera details (make, model, lens, serial numbers, owner) and keeps exposure, dates and copyright; `CopyrightOnly` keeps only the artist and copyright; `.Without(ExifGroup.Location | Camera | Dates)` leaves out more. EXIF is rebuilt rather than copied: orientation becomes 1 when the pixels have been turned, pixel dimensions match the saved image, and the thumbnail and maker note are dropped. A damaged block is dropped, never thrown. Checked with exiftool. GIF writes no metadata.
- PNG files are smaller: the encoder writes the smallest layout that holds the image exactly (`PngEncoderOptions.ReduceColorType`, on by default), as oxipng does. Opaque RGBA becomes RGB, grey colour becomes grey, up to 256 colours become a palette (translucent entries first, so `tRNS` is short), 16-bit samples that are exact 8-bit values become 8-bit, grey and palette indices pack into 1, 2 or 4 bits, and a single fully transparent colour becomes a `tRNS` key. With a colour space other than sRGB, grey stays grey and colour stays colour. `CleanTransparentPixels` (off by default) writes hidden colours as transparent black so they share one entry. `Filter` picks the row filter strategy (`PngFilter`: `Adaptive`, the five fixed filters, `MinimumSum`, `Entropy`, `Bigrams`, `BigramEntropy`); `Optimize` tries several filter and zlib strategies on each band at level 9 and keeps the smallest, within 0.3% of `oxipng -o2` on a photo and ahead of it on synthetic images, behind it by up to 13% on palette images, where oxipng's libdeflate compresses better than zlib. ImageMagick reads every layout back to the same pixels.
- PNG encoding uses all cores: rows are filtered and compressed in bands of about 1 MiB joined into one zlib stream, as pigz does (`PngEncoderOptions.MaxDegreeOfParallelism`). The file is the same on any number of cores and no larger than before. A 12-megapixel photo encodes in 134 ms on 16 cores (649 ms before), 689 ms on one.
- Added `ImageDefaults.MaxDegreeOfParallelism`: large images use all cores by default (as many as .NET reports, which respects container limits), and `DecodeOptions.MaxDegreeOfParallelism`, `JpegEncoderOptions.MaxDegreeOfParallelism`, `PngEncoderOptions.MaxDegreeOfParallelism`, `WebpEncoderOptions.MaxDegreeOfParallelism`, `GifEncoderOptions.MaxDegreeOfParallelism` and `ImagePipeline.WithMaxDegreeOfParallelism` override it per call; 1 keeps the work on the calling thread. Results are identical whatever the setting.
- Faster on one core: JPEG decoding about 3x (a 12-megapixel baseline photo in about 80 ms, 37 ms on 16 cores) and lossy WebP decoding about 1.8x, with SIMD through .NET's portable vector types and identical output. `docs/performance.md` has the numbers against libjpeg-turbo and libwebp.
- `ImagePipeline` finished for the first release: `SaveAsync(path)` writes the format the extension names and replaces the file only once the whole result is written; `SaveAsAsync(stream, format)` writes a format chosen at run time, such as from an `Accept` header; `ToSrgb()` is a step. JPEG shrink-on-load now also applies when turns, flips, `Flatten` or `ToSrgb` come before the resize. `SaveAsPngAsync` now honours `WithMaxDegreeOfParallelism` during the encode, as the other formats did, and every format writes an unchanged `From(image)` source without copying it. A guide is in `docs/pipeline.md`.
- Fuzz tests (mutation, first tier) for the JPEG, WebP and GIF decoders, as PNG had: every truncation, every byte set to edge values, corrupted length and size fields, random damage (also when JPEG shrinks on load and for every GIF frame), and damage through a stream; `Image.Identify` is held to never throwing. `ZORG_FUZZ_SCALE` lengthens the hunt. They found one bug, now fixed: a JPEG Huffman table with more short codes than fit wrote past its lookup table and threw `ArgumentOutOfRangeException` instead of `ImageFormatException`.
- CI and the Linux gate check sizes (`scripts/check-size.sh`): the library under 3 MB (428 KiB today), the NativeAOT smoke binary under 6 MB (2.9 MB with the runtime). The shell scripts are now executable in git, which CI needs to run them.
- Compliance tests against third-party corpora (`tests/Zorg.Imaging.ComplianceTests`, fetched by `scripts/fetch-corpora.sh` with pinned checksums, never committed): all 176 PngSuite files (valid ones match ImageMagick, broken ones throw `ImageException`), all 132 libwebp conformance vectors byte for byte against libwebp's published checksums and `dwebp`, and recurser's EXIF orientation photos 0 to 8 decoding upright. The Linux gate runs them.
- Fixed: a WebP lossless file whose header says it has no alpha, but whose pixels are transparent, decoded them as opaque. The header bit is only a hint, and libwebp keeps the alpha the pixels have; now so do we (libwebp-test-data's `bad_palette_index.webp`).
- Baseline JPEG decoding streams: coefficients are decoded and transformed a band at a time instead of being held for the whole frame, so decoding a 12-megapixel photo allocates under 1 MB instead of 34 MB, and a 400 px thumbnail takes 53 ms on one core instead of 99 (30 ms on 16 cores, level with libvips). The reduced inverse DCT used for shrink-on-load is vector code. Output is unchanged.
- Faster lossy WebP: encoding a 12-megapixel photo takes 2.1 s on one core instead of 3.6 s (539 ms on 16 cores), and decoding 343 ms instead of 403, with byte-for-byte the same output.
- Progressive JPEG encoding: `JpegEncoderOptions.Progressive`. The usual ten-scan progression with Huffman tables fitted to each scan; the pixels decode exactly as the baseline file's (checked with djpeg too), and files are 2 to 3% smaller. A 12-megapixel photo encodes in 354 ms on one core (baseline 132) and 159 ms on 16, where the scans are coded in parallel.
- Reading metadata: `Image.Metadata` (`ImageMetadata`) gives the EXIF, XMP and IPTC blocks as the file had them, and the facts most often wanted: camera make, model and lens, date taken with its UTC offset, GPS location (`GeoLocation`), artist, copyright, description, title, keywords and the stored orientation. EXIF first, then IPTC. Damaged blocks give no facts and no errors.
- IPTC: JPEG decoding reads it from the Photoshop APP13 segment, and JPEG encoding writes it back under `MetadataOptions.Keep`, with the groups left out dropping place names and contact details (Location) and dates (Dates); `CopyrightOnly` keeps the by-line and copyright. Stripped by default, as before.
- Lookup-table ICC profiles convert to sRGB: RGB profiles without a matrix and curves, and Lab connection spaces, through their A2B table (lut8, lut16 and the version 4 lutAToB), interpolated tetrahedrally as LittleCMS does, with version 4 perceptual black scaled to black. Within 3 levels of LittleCMS on test profiles of every kind. CMYK profiles are read too, for CMYK JPEG.
- CMYK and YCCK JPEG decoding: converted to sRGB through the file's CMYK ICC profile when it has one (within 4 levels of LittleCMS), otherwise with the device formula R = (1 - C)(1 - K); Adobe-marked files are read inverted, as Photoshop writes them. Shrink-on-load works for them too. They used to throw `UnsupportedImageFormatException`.
- `Image.Identify` can report the colour space decoding will give (`IdentifyOptions.ReadColorSpace`, `ImageInfo.ColorSpace`), from the headers of all four formats: the ICC profile, PNG's cICP, sRGB, gAMA and cHRM, sRGB for CMYK JPEG. Off by default, since a profile can be large.
- PNG `Optimize` compresses harder: each band's best filter is recompressed by an optimal-parsing deflate encoder of our own (Zopfli's approach: every match weighed by its cost in bits, costs refined over several passes, blocks split where that saves bits), and colour rows are tried against a palette. A photo crop comes out 0.8% smaller than `oxipng -o2` (3.2% smaller than level 6); the palette test image went from 13% behind oxipng to 0.7%. About 5 s for 12 megapixels on 16 cores.
- Faster JPEG thumbnails on one core: 43 ms for a 400 px thumbnail of a 12-megapixel photo, from 52 (26 ms on 16 cores). The reduced inverse DCT visits only the non-zero coefficients with precomputed weights, and baseline Huffman decoding keeps its state in locals for a row of MCUs.
