# Performance

Measured on a 12-megapixel photo (4000 x 3000) in every format, made by `scripts/make-bench-files.sh`, with
`benchmarks/Zorg.Imaging.Benchmarks` (BenchmarkDotNet, short job, 8 iterations). One core means
`DOTNET_PROCESSOR_COUNT=1`; all cores is 16 here. The native tools (libjpeg-turbo, libwebp) were timed by the same script
in the Linux gate image, best of three runs, on one core: they are single-threaded C with hand-written SIMD.

The machine runs other work too, so treat single numbers as roughly ±10%; what matters is the ratio to the native
tools and the change between runs made back to back.

## 2026-11-14: 256 colours in Oklab

The 256-colour quantiser (PNG `MaxColors`, GIF) now builds and matches an opaque picture's palette in Oklab. Same
corpus, ours less theirs:

| Format, against | Size | Time | ΔPSNR | ΔS2 before | ΔS2 now |
|---|---:|---:|---:|---:|---:|
| PNG 256 colours, pngquant 256 | 1.023 | 0.52 | +0.04 | -0.41 | +1.28 |
| GIF, libvips `gifsave` | 0.994 | 0.29 | +0.48 | -0.02 | +1.67 |

Photos went from 1 to 2 points behind pngquant to 2.5 to 3.2 ahead; photo textures -0.18 to +2.22. Of 168 images, 90
gained more than 0.3 and 3 lost more (flat game screenshots, up to 1.7); pictures with transparency are unchanged.
Without dithering on either side, pngquant's luma error was 25 to 35% lower than ours and its chroma error 25 to 35%
higher: it spends colours on lightness. A third more time, as the palette is packed tighter in Oklab and the nearest-
colour search has more candidates per cell.

Tried and left out: dithering only flat areas at full strength, or less at edges and in texture (all lower S2 than the
uniform 60%), and weighting each pixel's vote for the palette by how flat its surroundings are (no gain, icons 27%
larger). For PNG compression against oxipng (9.2% larger, at 0.06 of its time): zlib level 9 closes it to 5.8% for
3.7 times the time, Entropy and Bigrams among the adaptive filters 0.7% for 1.6 times, adaptive filtering of palette
rows nothing, and the optimal parser cut to 2 attempts to 3.1% for 36 times. `Optimize` is the way to smaller files:
0.996 of oxipng's size, for about 200 times the default's time.

## 2026-11-13: the corpus scored with SSIMULACRA 2.1

The corpus of the 2026-11-10 section below (168 images; sizes and times unchanged) scored again with SSIMULACRA 2.1, a perceptual
metric (0 to 100; about 90 is visually lossless, a point or less is noise), from the gate image's `metric` target.
Our build agrees with CID22's human ratings as published (4,292 rated images of its validation set, CC BY-SA 4.0: Kendall 0.706, Spearman 0.889, Pearson 0.878, against 0.708, 0.890 and 0.879). ΔS2 is ours less theirs, over white. For lossy WebP, cwebp was also run at qualities 50, 65, 90 and 97, so the size at
equal S2 over that sweep (BD-rate) could be measured.

| Format, against | Size | ΔPSNR | ΔS2 | ΔS2 at equal size | BD-rate (S2) |
|---|---:|---:|---:|---:|---:|
| Lossy WebP q80, cwebp `-q 80` | 0.971 | +1.13 | +2.13 | +2.07 | -9.0% |
| JPEG q80, cjpeg `-quality 80` | 0.913 | +0.00 | +0.22 | | |
| PNG 256 colours, pngquant 256 | 1.013 | +0.84 | -0.41 | | |
| GIF, libvips `gifsave` | 0.985 | +1.27 | -0.02 | | |

- **Lossy WebP** is 9% smaller than cwebp at equal S2. Only photo textures are even (+0.7%); plant cut-outs, behind
  by PSNR at equal size (-0.01 dB), are 2% smaller by S2, so their larger alpha costs size but not quality as seen.
- **256 colours and GIF:** ahead by PSNR, behind by S2 on photos (Kodak -1.0, Wikipedia -1.4, Tecnick -2.2 for PNG),
  and about even on graphics. The dithering adds noise the eye sees, as the SSIM column already hinted.

## 2026-11-11: JPEG decoding as djpeg decodes

The full-size inverse DCT is now libjpeg's integer `jpeg_idct_islow` (djpeg's default), with its constants, scaling,
rounding and range limit, in 16-bit multiply-adds: AVX2 `pmaddwd` for the columns as the butterfly's even and odd
halves, eight columns at a time, and for the rows two at a time, using the same symmetry (about 30 ns a block). Chroma
upsampling now rounds as libjpeg's "fancy" upsampling does: 8 and 7 for the two outputs of a 2 x 2 pair, 1 and 2 (in
quarters) for 2 x 1 and 1 x 2. The 12-megapixel `photo.jpg`, best of 20 runs after 15 warm-up runs, before and after
alternated three times:

