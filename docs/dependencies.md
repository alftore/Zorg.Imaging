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

## Considered and rejected

| Package | Reason |
|---|---|
| SixLabors.ImageSharp 4.x | Six Labors Split License, and 4.x fails the build without a licence key. Not usable even as a benchmark baseline without a key. |
| System.IO.Hashing | MIT, but a NuGet package; PNG's CRC-32 is a few lines of our own. |
| FluentAssertions, Moq, Verify | Licence changes or a licence declaration step in recent versions (same decision as Zorg.Ssh). |
| Microsoft.Testing.Extensions.CodeCoverage | Proprietary Microsoft terms (same decision as Zorg.Ssh); `coverlet.MTP` instead. |
