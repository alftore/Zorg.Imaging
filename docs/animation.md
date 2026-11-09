# Animations

`Image.Decode` and `ImagePipeline` give an animation's first frame. To work with every frame, read it with
`AnimationReader` and write it with `AnimationWriter`. Both go one frame at a time, so a long animation never sits in
memory whole.

```csharp
await using FileStream input = File.OpenRead("cat.gif");
await using FileStream output = File.Create("cat.webp");

using AnimationReader reader = await AnimationReader.OpenAsync(input);
AnimationWriter writer = AnimationWriter.CreateWebp(output, reader.LoopCount, new WebpEncoderOptions { Quality = 75 });
while (await reader.ReadFrameAsync() is { } frame)
{
    using (frame)
    using (Image small = frame.Image.Fit(320, 320))
    {
        await writer.WriteFrameAsync(small, frame.Duration);
    }
}

await writer.CompleteAsync();
```

## Reading

`AnimationReader.Open(byte[])` (then `ReadFrame`) and `AnimationReader.OpenAsync(Stream)` (then `ReadFrameAsync`)
read:

| File | Frames |
|---|---|
| Animated GIF | Every frame, with each frame's disposal, as browsers draw them |
| Animated WebP | Every frame, with blending and disposal, as libwebp's animation decoder draws them |
| Anything else (PNG, APNG, JPEG, a still GIF or WebP) | One frame of duration zero, as `Image.Decode` gives it; for an APNG the first frame, for now |

Every frame is the whole canvas as it shows at that point, in `Rgba32` unless `DecodeOptions.PixelFormat` asks for
another, so it can be resized, cropped or saved like any image. The canvas starts transparent and disposal clears to
transparent, as browsers do. `LoopCount` is how many times the animation plays, 0 for ever. A GIF's loop extension
counts repeats after the first play, as browsers read it, so the reader adds one.

`Duration` is the file's own value. Browsers show GIF frames of 0 or 10 ms for 100 ms; the reader doesn't.

Limits: the canvas is checked against `ImageLimits.MaxPixels` before anything is allocated, and the number of frames
against `ImageLimits.MaxFrames` (10,000 by default) as they are read.

## Writing

`AnimationWriter.CreateGif(stream, loopCount, options)` and `AnimationWriter.CreateWebp(stream, loopCount, options)`.
Give every frame whole and the same size as the first; the writer works out what changed:

- Only the rectangle that changed from the frame before is coded.
- A frame identical to the one before is joined into it, adding the durations.
- Where the next frame makes pixels transparent, the frame before is cleared after it shows.

One frame is held back until the next arrives, so call `Complete` or `CompleteAsync` at the end.

| | GIF | WebP |
|---|---|---|
| Colours | A colour table per frame, up to `MaxColors`, dithered as `Dithering` says; transparency is on or off, from `AlphaThreshold`, `AlphaDithering` and `Matte` | Lossy or lossless as `Lossless` says, with full alpha |
| Durations | Rounded to hundredths of a second | Whole milliseconds |
| Unchanged pixels | Left transparent inside the rectangle, so the frame before shows through and the frame codes smaller | Coded again (frames replace what was there) |
| Written | Frame by frame as they are coded | When completed, since the header holds the file size |
| Loop count | Up to 65,536 plays, or 0 for ever | Up to 65,535, or 0 |

Colour: a frame in another colour space is converted to sRGB unless the options say `ColorSpaceHandling.Keep`, since
one profile covers the whole file. Metadata isn't written.

Speed, one core: 30 frames of 480 x 360 photo panning past (every pixel changes) write in about 0.5 s as GIF, 0.7 s as
lossy WebP and 1.7 s as lossless WebP; they read back in 0.1, 0.3 and 0.2 s.