| | Before, one core | After, one core | Before, 16 cores | After, 16 cores |
|---|---:|---:|---:|---:|
| JPEG decode, baseline | 69 to 71 ms | 54 to 55 ms | 33 to 35 ms | 28 ms |
| JPEG decode, progressive | 155 to 167 ms | 135 to 140 ms | 130 to 135 ms | 128 to 141 ms |
| JPEG decode at 1/2 scale (steady state) | 70 ms | 44 ms | | |

djpeg takes 30 to 40 ms on one core. Bytes that differ from djpeg's output (`djpeg -pnm`, libjpeg-turbo in the gate
image), over 142 files (14 CID22 photos through cjpeg at quality 50 to 97, 4:2:0, 4:2:2, 4:4:0, 4:4:4, grey and
progressive, and the two benchmark photos) and the 18 JPEGs of the orientation corpus:

| | Before | Integer transform only | After |
|---|---:|---:|---:|
| cjpeg files, full size | 6.89% (at most 4 levels) | 4.14% | 0 |
| of which grey, 4:4:4 | 1.14%, 4.07% | 0, 0 | 0, 0 |
| Orientation corpus, full size | 8.46% | | 0 |
| cjpeg files at 1/2, 1/4, 1/8 | 5.80%, 6.71%, 6.34% | | 4.73%, 6.71%, 6.34% |

So the transform made the grey and 4:4:4 files exact, colour conversion already was, and the rest was the upsampler's
rounding. Reduced sizes are still area averages rather than libjpeg's reduced transforms; at 1/2 the 4:2:0 chroma goes
through the full-size transform, hence the change there. The test fixtures and the cjpeg oracle test now require exact
equality at full size.

## 2026-11-10: decoding on one core

The 12-megapixel photo, `photo-lossy.webp` (cwebp `-q 80`) and `photo.jpg`, decoded to RGB by a small harness, best of
20 runs after 15 warm-up runs, before and after run back to back three times with the machine otherwise idle. The native
tools were timed in the gate image (busybox time, 10 ms resolution). Decoded pixels are unchanged: 1879 decodes (cwebp
and cjpeg variants of 14 CID22 photos and odd-sized crops, the WebP corpus, the test fixtures, JPEG at 1/2, 1/4 and
1/8) hash the same before and after.

| | Before, one core | After, one core | Before, 16 cores | After, 16 cores | Native, one core |
|---|---:|---:|---:|---:|---:|
| WebP lossy decode | 279 ms | 211 ms | 156 ms | 134 ms | dwebp 160 ms (`-mt` 110 ms) |
| JPEG decode, baseline | 71 ms | 70 ms | 34 ms | 34 ms | djpeg 30 to 40 ms |

- **WebP lossy:** the boolean decoder's state stays in locals for a whole block, with the token probabilities looked
  up by position (parsing 125 to 120 ms; a branchless bit update was slower, 129 ms); the 4 x 4 inverse transform in
  32-bit lanes (reconstruction 67 to 50 ms); the loop filter loads and stores rows straight into registers (46 to
  27 ms); the chroma upsampler on eight pairs at a time (colour conversion 28 to 12 ms); ARGB to RGB by byte shuffles.
  Parsing the tokens is now more than half of the one-core time: about 25 million boolean decodes, each a dependent
  multiply, compare and shift, at about 4.5 ns.
- **JPEG baseline:** by profile, the float inverse DCT is 35% of the one-core time, Huffman decoding 23%, chroma
  upsampling and colour conversion 13%. Coefficients are now cleared block by block after the transform, while in
  cache, and luma rows are read in place rather than copied (copying and clearing 5.5% to 2.7% of the profile; the
  best run unchanged, the median one-core run 102 to 70 ms). A wider Huffman lookup (10 or 11 bits) made no difference.
- **Not landed: a 16-bit integer inverse DCT** (pmaddwd, both passes as matrix products, empty rows skipped):
  70 to 64.5 ms on one core and 35 to 30.5 ms on 16, but 1.9% of output bytes change by 1 to 3 levels. For scale,
  today's float transform differs from djpeg in 9.35% of bytes over 140 cjpeg files, the integer prototype in 9.98%.

## 2026-11-10: against the C tools on a test corpus

168 open-licensed images, at most 12 from each category of the QOI benchmark suite (icons, screenshots, photos,
textures, cut-outs; at most 4 megapixels) and the CID22 validation photos, on one core. Made by a corpus runner kept
outside the repository; the reference tools ran in the Linux gate image. Size and time are ours over theirs (geometric
mean); PSNR and SSIM are ours less theirs, over white. Their time is the whole process, start-up and reading the input
included; ours is the better of two encodes. Numbers for preview.7:

