# Performance tuning in C#: what worked here

Notes from tuning Zorg.Imaging on .NET 10, kept as we learn. Each item says what was done, why it helped, and how much,
where it was measured. `docs/performance.md` has the current numbers.

## Measuring

- **Measure before changing anything, by phase.** Wrapping each phase of a decoder in `Stopwatch.GetTimestamp()` and
  adding up `Stopwatch.GetElapsedTime` per phase showed where the time went. Guesses were often wrong: in the JPEG
  decoder the inverse DCT and colour conversion cost more than Huffman decoding, the reverse of what was expected.
- **Use CPU time when the machine is busy.** With other work on the machine, wall time swung by 50 to 100%;
  `Process.GetCurrentProcess().TotalProcessorTime` before and after, on one thread, was steady.
- **Turn tiered compilation off for A/B comparisons of a hot loop.** In a short run, code starts in tier 0 or as
  on-stack replacement, and background JIT work counts as process CPU. `DOTNET_TieredCompilation=0` gives fully
  optimised code from the first call: Huffman decoding read 74 to 129 ms with tiering and a steady 32 ms without.
  BenchmarkDotNet's warm-up gets the same effect for published numbers.
- **Compare on one core.** `DOTNET_PROCESSOR_COUNT=1` makes `Environment.ProcessorCount` 1, so code that splits work
  by core count runs single-threaded; that is the fair comparison with single-threaded native libraries.
- **Compare against the native tool on the same file**, timed in the same environment (`scripts/make-bench-files.sh`).
- **Temporary instrumentation and the analyzers:** the build treats style warnings as errors, which blocks quick
  timing lines. Build the scratch run with `-p:EnforceCodeStyleInBuild=false -p:TreatWarningsAsErrors=false`, and remove
  the lines before committing (search for `Stopwatch` in `src/`).

## Keep hot state where the JIT can keep it in registers

- **Copy fields into locals for a hot loop, and store them back once.** The JPEG Huffman decoder kept its 64-bit bit
  buffer, bit count and position in fields of a class; every symbol loaded and stored them. Moving them into locals
  for the length of a block, and calling the reader object only for the rare long codes, took the baseline Huffman
  phase from about 95 to 32 ms (with the change below).
- **Refill eight bytes at a time.** `BinaryPrimitives.ReadUInt64BigEndian` into the bit buffer, keeping only the whole
  bytes that fit, replaces a byte-at-a-time loop. Pad the data with zero bytes (16 here) so the load never needs a
  bounds check at the end, and check for over-reading once per block instead of on every read.
- **Prefer an array plus an index and a step to a helper struct with a 2-D indexer.** A loop filter reading pixels
  through `plane.At(x + dx * i, y + dy * i)` spent its time on index arithmetic; one precomputed index and step was
  cheaper, though the big win there was SIMD.

## SIMD with the portable vector types

- **`Vector128<T>`, `Vector256<T>` and `Vector512<T>` first.** They compile to SSE/AVX2/AVX-512 on x64 and to AdvSimd on
  arm64. Check `Vector256.IsHardwareAccelerated` (or 128) and keep a scalar path for when it is false.
- **Pick the lane type for headroom.** Pixel arithmetic that overflows bytes (loop filter, upsampling, colour) was done
  in 16-bit lanes (`Vector128<short>`, eight pixels) or 32-bit lanes for products like `y * 19077`. `Vector128.WidenLower`
  and `WidenUpper` go up a size, `Vector128.Narrow` comes back down (it truncates, so clamp first).
- **Clamp with `Min` and `Max`.** Many scalar clips are clamps in disguise: libwebp's
  `(v & ~16383) == 0 ? v >> 6 : v < 0 ? 0 : 255` is exactly `Clamp(v >> 6, 0, 255)`, which vectorises directly.
- **Don't take elements out of a vector in a loop.** `vector.GetElement(i)` in the inner loop of the inverse DCT was
  slow; broadcasting the scalar from memory (`vector * span[i]`) took the transform from 34 to 28 ms.
- **Masks instead of branches.** The loop filter computes "filter this point", "high edge variance" and the filtered
  values for every lane, then picks with `Vector128.ConditionalSelect`. The same arithmetic as the scalar code, so the
  result is identical; 258 to 63 ms.
- **Find non-zero data with one compare.** `Vector128.Equals(...).ExtractMostSignificantBits()` gives a bit mask of
  non-zero coefficients; `BitOperations.TrailingZeroCount` walks only those. The JPEG encoder's coding loops went from
  testing 64 coefficients a block to visiting the few that matter (encode 141 to 102 ms).
