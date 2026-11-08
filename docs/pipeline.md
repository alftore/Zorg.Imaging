# The image pipeline

`ImagePipeline` is the main way to use Zorg.Imaging: load an image, change it, and save it in one chain.

```csharp
await ImagePipeline.Load("photo.jpg")
    .Fit(1200, 1200)
    .SaveAsync("photo.webp");
```

Nothing is read until the chain is run. `Load` records where the image is, each step records what to do, and
`SaveAs...Async`, `SaveAsync` or `ToImageAsync` runs it all. Because the pipeline knows the steps before it decodes, a
large JPEG that is about to be made small is decoded at 1/2, 1/4 or 1/8 of its size, which is much faster.

## Sources

| Call | Reads | Runs |
|---|---|---|
| `ImagePipeline.Load(byte[])` | The whole file in memory, not copied | Any number of times |
| `ImagePipeline.Load(string path)` | The file, opened when the pipeline runs | Any number of times |
| `ImagePipeline.Load(Stream)` | The stream, from its current position, when the pipeline runs; it needn't seek | Once |
| `ImagePipeline.From(Image)` | An image already in memory, never changed or disposed | Any number of times |

A pipeline is immutable: each step returns a new pipeline. A configured pipeline can be kept in a field and reused,
and a step's arguments are checked when it is added, not when the pipeline runs.

## Steps

| Step | Does |
|---|---|
| `Resize(width, height)` | Resizes to an exact size; 0 for one side keeps the aspect ratio |
| `Fit(width, height)` | Shrinks to fit inside a box, never enlarging |
| `Fill(width, height)` | Covers the box exactly, cropping the overhang from the centre |
| `Pad(width, height, background)` | Fits inside the box, then centres the result on a background colour |
| `Crop(x, y, width, height)` | Cuts out a rectangle; whether it fits is checked when the pipeline runs |
| `Rotate(degrees)` | Turns clockwise by a multiple of 90° |
| `FlipHorizontal()`, `FlipVertical()` | Mirrors the image |
| `Flatten(background)` | Draws the image over a solid colour, removing transparency |
| `ToSrgb()` | Converts the pixels to sRGB, clipping colours outside it |
| `Blur(sigma)` | Gaussian blur in linear light, alpha premultiplied; `sigma` in pixels of the image the step gets |
| `Sharpen(sigma, amount)` | Unsharp mask on the stored values; 0.7 and 0.5 suit a resized photo |
| `DrawImage(overlay, x, y, opacity)` | Draws another image over this one, as a watermark; the overlay must outlive the pipeline run |
| `Trim(threshold)` | Crops away borders matching the top-left pixel, within `threshold` (0 to 255) per channel |

The resizing steps take an optional `Resampler`. Without one, shrinking uses Lanczos3 and enlarging uses Mitchell. Resizing works in
linear light with premultiplied alpha, so dark edges and halos around transparent areas don't appear.

Each step is the `Image` method of the same name. A chain gives the same pixels as calling those methods one by one,
except where shrink-on-load applies.

### When shrink-on-load applies

The JPEG decoder shrinks on load when a resize (`Resize`, `Fit`, `Fill` or `Pad`) comes before any `Crop`. Turns,
flips, `Flatten` and `ToSrgb` may come before the resize. It always decodes at least twice the target size and lets
the resampler do the rest, so quality is the same as a full decode followed by a resize, to within rounding.

```csharp
ImagePipeline.Load(jpeg).Rotate(90).Fit(400, 400)        // shrinks on load
ImagePipeline.Load(jpeg).Crop(0, 0, 2000, 1500).Fit(400, 400) // decodes at full size: the crop needs every pixel
```

PNG, WebP and GIF have no such shortcut and always decode at full size.

## Saving

| Call | Writes |
|---|---|
| `SaveAsPngAsync(stream, options)`, `SaveAsJpegAsync`, `SaveAsWebpAsync`, `SaveAsGifAsync` | That format, with the options given or the format's defaults |
| `SaveAsAsync(stream, format)` | A format chosen at run time, with its defaults |
| `SaveAsync(path)` | The format the file extension names (`.png`, `.jpg`, `.jpeg`, `.webp`, `.gif`), with its defaults |
| `ToImageAsync()` | Nothing: returns the image, which the caller disposes |

`SaveAsync(path)` writes to a temporary file in the same folder and replaces the target only once the whole file is
written. If decoding, a step or the write fails, an earlier file at that path is left as it was and the temporary file
is removed.

To choose the format from a request's `Accept` header:

```csharp
ImageFormat format = accept.Contains("image/webp") ? ImageFormat.Webp : ImageFormat.Jpeg;
response.ContentType = format.MimeType;
await ImagePipeline.Load(path).Fit(800, 800).SaveAsAsync(response.Body, format, cancellationToken);
```

## Safe defaults

- **Metadata is stripped.** EXIF, XMP and the like are not written unless the encoder options ask for them
  (`MetadataOptions.Keep`, or `KeepNonPersonal` to drop GPS position and camera details).
- **Orientation is applied.** A photo taken with the phone sideways comes out upright, and `Fit` and the other steps
  work on the upright image.