| Format, against | Size | Time | ΔPSNR | ΔSSIM | Notes |
|---|---:|---:|---:|---:|---|
| Lossy WebP q80, cwebp `-q 80` | 0.971 | 0.95 | +1.13 | +0.0000 | +1.16 dB at cwebp's file size |
| JPEG q80, cjpeg `-quality 80` | 0.913 | 0.29 | +0.00 | +0.0000 | decoding matches djpeg byte for byte |
| Lossless WebP, cwebp `-lossless` | 0.994 | 0.61 | lossless | | smaller than cwebp on most categories; textures_pk02 4.7% larger |
| PNG, oxipng `-o 2` | 1.092 | 0.06 | lossless | | oxipng searches far longer; `Optimize` closes most of it |
| PNG, libvips `pngsave` | 0.813 | 0.14 | lossless | | |
| PNG 256 colours, pngquant 256 | 1.013 | 0.46 | +0.84 | -0.0028 | photos' SSIM 0.005 to 0.011 behind (dithering) |
| GIF, libvips `gifsave` | 0.985 | 0.26 | +1.27 | -0.0027 | |

Lossy WebP by category, PSNR at cwebp's file size less cwebp's: web screenshots +2.8 dB and game screenshots +0.7
(flat graphics come out lossless where that is smaller), photos +0.02 to +0.25, icons +0.5 to +1.1, cut-outs +0.4,
photo textures -0.07, plant cut-outs -0.16 (their alpha is 3.5% larger than cwebp's). Times run from 0.21 (64-pixel
icons) to 1.6 (web screenshots) of cwebp's.

Tried for lossy WebP and left out: trellis quantisation (+0.1 dB PSNR but 0.1 to 0.17% lower luma SSIM, as it zeroes fine
texture, for 20 to 35% more time), the alpha chunk's prediction filters (alpha 17 to 31% larger than with the lossless
coder's own predictor), alpha-weighted distortion (-0.3 to -0.6 dB), per-segment loop filter levels (at most
+0.01 dB) and a larger luma rounding bias (more SSIM, less PSNR, quality scale moved 2 to 3%). For the palette
quantiser: an error-feedback re-cut and smoothness weighting (both worse than more k-means rounds). For lossy WebP
speed: ranking modes by SATD (Hadamard) instead of squared error (no better choices, 6 to 24% slower), trying fewer
16 x 16, chroma or subblock candidates (6 to 27% faster but 0.02 to 0.13 dB lower) and skipping the subblock search
on near-exact macroblocks (0.2 dB lower); only exact ones are skipped.

## 2026-11-09: lossy PNG

`PngEncoderOptions.MaxColors` reduces to a palette with full alpha (median cut over premultiplied colour cells, then
k-means). 800 x 600 crops of the photo, one core:

| Image | Lossless | 256 colours, dithered | 256, no dithering | 64, no dithering |
|---|---:|---:|---:|---:|
| Photo | 776,228 | 331,695 (34.0 dB, 145 ms) | 293,659 (36.4 dB, 86 ms) | 187,480 (33.1 dB) |
| Cut-out with soft alpha | 916,632 | 194,129 (34.2 dB) | 131,524 (37.7 dB) | 71,130 (34.7 dB) |

PSNR is over premultiplied colour and alpha. Dithering lowers PSNR but hides banding in gradients.

## 2026-11-09: JPEG encoding on many cores

The 12-megapixel photo at quality 80, 4:2:0, timed by the same harness as the WebP section below:

| | One core | 16 cores |
|---|---:|---:|
| Before | 115 ms | 77 ms |
| After | 116 ms | 27 ms |
| cjpeg `-optimize` (gate image) | 120 ms | |

The transform already ran in parallel; counting the symbols and writing the scan didn't. The AC symbols are now
counted per band as each block is made, and the scan is coded in bands of MCU rows as plain bits, each band starting
from the DC values of the band before (all known by then), then joined with the stuffed zeros added: the same bytes
as one pass. The pass down the columns of the forward DCT is split into even and odd butterflies (about 50 vector
operations instead of 128), which rounds rare ties differently: 49 bytes smaller at the same PSNR. None of it moved
the one-core time measurably.

## 2026-11-08: WebP encoding at libwebp's speed

The 12-megapixel photo decoded from `photo.jpg`, on one core (`MaxDegreeOfParallelism = 1`), timed by a small
harness with tiered compilation off, best of a few runs while other sessions shared the machine. cwebp 1.6 was timed in
the gate image on the same pixels.

**Lossy**, quality 80, effort 4: 2.31 s to 1.41 s, with every one of 36 reference encodings byte for byte the same.
cwebp `-q 80` takes 1.4 s.
- Quantisation as vectors, with each divisor turned into an exact multiply and shift.
- Token costs read from tables built from the default coefficient probabilities, which the mode decisions use.
- The subblock mode trials run four candidates at a time, one in each lane, following the scalar integer steps
  exactly (wrap to 16 bits included); the strict tie-break in rank order is kept.
- Whole-block luma and chroma code four blocks at a time in the same way.

**Lossless**, by effort:

| Effort | Before | After | cwebp |
|---|---|---|---|
| 1 | 3.4 s, 14.97 MB | 2.2 s, 14.64 MB | `-z 1`: 2.0 s, 14.42 MB |
| 2 | 14.61 MB | 2.5 s, 14.57 MB | |
| 4 (default) | 5.9 s, 14.80 MB | 3.0 s, 14.56 MB | `-z 3`: 3.0 s, 14.42 MB |
| 5 | | 3.8 s, 14.53 MB | `-z 6`: 6.0 s, 14.43 MB |

- Predictor residuals as vectors, four pixels at a time, equal to the scalar predictor for every mode (tested): 1.4
  to 0.6 s.
- Subtract-green chosen per image by the entropy of left-neighbour differences, as libwebp's analysis does, rather
  than by effort: 1 to 2% smaller.
- The cross-colour search starts from the least-squares slope instead of scanning every 16th multiplier.
- The colour cache size is judged on four strips of a large image: 0.5 to 0.16 s, the same choice on the test photo.
- The match finder hashes runs of three pixels rather than pairs. A photo's residuals are mostly near zero, so pairs
  repeat everywhere and the chains were long; the default effort went from 5.0 to 3.0 s at the same size.
- Effort 2 now takes the cost-based parse with 16-pixel predictor tiles, so it is smaller than effort 1 again.

Sizes stay within about 1% of cwebp's on the photo and the same or smaller on the graphics test images. Deeper
searches find short, far matches that make a photo slightly larger (effort 3 and 4 are no smaller than effort 2 on
it), while graphics need them; a cost model that tells the two apart is still to do.

## 2026-11-08: JPEG thumbnails on one core

Phase timings of the 400 px thumbnail on one core showed Huffman decoding at 23 ms and the reduced inverse DCT at
20 ms. The reduced transform now folds quantisation and both passes into one weight row per coefficient, and visits
only the non-zero coefficients (13 a block on average in the test photo), with the sums for the usual 2 x 2 and 4 x 4
shapes held in registers and the output rounded as vectors: 20 to 14 ms. Sequential scans keep the bit buffer in
locals for a whole row of MCUs instead of going through the reader for every block, and the hot loop drops bounds
checks it provably doesn't need: Huffman 27 to 23 ms.

| BenchmarkDotNet short job | Before, one core | After, one core | Before, 16 cores | After, 16 cores |
|---|---:|---:|---:|---:|
| JPEG to a 400 px thumbnail | 52 ms | 43 ms | 30 ms | 26 ms |
| JPEG to a 400 px WebP | 73 ms | 63 ms | 47 ms | 46 ms |

libvips makes the thumbnail in 29 ms on one core and 32 ms on 16.

## 2026-11-08: optimal deflate for PNG Optimize

`Optimize` now recompresses each band's best filter with our own deflate encoder (`OptimalDeflate`), which weighs
every match length at every position by its cost in bits and refines the costs over several passes, as Zopfli does,
with simple block splitting. It also tries colour rows against a palette when the image fits one.

| Image | Level 6 | Optimize before | Optimize now | oxipng -o2 | oxipng -o4 |
|---|---:|---:|---:|---:|---:|
| 800 x 600 photo crop | 776,228 | | 751,221 | 757,653 | 755,938 |
| 400 x 300 test image, 40 colours (palette) | | 12,163 | 10,816 | 10,736 | |
| 400 x 300 test image, RGBA | | | 165,464 | 164,802 | |
| 400 x 300 test image, grey | | | 40,155 | 39,273 | |
| 12 MP photo | 19,172,860 | | 18,600,375 | | |

Ahead of oxipng on the photo; within 0.4 to 2.2% on the synthetic test images, where it was 13% behind on the palette
one. The cost is time: 5.2 s for the 12-megapixel photo on 16 cores (level 6: 155 ms).

## 2026-11-08: lossy WebP encoder search

The subblock mode search took 25 of the 41 ms a 400 x 300 lossy encode needed on one core. It now reads the edge
pixels once per subblock, predicts each of the ten modes into a 16-byte buffer, compares with vector squared errors,
and copies the winner back for the full trials; mode costs are tables; the shared predictor stores pixels directly
instead of through a `params` helper. Every change leaves the output byte for byte the same (hashes of 36 encodings
compared before and after).

| BenchmarkDotNet short job | Before, one core | After, one core | Before, 16 cores | After, 16 cores |
|---|---:|---:|---:|---:|
| 12 MP lossy encode, quality 80 | 3.60 s | 2.14 s | 667 ms | 539 ms |
| 12 MP lossy decode | 403 ms | 343 ms | 183 ms | 177 ms |
| JPEG to a 400 px WebP thumbnail | 86 ms | 72 ms | 59 ms | 50 ms |

cwebp takes 1.4 s for the 12 MP encode on one core; Skia makes the WebP thumbnail in 49 ms.

## 2026-11-08: streaming baseline JPEG decode