- **Skip empty rows.** One vector compare per row of an 8 x 8 block tells which rows of coefficients are all zero; the
  transform skips them.
- **Store data in the order the vectors want it.** Keeping JPEG coefficients in natural (row-major) order rather than
  the zigzag order they arrive in made dequantisation one vector multiply per row (inverse DCT 120 to 36 ms, with
  vector output).
- **Byte shuffles need the hardware intrinsic.** Interleaving R, G and B planes into RGB uses `Ssse3.Shuffle` (`pshufb`)
  or `AdvSimd.Arm64.VectorTableLookup` (`tbl`); both give zero for an index of 0x80, so one mask table works for
  both. Fall back to scalar when neither is supported.
- **Rounding a float vector:** `Vector256.Floor(x + 0.5)` then `ConvertToInt32` is round-half-up; then clamp and narrow.
- **Every SIMD routine keeps a scalar reference and a test** that compares them on random input, and the Linux gate
  runs all tests with `DOTNET_EnableAVX512F=0`, `DOTNET_EnableAVX2=0` and `DOTNET_EnableHWIntrinsic=0`
  (`scripts/test-linux.sh --fallbacks`), so each narrower path runs too.

- **Look for loops that only look sequential.** PNG *unfiltering* depends on the byte just decoded, but *filtering*
  reads only raw bytes, so all five filters vectorise: Sub, Up and Average on 16 bytes at a time (Average as
  `(a & b) + ((a ^ b) >> 1)`, the overflow-free floor of the mean), Paeth in 16-bit lanes with two masks. Scoring each
  candidate in the same pass, and swapping buffers instead of copying the winner, took PNG filtering from 916 to 37 ms
  for a 12-megapixel image. Profiling first mattered: the expected cost, zlib, was the smaller part.
- **Not every knob helps.** zlib's `Z_FILTERED` strategy (`ZLibCompressionOptions.CompressionStrategy`), which libpng
  uses for filtered rows, gave the same bytes as the default with .NET's zlib-ng on our photos; the change was dropped.

- **Pull interleaved channels apart with the same shuffles.** RGB24 to separate R, G and B planes is three `pshufb`
  (or `tbl`) per output vector, ORed together, with a mask table built once. The JPEG encoder converts 16 pixels per
  step that way, then widens to floats.
- **Read blocks where they lie.** Passing a span and a row stride to the forward DCT, instead of copying each 8 x 8
  block into a buffer first, and averaging chroma once per row of blocks, took the encoder's transform stage from 160
  to 52 ms on one core.
- **`Vector256.Round` rounds half to even, like `MathF.Round`**, so vectorising rounding kept the output identical.

- **Two-level code tables.** A prefix code with an 8-bit root table walked longer codes a bit at a time; photos use
  many 9- to 12-bit codes, so that walk was hot. A second-level table under each 8-bit prefix (as zlib's inflate
  builds them) decodes every code in at most two lookups. With the bit buffer in locals, lossless WebP's entropy
  decoding went from 320 to 175 ms.
- **Hoist per-block values out of per-pixel loops.** The cross-colour transform looked up its block's multipliers for
  every pixel; reading them once per run of the block halved the stage with the other transforms (37 to 23 ms).
- **Vectorise the independent cases, leave the dependent ones tight.** VP8L predictor modes that read only the row
  above add four pixels per vector; modes that need the pixel just decoded stay scalar but lose the per-pixel mode
  lookup.
- **Measure even "obvious" rewrites.** Hand-unrolling a four-channel clamp-and-add loop into one expression made the
  predictor slower (58 to 72 ms); the loop went back.

## Bit writing

- **Write a code and its extra bits as one value** (`code << size | bits`, at most 27 bits) instead of two calls.
- **Let bits pile up, then store four bytes at once** when none of them is 0xFF (JPEG must follow 0xFF with 0x00):
  a branch-free test for 0xFF in a word decides, and the rare word that has one goes byte by byte. JPEG encoding's
  bit writing went from 55 to 41 ms.

## Arithmetic

- **Rewrite an algorithm into an equivalent form only with a proof.** The VP8 boolean decoder keeps the range as range
  - 1, so the split is `(range - 1) * p >> 8` and the test `value > split`: algebraically the RFC's
  `value >= 1 + ((range - 1) * p >> 8)`. Renormalising with one shift from `BitOperations.Log2` replaces a loop. The
  output stayed byte for byte libwebp's (token decoding 163 to 119 ms).
- **Compute shared terms once.** The chroma upsampler computed each column's vertical sum twice per output pixel;
  computing all column sums first, then the outputs (and then both as vectors), took colour output from 125 to 22 ms
  together with SIMD colour conversion.

