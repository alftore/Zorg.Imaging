# .NET versions

The library targets one .NET version and runs on later ones unchanged. This page keeps what each new version offers
an image library, so the target can be reconsidered when a new LTS ships or the current one leaves support.

Rule of thumb: move the target to the next LTS when the current one leaves support, and add a second target before
that only for an API with a measured gain.

## .NET 10 (LTS, the current target)

- Supported until 2028-11-14.
- What the library is built on: `Vector128/256/512`, AVX-512, `ZLibCompressionOptions`, `LibraryImport`, C# 14
  (`field`, extension members).

## .NET 11 (STS, 2026-11-10)

- Supported until about November 2028, about the same time as .NET 10.
- Checked 2026-11-11: every test passes and 510 outputs are byte-identical on .NET 11.0.0 running the `net10.0`
  build. CI runs the tests on .NET 11.
- Free on the .NET 11 runtime, no retarget needed: JIT bounds-check elimination (`i + c < len`, `span[^1]`), a SIMD
  cost model for modern SSE/AVX, faster `uint` to `float` casts, hardware FP16 for `Half`.
- Behaviour change to keep in mind: unchecked `float`/`double` to small-integer casts now saturate instead of wrapping.
  No output changed, but new code should clamp before casting rather than rely on either.
- New minimum hardware: x86-64-v2 (SSE4.2), and LSE on Arm64 Windows. Our scalar fallbacks below that become dead
  code on .NET 11 only.
- APIs worth a look if we target it:
  - `Vector*.Zip`, `Unzip`, `Concat*`, `Reverse`: interleaving RGB and YUV planes, now hand-written shuffles.
  - `Reset()` on the streamless Deflate and ZLib encoders: fewer allocations in the PNG optimiser's trial compressions.
  - Zstandard in `System.IO.Compression`: TIFF with zstd compression (rare).
  - `AvxVnni.V512`: 8-bit multiply-add, maybe for integer resize or DCT paths.
- Not useful here: Runtime Async (hot paths are synchronous), C# 15 unions and closed hierarchies (API design only).
- Decision: stay on `net10.0`.

## .NET 12 (LTS, expected November 2027)

- To check when it ships: new SIMD and intrinsics, compression and JIT changes, and whether the .NET 11 APIs above
  have a measured gain. Then decide between retargeting to `net12.0` and targeting both until .NET 10 leaves support.