A baseline JPEG with one scan for every component (most photos) now decodes in bands of about 512 KiB of
coefficients: each band is decoded into a small buffer and transformed into the sample planes straight away, and with
more than one core the transform of one band runs while the next is decoded. The whole frame's coefficients (two
bytes a sample, 36 MB for the 12-megapixel photo) are never held. The reduced inverse DCT for shrink-on-load also
became vector code (down first as 8-wide vectors, then a dot product per sample). Progressive files and files with a
scan per component keep the whole-frame path; both give the same pixels.

| 12 MP JPEG, BenchmarkDotNet short job | Before, one core | After, one core | Before, 16 cores | After, 16 cores | Allocated before | After |
|---|---:|---:|---:|---:|---:|---:|
| Decode at full size | 123 ms | 119 ms | 44 ms | 37 ms | 34.5 MB | 0.4 MB |
| 400 px thumbnail (decode at 1/4, Lanczos3) | 99 ms | 53 ms | 40 ms | 30 ms | 34.5 MB | 0.5 MB |
| 1600 px web size | 274 ms | 206 ms | 63 ms | 60 ms | 34.7 MB | 0.7 MB |

libvips makes the thumbnail in 29 ms on one core and 32 ms on all of them, so on 16 cores we are level. The rest of
the one-core gap at full size is mostly the float inverse DCT and the colour output, not Huffman decoding (see
2026-11-10: decoding on one core).

## 2026-11-08: the pipeline end to end

The 12-megapixel photo to a 400 x 300 thumbnail, from the JPEG to the encoded file, BenchmarkDotNet short job:

| JPEG to a 400 px WebP, quality 80 | One core | 16 cores |
|---|---:|---:|
| Zorg.Imaging (`Fit(400, 400).SaveAsWebpAsync`) | 128 ms (197 in a busier run) | 68 ms |
| libvips 8.18 through NetVips (`ThumbnailBuffer`, `WebpsaveBuffer`) | 40 ms | 42 ms |
| SkiaSharp (scaled decode, Mitchell, libwebp) | 46 ms | 48 ms |
| ImageSharp 2.1 (full decode, Mitchell) | 385 ms | 122 ms |

The pipeline itself costs nothing measurable; the thumbnail without the encode is 105 ms on one core (Skia 37 ms).
What is left is in the codecs: the scalar reduced inverse DCT, the whole-frame coefficient buffer (35 MB allocated
for a thumbnail), and lossy WebP encoding at about twice libwebp's time per pixel (23 against 10 ms for 400 x 300).

| JPEG to 1600 px, JPEG 80 | One core | 16 cores |
|---|---:|---:|
| Zorg.Imaging (`Fit(1600, 1600).SaveAsJpegAsync`) | 364 ms | 63 ms |
| libvips 8.18 (`ThumbnailBuffer`, `JpegsaveBuffer`) | 70 ms | 65 ms |
| SkiaSharp (full decode, Mitchell) | 155 ms | 156 ms |

libvips is the target to beat: on all cores we are level with it, on one core it is 3x to 5x faster. Its time hardly
changes with the core count because its JPEG work is libjpeg-turbo's, which is single-threaded; its own thread pool
only splits the resize. `NetVips.Concurrency` follows `DOTNET_PROCESSOR_COUNT` in the benchmark, so the one-core rows
are fair to it.

## 2026-11-08: PNG optimiser and banded compression

The 12-megapixel photo (`photo.png`, RGB), BenchmarkDotNet short job, same busy machine for both columns:

| PNG encode, level 6 | One core | All cores (16) | Allocated |
|---|---:|---:|---:|
| Before (one zlib stream) | 682 ms | 649 ms | 64 MB |
| After (bands of 1 MiB) | 689 ms | 134 ms | 118 MB |

Rows are filtered and compressed in independent bands of about 1 MiB, each a raw deflate stream ended by a sync
flush, then joined into one zlib stream with the Adler-32 values combined, as pigz does. The bands depend only on the
image size, so the file is the same on any number of cores. Losing the window across band edges cost nothing
measurable here: 24,221,525 bytes against 24,223,014 before.

Sizes against oxipng 9 (`-o 2 --strip safe`, libdeflate) in the gate image, ours with `Optimize`:

| Image | Default | `Optimize` | oxipng -o2 | oxipng -o4 |
|---|---:|---:|---:|---:|
| 800 x 600 photo crop, RGB | 979,681 | 973,356 | 970,541 | 970,541 |
| 400 x 300 smooth gradient with noise | 144,133 | 121,774 | 123,644 | 121,797 |
| 400 x 300 with 64 colours (palette) | 12,303 | 12,163 | 10,736 | 10,732 |
| 600 x 400 RGBA graphic | 8,968 | 5,251 | 5,700 | 5,321 |

