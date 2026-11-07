# Performance

Measured on a 12-megapixel photo (4000 x 3000) in every format, made by `scripts/make-bench-files.sh`, with
`benchmarks/Zorg.Imaging.Benchmarks` (BenchmarkDotNet, short job, 8 iterations). One core means
`DOTNET_PROCESSOR_COUNT=1`; all cores is 16 here. The native tools (libjpeg-turbo, libwebp) were timed by the same script
in the Linux gate image, best of three runs, on one core: they are single-threaded C with hand-written SIMD.

The machine runs other work too, so treat single numbers as roughly ±10%; what matters is the ratio to the native
tools and the change between runs made back to back.

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