- **Colour is never silently shifted.** A wide-gamut photo keeps its colours: JPEG, WebP and GIF convert to sRGB when every
  colour fits and otherwise keep the colour space; PNG keeps it. `ColorSpaceHandling` on the encoder options changes
  this, and the `ToSrgb()` step converts the pixels themselves.
- **Hostile files are bounded.** Dimensions, frame counts and data sizes are checked against `ImageLimits` before
  anything large is allocated.

## Uploads

For files from users, set tighter limits, check the format first, and keep the work on one core per request when the
server already handles many requests at once:

```csharp
var uploads = new DecodeOptions { Limits = new ImageLimits { MaxPixels = 40_000_000 } };

app.MapPost("/avatar", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    FormatPeek peek = await ImageFormats.PeekAsync(request.Body, cancellationToken);
    if (peek.Format is not { IsSupported: true })
    {
        return Results.BadRequest("Upload a PNG, JPEG, GIF or WebP image.");
    }

    string path = Path.Combine(avatars, Guid.NewGuid().ToString("N") + ".webp");
    await ImagePipeline.Load(peek.Stream)
        .WithDecodeOptions(uploads)
        .WithMaxDegreeOfParallelism(1)
        .Fill(256, 256)
        .SaveAsync(path, cancellationToken);
    return Results.Ok();
});
```

`PeekAsync` reads at most 64 bytes and hands back a stream that replays them, so a request body that can't seek still
reaches the decoder whole. `Image.IdentifyAsync` on that stream checks the headers and the limits without decoding.

## Errors

| Exception | When |
|---|---|
| `ArgumentException` and its subclasses | A step's arguments are wrong (when the step is added), a `Crop` reaches outside the image (when it runs), or `SaveAsync`'s extension isn't a format it writes |
| `UnsupportedImageFormatException` | The source isn't PNG, JPEG, GIF or WebP |
| `ImageFormatException` | The file is damaged or ends early |
| `ImageLimitException` | The image is larger than the decode limits allow |
| `NotSupportedException` | The stream can't be written, or the result can't be written in the format (too large, or a colour space it can't hold) |
| `InvalidOperationException` | A pipeline loaded from a stream is run a second time |
| `IOException` | Reading or writing fails |
| `OperationCanceledException` | The cancellation token was cancelled; the pipeline stops between steps |

`UnsupportedImageFormatException`, `ImageFormatException` and `ImageLimitException` share the base class
`ImageException`, so one `catch (ImageException)` covers every problem with the file itself.

## Several outputs from one decode

A pipeline decodes each time it runs. To make several sizes from one upload, decode once and start each pipeline
from the image:

```csharp
using Image photo = await ImagePipeline.Load(upload).WithDecodeOptions(uploads).ToImageAsync(cancellationToken);
await ImagePipeline.From(photo).Fit(1600, 1600).SaveAsync("large.webp", cancellationToken);
await ImagePipeline.From(photo).Fit(400, 400).SaveAsync("small.webp", cancellationToken);
await ImagePipeline.From(photo).Fill(128, 128).SaveAsync("square.webp", cancellationToken);
```

This gives up shrink-on-load, which pays off when only small sizes are wanted.

## Threads

The decoders, the resampler and the encoders split large images across cores. `ImageDefaults.MaxDegreeOfParallelism`
sets the limit for the process (by default every core), and `WithMaxDegreeOfParallelism` overrides it for one
pipeline, including its save. Small images, up to about a quarter of a megapixel, stay on one thread whatever the setting.

## Speed

A 12-megapixel JPEG (4000 x 3000) made into a 400 x 300 thumbnail, BenchmarkDotNet short job, on a machine busy
with other work (2026-11-08). libvips (through NetVips, the engine behind sharp) and Skia use libjpeg-turbo's scaled
decode at 1/4 and libwebp; ImageSharp 2.1 can't shrink on load.

| JPEG to a 400 px thumbnail | One core | 16 cores | Allocated |
|---|---:|---:|---:|
| Zorg.Imaging, to an `Image` | 105 ms | 39 ms | 35 MB |
| Zorg.Imaging, to WebP quality 80 | 128 ms | 68 ms | 36 MB |
| libvips 8.18, to memory | 30 ms | 31 ms | 2 KB managed |
| libvips 8.18, to WebP quality 80 | 40 ms | 42 ms | 14 KB managed |
| SkiaSharp, to a bitmap | 37 ms | 36 ms | 2 KB managed |
| SkiaSharp, to WebP quality 80 | 46 ms | 48 ms | 3 KB managed |
| ImageSharp 2.1, to an image | 107 ms | 88 ms | 64 KB |
| ImageSharp 2.1, to WebP quality 80 | 385 ms | 122 ms | 8 MB |

On all cores the pipeline is within reach of the native libraries; on one core libvips and Skia are about 3x faster.
For a 1600 px web size re-encoded as JPEG 80, all cores make it 63 ms against libvips' 65 and Skia's 156 (from a full
decode), but one core makes it 364 ms against libvips' 70. The time is in the codecs,
not the pipeline: the reduced inverse DCT that shrink-on-load uses is still scalar, the JPEG decoder keeps a
coefficient buffer for the whole frame (most of the 35 MB), and the lossy WebP encoder takes about twice libwebp's
time per pixel. `docs/performance.md` tracks these.