- **Allocation tests need a real warm-up.** A test that `GC.GetAllocatedBytesForCurrentThread()` is unchanged by a
  call failed once (6,800 bytes over 100 rounds) when run with SIMD switched off: one warm-up call left the method
  inside tiered compilation's call counting during the measured rounds. Fifty warm-up rounds fixed it.

## Threads

- **Split by bands of rows on the thread pool** (`Parallel.For` with `MaxDegreeOfParallelism`), never more bands than
  the work justifies; small images stay on the calling thread.
- **Let the caller choose.** `ImageDefaults.MaxDegreeOfParallelism` (default: all cores) and per-call overrides flow
  through an `AsyncLocal` scope, so everything a pipeline calls follows one setting without passing it everywhere.
- **Results must not depend on the number of threads.** A test runs the same work on 1 and 8 cores and compares bytes.

- **When rows depend on rows, pipeline the stages instead of splitting the rows.** A VP8 frame can't be cut into
  bands (each row predicts from the row above, and filtering a row changes the one above it), but its four stages per
  row (parse, reconstruct, filter, convert) can overlap: each stage takes its rows in order, one thread at a time, and
  any thread takes whichever stage has a row ready. Lossy WebP went from 315 to 169 ms on 16 cores, with identical
  output because each stage keeps its sequential order.
- **Let the caller's thread work too, and never wait for a pool thread to start.** If helpers are queued to the thread
  pool and the caller only waits for them, a busy pool can stall or deadlock the decode. Here the caller runs the same
  loop as the helpers, so it finishes alone if no helper ever starts; it waits only for helpers that are already
  inside a stage. A failure in any stage is captured (`ExceptionDispatchInfo`) and rethrown on the caller's thread.
- **Find the work that's independent once a decision is held still.** The WebP lossless encoder chooses each tile's
  predictor and colour multipliers by statistics of what it chose before, which looks sequential. Refreshing those
  statistics once per row of tiles instead of after every tile cost nothing in file size, and makes the tiles of a row
  independent: they run in parallel, and the counts they add are summed after the row. 1.5 s became 0.22 s.
- **Split a search from the state it depends on.** Hash chains can be built first (each pixel links only to earlier
  ones), then every pixel's match searched in parallel. The few candidates that depend on the parse so far (the last
  distances used) are checked afterwards in one cheap sequential pass. Chunks have a fixed size, not one per thread,
  so the result is the same on any number of cores.
- **Several candidates evaluated over the same data are separate tasks.** The colour-cache size is chosen by trying
  eleven sizes; each is its own walk over the tokens, so they run side by side.
- **A wavefront when each unit depends on its neighbours above.** A VP8 encoder's macroblock needs the one to its left
  and the three above it. Rows are claimed in order by threads that code them to the end, each row two macroblocks
  behind the one above (a volatile progress counter per row, read with `Volatile.Read`). A row only ever waits on a row
  already claimed by a running thread, so nothing can deadlock however the pool schedules, and the caller codes rows
  too. A failure sets a flag the waiting threads check, so they stop instead of spinning forever. 12-megapixel lossy
  WebP went from 2.9 s to 0.7 s on 16 cores, the same bytes either way.
- **Spin without sleeping for short waits.** `SpinWait.SpinOnce(sleep1Threshold: -1)` yields but never calls
  `Thread.Sleep(1)`, which on Windows can sleep for 15 ms.

## Measuring, again

- **Give BenchmarkDotNet enough warm-up for big methods.** With the short job's three warm-up rounds, the JPEG encoder
  measured bimodally (mean 272 ms, median 336 ms) because its long methods were still in tier 0 or on-stack
  replacement; with 15 warm-up rounds it was a steady 116 ms.

## Doing less

- **Rank cheaply, then test the few.** The lossy encoder chose each 4 x 4 block's prediction mode by transforming,
  quantising, pricing the tokens and reconstructing every one of the ten modes. Ranking them by plain prediction error
  first and testing only the best three fully halved the time on one core, and the files came out a little smaller,
  not larger: the error ranking agrees with the full test almost always.

## Memory

- **Size hash tables to the input.** A fixed 2^18-bucket table for a 12-megapixel image puts about 46 pixels in each
  bucket, and walking a chain becomes a run of cache misses. One bucket per pixel (2^22 at most) halved the WebP
  match search, from 2.4 to 1.3 s, with the same output.

- **Rent big buffers from `ArrayPool<T>.Shared`, `stackalloc` small ones**, and never allocate per pixel or per block.