Layouts and filters match oxipng's; where it is ahead, it is libdeflate at level 11 against zlib at 9 (oxipng's
palette file has the same filter, None). `Optimize` takes about 2.5 s for the 12-megapixel photo on 16 cores and saves
1%; on noisy rows zlib's Huffman-only and RLE strategies often beat level 9, which is why they are among the trials.
Zopfli-class deflate is the planned next step for size.

## 2026-11-08: colour conversion to sRGB

`ToSrgb_DisplayP3`: the 12-megapixel photo tagged Display P3, converted to sRGB as encoders do by default.

| | One core | All cores |
|---|---:|---:|
| Before the quantiser change | 396 ms | 41 ms |
| After | 144 ms | 28 ms |

Three curve encodes a pixel dominated: each was a bucket lookup and a short binary search whose branches the
processor couldn't predict. For 8-bit curves whose every step is wider than 1/16384 in linear light (sRGB, BT.709,
linear; not a pure gamma), a table of the code at each of 16384 buckets and one comparison with the next midpoint
give the same exactly rounded code. Resizing 8-bit images in linear light uses the same encoder, so it gains too.

## 2026-11-08: GIF

Decoding `photo.gif` (ImageMagick's default reduction of the photo, 8.7 MB) and encoding the photo decoded from
`photo.jpg` at 256 colours (BenchmarkDotNet, short job).

| | One core | All cores | Allocated |
|---|---:|---:|---:|
| Decode, ours | 100 ms | 68 ms | 0.3 MB |
| Decode, SkiaSharp (giflib-derived) | | 51 ms | |
| Decode, ImageSharp 2.1 | | 123 ms | |
| Decode, ImageMagick 7 `magick photo.gif ppm:` (gate image) | 270 ms | | |
| Encode, ours, dithered (default) | 0.46 to 0.59 s | 0.30 s | 116 MB |
| Encode, ours, no dithering | 241 ms | 157 ms | 100 MB |
| Encode, ImageSharp 2.1 | | 2.8 s | 154 MB |
| Encode, ImageMagick 7 `magick photo.ppm gif:` (gate image) | 3.8 s | | |

SkiaSharp can't write GIF. Without dithering our reduction is within a dB of ImageMagick's `-dither None -colors`
PSNR at 16, 64 and 256 colours (`GifOracleTests`).

What made decoding fast: writing each LZW code as one copy of an earlier stretch of the output (every string in the
table has been written once already), and turning a first frame that fills the canvas straight into the image, in
bands of rows, without a canvas (136 to 68 ms, 46 MB to 0.3 MB allocated). For encoding: a 16K-entry hash table for
LZW that stays in the fastest cache, sub-blocks written as the codes are, and a nearest-colour search that sorts the
palette by green and stops once green alone is further than the best so far. Still to do: Floyd–Steinberg dithering
is one thread (about 150 ms here); a wavefront over rows, as the lossy WebP encoder uses, would spread it.

## 2026-11-08: lossy WebP encoding

The 12-megapixel photo decoded from `photo.jpg`, quality 80, effort 4 (BenchmarkDotNet, short job).

| Encoder | One core | All cores | Allocated |
|---|---:|---:|---:|
| Ours | 3.05 s | 0.65 s | 130 MB |
| libwebp through SkiaSharp | 0.99 s | | |
| cwebp 1.6 `-q 80` (gate image) | 1.41 s | | |
| ImageSharp 2.1 | 3.53 s | 3.57 s | 1.47 GB |

Quality: at the file sizes cwebp gives for qualities 10 to 90, ours decodes to the same or higher PSNR (+0.05 dB on a
1000 x 1000 crop of the photo, +0.5 dB on the synthetic test photo; -0.1 dB on the crop at quality 90). cwebp tunes
for perceived quality with segments and spatial noise shaping, which ours doesn't have yet, so PSNR isn't the whole
story. Quality numbers are fitted so a quality gives about cwebp's file size.

Speed came from ranking each block's modes by prediction error and fully testing only the best few (5.3 to 2.9 s on
one core, with slightly smaller files), and from coding macroblock rows as a wavefront (0.65 s on 16 cores). Still to
do on one core: SIMD for the forward transform, quantisation and error sums, and a cheaper token-cost walk.

## 2026-11-08: lossless WebP encoding

The 12-megapixel photo, effort 4 (cwebp's default `-m 4`). Sizes are for `photo.png`; times are BenchmarkDotNet's for
the photo decoded from `photo.jpg`, taken while another session was using the machine, so treat them as ±15%.

| Encoder | Size | One core | All cores |
|---|---:|---:|---:|
| Ours | 23.08 MB | 5.9 s | 2.2 s |
| cwebp 1.6 `-lossless -exact -m 4` (gate image, its own timer) | 23.05 MB | about 4 s | |
| libwebp through SkiaSharp (lossless, quality 75) | | 2.8 s | |
| ImageSharp 2.1 (lossless) | | 9.7 s | 9.9 s |

At effort 0 ours is 26.44 MB against cwebp `-m 0`'s 26.77 MB; on small test pictures ours is smaller on grey (84
against 116 bytes) and within 10% on few-colour graphics. What made the difference, in order:
1. **No subtract-green before the predictor when cross-colour follows** (5%): cross-colour's multipliers take green
   out as far as it helps, and predicting on the raw channels gives cleaner residuals. cwebp's verbose output
   showed it doing the same.
