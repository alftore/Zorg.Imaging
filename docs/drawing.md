# Drawing

`Canvas` draws filled shapes into an `Image`, anti-aliased by exact pixel coverage. It is made for previews of pages
(PDF), charts and badges: shapes, rectangles and, later, lines and placed images. It is new: the API may change, or
move to a package of its own, so using it needs the diagnostic `ZORGDRAW` suppressed.

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
canvas.FillRectangle(20, 260, 360, 0.5f, new Rgba32(0, 0, 0)); // a hairline rule

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

## How it looks

Each pixel is covered by exactly the share of its area the shape covers, so a black edge across half a pixel gives grey
128. Colours blend in the stored values, as PDF viewers, browsers and `Image.DrawImage` blend, so text looks as it does
in a viewer. (Blending in linear light, as `Resize` does, would make thin dark text look lighter and thinner.)

Curves are cut into straight lines at most `Canvas.Tolerance` from them, 0.2 pixels by default, which doesn't show.
Where two parts of one shape with different windings meet inside a single pixel, the even-odd coverage of that pixel is
an approximation, as in FreeType and Skia.

The image must be sRGB or linear sRGB, so a colour means what it says; call `ToSrgb` first for another colour space.
Every pixel layout works; grey images take each colour's Rec. 709 luma.

## Limits

A shape is the caller's data, as an image's size is. Drawing takes time in proportion to the lines and the rows they
cross, and up to 4 bytes per canvas pixel of memory, reused from one call to the next. A program drawing outlines from
untrusted files should limit `Shape.SegmentCount`. Coordinates must be finite; beyond a million pixels they're clamped,
and a curve is cut into at most 1024 lines. `SetClip` limits drawing to a rectangle.

## Not included

Text and fonts (the caller has the glyph outlines), gradients, patterns, dashes, clip paths, blend modes and layers.
Each would be a design decision of its own.
