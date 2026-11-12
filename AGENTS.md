# Working on Zorg.Imaging

Managed image decoding, resizing and encoding for .NET 10, in C# only. Early development: the public API so far is the format check (`ImageFormats`, `Image.Identify`), the pixel buffer (`Image`, `PixelFormat`, the 8- and 16-bit pixel types), colour spaces and conversion to sRGB (`ColorSpace`, `Image.ColorSpace`, `ToSrgb`, `ColorSpaceHandling`), metadata (`MetadataOptions`: strip by default, keep, keep non-personal), resizing and the everyday operations (`Resize`, `Fit`, `Fill`, `Pad`, `Crop`, `Rotate`, `Flip...`, `Flatten`), the pipeline (`ImagePipeline`), the exception types, PNG decode and encode (`Image.Decode`, `SaveAsPng`), and JPEG decode (with shrink-on-load) and encode (`SaveAsJpeg`). WebP decode and encode, lossy, lossless and near-lossless (`SaveAsWebp`). GIF decode (first frame) and encode (`SaveAsGif`). BMP and TIFF decode. Animations frame by frame (`AnimationReader`, `AnimationWriter`). Effects (`Blur`, `Sharpen`, `DrawImage`, `Trim`, `ToBlurHash`) and lossy PNG (`PngEncoderOptions.MaxColors`). Codecs from other packages for recognised formats (`ImageCodec`, `ImageCodecs`, `docs/codecs.md`).

## Working on the repo

```bash
dotnet build Zorg.Imaging.slnx -nologo -v q
```

```bash
dotnet test --solution Zorg.Imaging.slnx --no-build
```

- `scripts/test-linux.sh` builds and tests in a Linux container with every oracle tool (add `--aot`, `--variant ubuntu`). `CONTRIBUTING.md` has the details.
- `CONTRIBUTING.md` has the conventions and the clean-room rules. The build fails on any warning.
- Commits are signed through `commit.gpgsign`. Never bypass it. If signing fails, stop and tell the maintainer.
- Commit messages and pull requests have no `Co-Authored-By` or "Generated with" lines. The README states that agents help build the library.
- `.tmp/design/` holds the agreed design, where it exists on your machine. `01-implementation-plan.md` wins over older notes.
- `docs/dotnet-versions.md` records what each new .NET version offers the library; update it when one ships.
- The library takes no runtime packages. Test and benchmark packages need a row in `docs/dependencies.md`, with the licence checked in the `.nuspec`.
- Never read ImageSharp's source (Six Labors Split License). Other libraries may be read for ideas, but don't copy their code.

## The guarantees

Each will have tests. A change that weakens one needs a design decision, not a test edit.

1. **A hostile file can't cause unbounded memory, a hang or an unhandled exception type.** Dimensions, frame counts, chunk sizes and decompressed sizes are checked against limits before anything is allocated.
2. **Defaults are safe for the web.** Metadata is stripped unless asked for, orientation is applied, and colour is never silently shifted.
3. **No native code unless the caller adds a native package.**