2. **Cross-colour** (6%) and the **entropy image** (1%).
3. **A cost-based parse** that prices cache hits exactly (a pixel's cache hit doesn't depend on the parse, since every
   pixel enters the cache in order); on graphics it halved the size.

And the time, from 5.5 s on one thread: the hash table sized to the image (2.4 to 1.3 s for the match search), tiles
of a row chosen in parallel (predictor 1.5 to 0.2 s, cross-colour 1.1 to 0.1 s), matches searched in parallel in fixed
chunks, and the cache sizes tried in parallel. Still to do: SIMD for the predictor and multiplier searches (2.4 s of
the 4.2 s on one core), and memory: about 840 MB for a 12-megapixel encode, mostly per-pixel arrays in the match
search and the parse, which could be pooled or banded.

## 2026-11-08: against ImageSharp 2.1

ImageSharp 2.1.13 (`ImageSharpBenchmarks`) is the last release under Apache-2.0; 3.0 moved to the Six Labors Split
License, and we never read its source. It is the closest managed baseline, but an older one: its package has no .NET 5+
build, so .NET 10 runs its netcoreapp3.1 build, and 2.1 has no shrink-on-load. It decodes to `Rgb24` here, like ours.
It uses all cores by default. Another session was using the machine during these runs, so they are noisier than the
SkiaSharp ones below (the one-core column especially).

| Task | ImageSharp 2.1, one core | ImageSharp 2.1, all cores | Ours, one core | Ours, all cores |
|---|---:|---:|---:|---:|
| JPEG decode, baseline | 80 ms | 59 ms | 76 ms | 37 ms |
| JPEG decode, progressive | 193 ms | 139 ms | 180 ms | 139 ms |
| JPEG encode, quality 80, 4:2:0 | 79 ms | 77 ms | 116 ms | 77 ms |
| PNG decode | 135 ms | 109 ms | 152 ms | 153 ms |
| PNG encode (its defaults, ours level 6) | 2,532 ms | 2,444 ms | 632 ms | 631 ms |
| WebP lossy decode | 257 ms | 208 ms | 310 ms | 169 ms |
| WebP lossless decode | 367 ms | 313 ms | 241 ms | 255 ms |
| JPEG to a 400 px thumbnail | 91 ms | 82 ms | 98 ms | 37 ms |
| JPEG to JPEG at 1600 px | 214 ms | 116 ms | 217 ms | 62 ms |

What it adds to the list below: ImageSharp 2.1's **JPEG encoder is faster than ours on one core** (79 against 116 ms),
its **PNG decode is faster** (109 ms on all cores; ours does not split PNG decode yet), and its one-core WebP lossy
decode is faster than ours (257 against 310 ms). Ours is ahead on PNG encode (about 4x), WebP lossless, and anything
shrink-on-load or our threads reach.

## 2026-11-08: against SkiaSharp and libpng

SkiaSharp 4.154.0 (Skia with libjpeg-turbo, libpng, libwebp and zlib, all native) runs the same work in the same
process (`SkiaBenchmarks`). Skia is single-threaded here. It decodes to 32-bit BGRA (ours to 24-bit RGB for these
photos), and its resize is a plain Mitchell filter on the stored values (ours Lanczos3 in linear light), so it does a
little less work in both.

| Task | SkiaSharp | Ours, one core | Ours, all cores |
|---|---:|---:|---:|
| JPEG decode, baseline | 84 ms | 76 ms | 37 ms |
| JPEG decode, progressive | 150 ms | 180 ms | 139 ms |
| JPEG encode, quality 80 | 161 ms | 116 ms | 77 ms |
| PNG decode | 120 ms | 152 ms | 153 ms |
| PNG encode (level 6, adaptive filters) | 1,332 ms | 632 ms | 631 ms |
| WebP lossy decode | 134 ms | 310 ms | 169 ms |
| WebP lossless decode | 169 ms | 241 ms | 255 ms |
| JPEG to a 400 px thumbnail (scaled decode, resize) | 35 ms | 98 ms | 37 ms |
| JPEG to JPEG at 1600 px | 156 ms | 217 ms | 62 ms |

libpng through ImageMagick 7.1.2 in the gate image (best of three, PPM reading and writing subtracted): decode about
150 ms, encode with adaptive filters at level 6 about 1.65 s. Our encoder's files were about 1% larger than libpng's
(24.2 MB against 23.9 MB); .NET's zlib is zlib-ng, which is faster than classic zlib.

