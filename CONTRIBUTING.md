# Contributing

## Build and test

You need the .NET SDK version in `global.json`.

```
dotnet build Zorg.Imaging.slnx
dotnet test --solution Zorg.Imaging.slnx
```

The build treats warnings as errors, so it has to finish with 0 warnings.

## The Linux gate

The oracle tests compare our output with reference tools: libpng's tools, libjpeg-turbo (`djpeg`, `cjpeg`), libwebp (`dwebp`, `cwebp`, `webpmux`), `oxipng`, `exiftool` and ImageMagick. A plain machine doesn't have them, so those tests skip. The Linux gate builds and tests in a container that has them. It needs Podman or Docker, and installs nothing on your machine.

```
scripts/test-linux.sh                    # unit and oracle tests on Alpine (musl, every oracle tool)
scripts/test-linux.sh --variant ubuntu   # unit tests on Ubuntu (glibc; Ubuntu has no oxipng package)
scripts/test-linux.sh --aot              # also a Native AOT publish and run in the container
```

The script sends the tree into the container as a tar stream, so uncommitted edits are tested and the Windows checkout is never mounted. It uses `git ls-files`, so it needs the repository to be a git checkout. NuGet packages are cached in the volume `zorg-imaging-nuget`, and downloaded test corpora in `zorg-imaging-corpora`. Every step has a timeout. Set `ZORG_IMAGING_CONTAINER_CLI` if Podman or Docker isn't on `PATH`.

The containers use `--network host`, as in Zorg.Ssh: on the Podman machine used for development, the default rootless network was slow enough to make package installs look hung.

### Required oracles

`ZORG_REQUIRE_ORACLES=1` turns every "tool isn't installed" skip into a failure. The Alpine gate sets it, and so does the CI oracle job. A run that passes by skipping the oracle tests proves nothing.

## Clean-room rules

- Write codecs from the specifications: PNG (W3C), JPEG (ITU-T T.81, JFIF), GIF89a, VP8 (RFC 6386), WebP container and lossless (RFC 9649), APNG, EXIF (CIPA DC-008), ICC.
- **Never read ImageSharp's source.** It's under the Six Labors Split License, not an open licence.
- libwebp (BSD-3-Clause) and Rust's image-webp (MIT or Apache-2.0) may be read for ideas; say so in the file and keep their notices if anything is ported.
- Reference tools are black boxes: compare outputs, never copy code.

## AOT smoke test

`samples/Zorg.Imaging.AotSmoke` is published with Native AOT and run. Trimming keeps only what's used, so add each new codec and operation to it. It must publish with no trim or AOT warnings.

```
dotnet publish samples/Zorg.Imaging.AotSmoke -r win-x64 -c Release
samples/Zorg.Imaging.AotSmoke/bin/Release/net10.0/win-x64/publish/Zorg.Imaging.AotSmoke.exe
```

Windows needs the Visual Studio C++ workload. Use `linux-x64` on Linux (needs `clang` and `zlib1g-dev`), or run `scripts/test-linux.sh --aot`.

## Conventions

- **XML docs on all public members**, with an `<example>`.
- **No runtime dependencies.** The library references nothing but the BCL. Test and benchmark packages go through the review in `docs/dependencies.md`.
- **Hostile input is normal input.** Every decoder checks dimensions and sizes before it allocates, and malformed data throws `ImageFormatException`-family types, never `IndexOutOfRangeException` and the like.
- **`CancellationToken` on every async method.**
- **Public API changes** update `src/Zorg.Imaging/PublicAPI.Unshipped.txt`. The build fails otherwise.
- **Vertical slices.** Tests and docs land in the same commit as the code.
- **Codecs behind the shared codec interface**, even the built-in ones, so any can move to its own package later.

## Commits

Signed, with no co-author or generator lines. The README covers how the library is built.
