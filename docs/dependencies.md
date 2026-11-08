# Dependencies

The library itself has no runtime dependencies, and that is a design rule. Every package used to build, test or benchmark is reviewed for licence and for future risk before it's added: a package can change licence, add fees, or lose its maintainer. Add a row here in the same change as the package.

Rules:

- No runtime package in `src/`. If one ever seems needed, it's a design decision first.
- No package with a fee, a commercial tier gating needed features, or a licence declaration step, even in tests or benchmarks.
- Check the licence in the `.nuspec` of the exact version. Don't rely on memory.

## Build only (not shipped)

| Package | Licence | Why | Risk |
|---|---|---|---|
| Microsoft.CodeAnalysis.PublicApiAnalyzers 5.6.0 | MIT | Tracks the public API | Low. Removing it changes nothing at run time. |

## Tests (not shipped)

| Package | Licence | Why | Risk |
|---|---|---|---|
| xunit.v3 4.0.2 | Apache-2.0 | Test framework | Low. |
| coverlet.MTP 10.1.0 | MIT | Coverage | Low. |

## Benchmarks (not shipped)

| Package | Licence | Why | Risk |
|---|---|---|---|
| BenchmarkDotNet 0.15.8 | MIT (checked in the `.nuspec`) | Measures speed and allocations | Low. Its own dependencies are only in the benchmark project; nothing reaches `src/`. |
| SkiaSharp 4.154.0, SkiaSharp.NativeAssets.Linux 4.154.0 (and the Win32, macOS assets it brings) | MIT (checked in each `.nuspec`) | A native baseline in the same process: Skia with libjpeg-turbo, libpng, libwebp and zlib | Low. Benchmarks only, never referenced from `src/` or `tests/`. Native code, so its own licences (Skia BSD-3-Clause, the codecs' permissive licences) ride along in the package. |
| SixLabors.ImageSharp 2.1.13 | Apache-2.0 (checked in the `.nuspec`; the last release before the Six Labors Split License) | A managed baseline in the same process | Medium, contained. Benchmarks only, never referenced from `src/` or `tests/`, and its source is never read. It has five open advisories about hostile files (GHSA-gwg2-r3hj-4w44, GHSA-j3p4-wp97-rph4, GHSA-j9gm-c75j-xc9q, GHSA-jjfr-hcj7-qf5w, GHSA-wmxv-xphr-5c9g), suppressed in the benchmark project alone: it only reads the photos `scripts/make-bench-files.sh` makes, and the project never ships. |
| NetVips 3.2.0 | MIT (checked in the `.nuspec`) | Calls libvips, the engine behind sharp, as the pipeline's native baseline | Low. Benchmarks only. Added at the maintainer's request (2026-11-08). |
| NetVips.Native 8.18.7 and its per-platform packages (libvips 8.18) | LGPL-3.0-or-later (checked in the umbrella and the win-x64 and linux-x64 `.nuspec` files) | The libvips binaries NetVips loads | Low, contained. Benchmarks only, loaded dynamically, never referenced from `src/` or `tests/`, never shipped; its `THIRD-PARTY-NOTICES.md` lists the bundled libraries (libjpeg-turbo, libwebp, libpng and others). |

## Considered and rejected

| Package | Reason |
|---|---|
| SixLabors.ImageSharp 4.x | Six Labors Split License, and 4.x fails the build without a licence key. Not usable even as a benchmark baseline without a key. |
| System.IO.Hashing | MIT, but a NuGet package; PNG's CRC-32 is a few lines of our own. |
| FluentAssertions, Moq, Verify | Licence changes or a licence declaration step in recent versions (same decision as Zorg.Ssh). |
| Microsoft.Testing.Extensions.CodeCoverage | Proprietary Microsoft terms (same decision as Zorg.Ssh); `coverlet.MTP` instead. |
