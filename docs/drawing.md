# Drawing

`Canvas` draws shapes, lines and images into an `Image`, anti-aliased by exact pixel coverage. It is made for
previews of pages (PDF), charts and badges. It is new: the API may change, or move to a package of its own, so using it
needs the diagnostic `ZORGDRAW` suppressed.

```xml
<NoWarn>$(NoWarn);ZORGDRAW</NoWarn>
```

```csharp
using System.Numerics;
using Zorg.Imaging;
using Zorg.Imaging.Drawing;

using var page = new Image(400, 300, PixelFormat.Rgb24);
using var canvas = new Canvas(page);
canvas.Clear(new Rgba32(255, 255, 255));

var triangle = new Shape().MoveTo(20, 20).LineTo(180, 40).LineTo(90, 160).Close();
canvas.Fill(triangle, new Rgba32(200, 30, 30));
canvas.Fill(Shape.Ellipse(280, 150, 80, 60), new Rgba32(30, 90, 200, 180)); // translucent
canvas.StrokeLine(20, 260, 380, 260, 0, new Rgba32(0, 0, 0)); // a hairline rule
canvas.Stroke(triangle, 4, new Rgba32(0, 0, 0)); // its outline, with round corners
canvas.DrawImage(photo, 220, 20, 160, 100); // a photo, shrunk to fit

page.SaveAsPng(output);
```

Nothing else in the library uses the canvas, so it costs a program that doesn't draw nothing: .NET loads code only
when it is used, and trimmed or AOT builds leave it out.

## Shapes

A `Shape` is one or more figures of lines (`LineTo`), quadratic curves (`QuadTo`, as TrueType glyphs use) and cubic
curves (`CubicTo`, as PDF, SVG and CFF fonts use), each started by `MoveTo`. `Shape.Rectangle` and `Shape.Ellipse`
make the common ones. Build a shape once, say a glyph in font units, and fill it many times with a transform:

```csharp
Matrix3x2 place = Matrix3x2.CreateScale(size / unitsPerEm, -size / unitsPerEm) * Matrix3x2.CreateTranslation(x, baseline);
canvas.Fill(glyph, black, place);
```

`Canvas.Transform` applies after each call's own transform, for a mapping that holds for the whole page, such as
points to pixels.

Filling closes every figure. `FillRule.NonZero` (the default, as TrueType, PDF's `f` and SVG use) counts which way
round each outline goes, so a hole needs its outline drawn the other way; `FillRule.EvenOdd` makes every second
crossing a hole whatever the direction.

## Strokes

`StrokeLine` draws a straight line with square-cut ends, for rules and underlines. `Stroke` draws a shape's outline
with a round pen: round corners and round ends, joined back to the start only where `Close` ended a figure. Synthetic
bold strokes a glyph as well as filling it (PDF's `2 Tr`), about 0.03 em wide.

Widths are in the shape's units and scale with the transform, as PDF's line width does. `MinimumStrokeWidth`, 1 pixel
by default, keeps hairlines (width 0) visible, as PDF viewers draw them: a pen narrower than that in every direction is
drawn that many pixels wide, however the transform stretches. A translucent stroke has no darker spots
where its pieces overlap. Miter and bevel corners and dashes aren't supported.

## Images

`DrawImage` places an image into a rectangle, or through any transform from its pixels: scaled, turned, sheared or
mirrored, with anti-aliased edges. When the image is drawn smaller than it is, it is first resized with `Resize`
(Lanczos3 in linear light by default, or the resampler given), so no detail is skipped; then each pixel is sampled
bilinearly, without letting transparent pixels lend their colour. `Resampler.Nearest` draws pixels as blocks instead,
for pixel art and enlarged screenshots. The image must have the canvas's colour space.

## How it looks

Each pixel is covered by exactly the share of its area the shape covers, so a black edge across half a pixel gives grey
128. Colours blend in the stored values, as PDF viewers, browsers and `Image.DrawImage` blend, so text looks as it does
in a viewer. (Blending in linear light, as `Resize` does, would make thin dark text look lighter and thinner.)

Curves are cut into straight lines at most `Canvas.Tolerance` from them, 0.2 pixels by default, which doesn't show.
Where two parts of one shape overlap inside a single pixel (a glyph's stem over its bowl, an outline crossing itself,
a stroke's separate figures), that pixel's coverage is an approximation, as in FreeType's and Skia's analytic paths:
a few edge pixels come out a little darker. A stroke's own corners don't overlap, so a single outline is exact.

The Linux gate checks fills and strokes against librsvg's `rsvg-convert` (cairo): the total ink agrees within 0.3 %
for fills and 0.5 % for strokes, and the ink of every 4 x 4 block within a third of a pixel for fills.

The image must be sRGB or linear sRGB, so a colour means what it says; call `ToSrgb` first for another colour space.
Every pixel layout works; grey images take each colour's Rec. 709 luma.

## Limits

A shape is the caller's data, as an image's size is. Drawing takes time in proportion to the lines and the rows they
cross. Its working memory is at most 4 MB however large the canvas, plus 16 bytes per line of the largest shape
drawn, reused from one call to the next; a page of glyphs allocates nothing. A program drawing outlines from
untrusted files should limit `Shape.SegmentCount`. Coordinates must be finite; beyond a million pixels they're clamped,
and a curve is cut into at most 1024 lines. `SetClip` limits drawing to a rectangle.

## Not included

Text and fonts (the caller has the glyph outlines), gradients, patterns, dashes, miter corners, clip paths, blend modes
and layers. Each would be a design decision of its own.
