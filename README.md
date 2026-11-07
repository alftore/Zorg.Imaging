# Zorg.Imaging

Managed image decoding, resizing and encoding for .NET 10, written in C# only. PNG, JPEG, GIF and WebP in one package, with no native binaries and no NuGet dependencies.

**Status: early development. Nothing works yet beyond the first building blocks, and nothing has been published.** The plan is in `.tmp/design/01-implementation-plan.md` on the maintainer's machine.

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
