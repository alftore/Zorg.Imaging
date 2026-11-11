# Zorg.Imaging.SystemCodecs

AVIF and HEIC decoding for [Zorg.Imaging](https://www.nuget.org/packages/Zorg.Imaging), through the operating system's own decoders: Windows Imaging Component on Windows, ImageIO on macOS. It ships no codec and no native binaries, so it carries no HEVC or AV1 code of its own. Meant for desktop apps on Windows and macOS.

```csharp
using Zorg.Imaging;

ImageDefaults.Codecs = SystemCodecs.All; // once, at start-up

using Image photo = Image.Decode(File.ReadAllBytes("IMG_0001.HEIC"));
```

**Early releases: the API may still change before 1.0.**

## What each system needs

| | HEIC | AVIF |
|---|---|---|
| Windows 10 or 11 | HEIF Image Extension and HEVC Video Extensions from the Microsoft Store (some PCs have them preinstalled; Windows 11 24H2 includes them) | HEIF Image Extension and the free AV1 Video Extension |
| macOS | 10.13 or later | 13 or later |
| Linux, Windows Server | Not available | Not available |

`SystemCodecs.Heif.Availability` says whether it works here and, if not, what to install. A file that needs a missing codec fails with `UnsupportedImageFormatException` and that reason.

## Safety

Zorg.Imaging reads each file's boxes itself and checks the size and tile count against `ImageLimits` before the operating system sees the file. The decoding itself is the operating system's native code: it runs in your process, can't be cancelled or timed out once started, and a fault in it can't be caught. Keep the system updated; the Store updates the Windows extensions and macOS updates ImageIO.

## Documentation

- [Codecs from other packages](https://github.com/alftore/Zorg.Imaging/blob/main/docs/codecs.md)
- [Changelog](https://github.com/alftore/Zorg.Imaging/blob/main/CHANGELOG.md)
