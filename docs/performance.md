# Performance

Measured on a 12-megapixel photo (4000 x 3000) in every format, made by `scripts/make-bench-files.sh`, with
`benchmarks/Zorg.Imaging.Benchmarks` (BenchmarkDotNet, short job, 8 iterations). One core means
`DOTNET_PROCESSOR_COUNT=1`; all cores is 16 here. The native tools (libjpeg-turbo, libwebp) were timed by the same script
in the Linux gate image, best of three runs, on one core: they are single-threaded C with hand-written SIMD.

The machine runs other work too, so treat single numbers as roughly ±10%; what matters is the ratio to the native
tools and the change between runs made back to back.

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

Left: lossy WebP and baseline JPEG on one core (about 1.9x the native tools: token decoding, reconstruction, and
Huffman decoding), progressive JPEG's refinement scans, the scalar reduced inverse DCT used by shrink-on-load, and
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
