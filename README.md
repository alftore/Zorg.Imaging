# Zorg.Imaging

Managed image decoding, resizing and encoding for .NET 10, written in C# only. PNG, JPEG, GIF and WebP (with animations), plus BMP and TIFF decoding, in one package, with no native binaries and no NuGet dependencies.

**Status: 0.3, on NuGet as `Zorg.Imaging` (and `Zorg.Imaging.SystemCodecs`). The API may still change before 1.0. It can recognise image formats and read their headers (`ImageFormats.Detect`, `Image.Identify`), decode and encode PNG (with an oxipng-style optimiser) and JPEG, decode and encode WebP (lossy, lossless and near-lossless) and GIF, decode BMP and TIFF, keeping the colour space (`Image.Decode`, `SaveAsPng`, `SaveAsJpeg`, `SaveAsWebp`, `SaveAsGif`), and resize, fit, crop, turn, flatten, blur, sharpen, watermark and trim images, make BlurHash placeholders, one at a time or as a pipeline (`ImagePipeline.Load(...).Fit(...).SaveAsync(...)`; see [docs/pipeline.md](docs/pipeline.md)), and read and write animated GIF and WebP frame by frame (`AnimationReader`, `AnimationWriter`; see [docs/animation.md](docs/animation.md)).** Other packages can add decoders for AVIF, HEIC and the other formats it only recognises ([docs/codecs.md](docs/codecs.md)); `Zorg.Imaging.SystemCodecs` decodes AVIF and HEIC through Windows' and macOS's own decoders. What isn't supported yet is in [docs/limits.md](docs/limits.md).

## Goals

- Decode, resize and encode the common web formats, including WebP, in pure managed code.
- MIT licensed, with no runtime dependencies, NativeAOT and trimming clean.
- Sensible defaults for the web: metadata stripped, EXIF orientation applied, wide-gamut colour converted to sRGB.
- Quality resizing: linear-light downscaling, premultiplied alpha, JPEG shrink-on-load.
- Native code only as an opt-in add-on, never a requirement.

## How this is built

Zorg.Imaging is written with the help of AI agents, mainly Claude Code from Anthropic. The maintainer sets the direction and makes the design decisions, and agents do most of the implementation. This note covers the whole project, so individual commits don't carry a co-author line.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Report security problems as [SECURITY.md](SECURITY.md) describes. Local scratch files go in `.tmp/`, which is gitignored.

## License

MIT. See [LICENSE](LICENSE). Package choices are recorded in [docs/dependencies.md](docs/dependencies.md).