Where we're behind on one core, in order of what it costs a web server:
1. **The JPEG thumbnail** (98 against 35 ms): the reduced inverse DCT that shrink-on-load uses is still scalar, and
   the final resize is in linear light.
2. **WebP lossy decode** (310 against 134 ms): token decoding and reconstruction; threads help (169 ms).
3. **WebP lossless decode** (241 against 169 ms) and **PNG decode** (152 against 120 ms).
4. **Progressive JPEG** (180 against 150 ms): refinement scans.

## 2026-11-08, after the second round

Same files and machine, quieter; 15 warm-up rounds so every method reaches its final compiled tier.

| Task | One core | All cores | Native, one core |
|---|---:|---:|---:|
| JPEG decode, baseline | 75 ms | 37 ms | djpeg 40 ms |
| JPEG decode, progressive | 171 ms | 139 ms | djpeg 110 ms |
| JPEG thumbnail, 400 px | 98 ms | 37 ms | |
| JPEG to JPEG at 1600 px | 219 ms | 62 ms | |
| JPEG encode, quality 80 | 116 ms | 77 ms | cjpeg 120 ms |
| WebP lossy decode | 305 ms | 169 ms | dwebp 160 ms |
| WebP lossless decode | 239 ms | 255 ms | dwebp 220 ms |
| PNG decode | 151 ms | 153 ms | |
| PNG encode, level 6 | 628 ms | 631 ms | |

What changed in this round:

- **PNG encode** (1.5 s to 628 ms): the five row filters run as vectors and are scored in the same pass; what is
  left is zlib's own time.
- **JPEG encode** (about 2.3x on one core, now level with cjpeg): RGB to YCbCr by byte shuffles and float vectors,
  blocks read in place, chroma averaged once per MCU row, vector quantisation, and a bit writer that stores four bytes
  at a time.
- **WebP lossless decode** (about 1.6x): two-level prefix-code tables with the bit buffer in locals, vector predictor
  modes and transforms.
- **WebP lossy decode on several cores** (about 1.8x): parsing, reconstruction, filtering and colour conversion run as
  a row pipeline across threads; one core is unchanged.

Left: lossy WebP and baseline JPEG on one core (about 1.9x the native tools: token decoding and reconstruction for
WebP; for JPEG, the inverse DCT and colour output more than Huffman decoding), progressive JPEG's refinement scans, the scalar reduced inverse DCT used by shrink-on-load, and
zlib's share of PNG encoding (which libpng pays too).

## 2026-11-08, after the first performance pass (D13)

| Task | One core | All cores | Native, one core | Before the pass, one core |
|---|---:|---:|---:|---:|
| JPEG decode, baseline | 78 ms | 37 ms | djpeg 40 ms | about 266 ms |
| JPEG decode, progressive | 188 ms | 139 ms | djpeg 110 ms | about 360 ms |
| JPEG thumbnail, 400 px (shrink-on-load) | 98 ms | 37 ms | djpeg -scale 1/4: 20 ms | about 118 ms |
| JPEG to JPEG at 1600 px | 310 ms | 67 ms | | about 533 ms |
| JPEG encode, quality 80 | 309 ms | 96 ms | cjpeg 120 ms | about 396 ms |
| WebP lossy decode | 320 ms | 315 ms | dwebp 160 ms | about 584 ms |
| WebP lossless decode | 370 ms | 371 ms | dwebp 220 ms | about 383 ms |
| PNG decode | 152 ms | 152 ms | | about 178 ms |
| PNG encode, level 6 | 1.54 s | 1.47 s | not timed | about 1.55 s |
| Resize to a 400 px thumbnail | 75 ms | 14 ms | | |

What changed, and what is left:

- **JPEG decode:** coefficients in natural order with vector dequantisation, empty-row skipping and vector output in
  the inverse DCT; Huffman decoding with its bit buffer in registers and 8-byte refills; YCbCr to RGB and chroma
  upsampling on 16 pixels at a time. Left: progressive scans (one bit at a time in refinement), and the reduced
  inverse DCT that shrink-on-load uses, which is still scalar.
- **WebP lossy decode:** the loop filter on eight points at once, a 64-bit boolean decoder, vector YUV to RGB. Left:
  token decoding (about 120 ms), reconstruction (about 75 ms), and no threads yet (macroblock rows as a wavefront).
- **Not started:** WebP lossless, JPEG encode, PNG encode and decode. PNG encode spends its time in zlib and the
  adaptive filter choice; measure first.

## How to measure

```bash
scripts/make-bench-files.sh .tmp/bench
ZORG_BENCH_JPEG_DIR=.tmp/bench dotnet run -c Release --project benchmarks/Zorg.Imaging.Benchmarks -- --filter '*' --job short
```

For one core, set `DOTNET_PROCESSOR_COUNT=1`. When comparing two versions of a hot loop, `DOTNET_TieredCompilation=0`
gives steadier numbers (the code is fully optimised from the first call); BenchmarkDotNet's own warm-up does the same for
the published numbers.
