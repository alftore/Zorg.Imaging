using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging;

/// <content>Cropping, fitting, turning, flipping and flattening.</content>
public sealed partial class Image
{
    /// <summary>Creates a copy of a rectangle of the image.</summary>
    /// <param name="x">The left edge, from 0.</param>
    /// <param name="y">The top edge, from 0.</param>
    /// <param name="width">The width, at least 1.</param>
    /// <param name="height">The height, at least 1.</param>
    /// <returns>A new image with the same <see cref="PixelFormat"/> and <see cref="ColorSpace"/>, which the caller disposes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The rectangle isn't wholly inside the image, or is empty.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <example><code>using Image face = photo.Crop(120, 80, 256, 256);</code></example>
    public Image Crop(int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if ((long)x + width > Width || (long)y + height > Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                string.Create(CultureInfo.InvariantCulture, $"The rectangle {width} x {height} at ({x}, {y}) reaches outside the {Width} x {Height} image."));
        }

        _ = Buffer;
        var result = new Image(width, height, PixelFormat) { ColorSpace = ColorSpace, SourceMetadata = SourceMetadata };
        for (int row = 0; row < height; row++)
        {
            GetRowBytes(y + row).Slice(x * BytesPerPixel, result.Stride).CopyTo(result.GetRowBytes(row));
        }

        return result;
    }

    /// <summary>
    /// Shrinks the image to fit inside a box, keeping the aspect ratio. An image already inside the box isn't enlarged.
    /// </summary>
    /// <param name="width">The box width, at least 1.</param>
    /// <param name="height">The box height, at least 1.</param>
    /// <param name="resampler">The filter, or <see langword="null"/> for <see cref="Resampler.Lanczos3"/>.</param>
    /// <returns>A new image no larger than the box, which the caller disposes; a copy when it already fits.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is less than 1.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <remarks>
    /// The usual call for thumbnails and web sizes: a 4000 x 3000 photo fitted to 1600 x 1600 becomes 1600 x 1200, a
    /// 1200 x 900 one stays as it is. Use <see cref="Resize"/> to enlarge.
    /// </remarks>
    /// <example><code>using Image web = photo.Fit(1600, 1600);</code></example>
    public Image Fit(int width, int height, Resampler? resampler = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        _ = Buffer;
        (int w, int h) = Inside(width, height);
        return Resize(w, h, resampler);
    }

    /// <summary>
    /// Fills a box exactly: scales the image to cover it, keeping the aspect ratio, and crops what overhangs equally
    /// from both sides.
    /// </summary>
    /// <param name="width">The width of the result, at least 1.</param>
    /// <param name="height">The height of the result, at least 1.</param>
    /// <param name="resampler">
    /// The filter, or <see langword="null"/> for <see cref="Resampler.Lanczos3"/> when shrinking and
    /// <see cref="Resampler.Mitchell"/> when enlarging.
    /// </param>
    /// <returns>A new image of exactly <paramref name="width"/> x <paramref name="height"/>, which the caller disposes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is less than 1.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <remarks>For square avatars and cards of a fixed shape. A small image is enlarged to fill the box.</remarks>
    /// <example><code>using Image avatar = photo.Fill(256, 256);</code></example>
    public Image Fill(int width, int height, Resampler? resampler = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        _ = Buffer;

        // Crop the source to the box's shape first, centred, then resize: no work on pixels that are thrown away.
        int cropWidth = Width;
        int cropHeight = Height;
        if ((long)Width * height > (long)Height * width)
        {
            cropWidth = (int)Math.Clamp(((2L * Height * width) + height) / (2L * height), 1, Width);
        }
        else
        {
            cropHeight = (int)Math.Clamp(((2L * Width * height) + width) / (2L * width), 1, Height);
        }

        if (cropWidth == Width && cropHeight == Height)
        {
            return Resize(width, height, resampler);
        }

        using Image cropped = Crop((Width - cropWidth) / 2, (Height - cropHeight) / 2, cropWidth, cropHeight);
        return cropped.Resize(width, height, resampler);
    }

    /// <summary>
    /// Shrinks the image to fit inside a box, keeping the aspect ratio, and centres it on a background of exactly the
    /// box's size. An image already inside the box isn't enlarged.
    /// </summary>
    /// <param name="width">The width of the result, at least 1.</param>
    /// <param name="height">The height of the result, at least 1.</param>
    /// <param name="background">
    /// The colour around the image, as sRGB-style 8-bit values: each is scaled to 16 bits for a 16-bit image, grey
    /// images take its luma (BT.709 weights), and formats without alpha ignore <see cref="Rgba32.A"/>.
    /// </param>
    /// <param name="resampler">The filter, or <see langword="null"/> for <see cref="Resampler.Lanczos3"/>.</param>
    /// <returns>A new image of exactly <paramref name="width"/> x <paramref name="height"/>, which the caller disposes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is less than 1.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <example><code>using Image tile = photo.Pad(300, 300, new Rgba32(255, 255, 255));</code></example>
    public Image Pad(int width, int height, Rgba32 background, Resampler? resampler = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        _ = Buffer;
        (int w, int h) = Inside(width, height);
        using Image inner = Resize(w, h, resampler);
        var result = new Image(width, height, PixelFormat) { ColorSpace = ColorSpace, SourceMetadata = SourceMetadata };
        Span<byte> pixel = stackalloc byte[BytesPerPixel];
        Encode(background, PixelFormat, pixel);
        Span<byte> all = result.GetPixelBytes();
        for (int i = 0; i < all.Length; i += pixel.Length)
        {
            pixel.CopyTo(all[i..]);
        }

        int left = (width - w) / 2;
        int top = (height - h) / 2;
        for (int row = 0; row < h; row++)
        {
            inner.GetRowBytes(row).CopyTo(result.GetRowBytes(top + row)[(left * BytesPerPixel)..]);
        }

        return result;
    }

    /// <summary>Creates a copy turned clockwise by a multiple of 90°.</summary>
    /// <param name="degrees">90, 180 or 270 clockwise; negative values turn anticlockwise (-90 is 270); 0 copies.</param>
    /// <returns>A new image, which the caller disposes. 90 and 270 swap the width and height.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="degrees"/> isn't a multiple of 90.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <remarks>Lossless: pixels are moved, not resampled.</remarks>
    /// <example><code>using Image upright = scan.Rotate(90);</code></example>
    public Image Rotate(int degrees)
    {
        if (degrees % 90 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "Only multiples of 90 degrees turn an image without resampling it.");
        }

        _ = Buffer;
        int turns = ((degrees / 90 % 4) + 4) % 4;

        // EXIF orientations: 6 turns 90° clockwise, 3 turns 180°, 8 turns 90° anticlockwise.
        return Orientation.Apply(this, turns switch { 1 => 6, 2 => 3, 3 => 8, _ => 1 });
    }

    /// <summary>Creates a copy mirrored left to right.</summary>
    /// <returns>A new image, which the caller disposes.</returns>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <example><code>using Image mirrored = selfie.FlipHorizontal();</code></example>
    public Image FlipHorizontal()
    {
        _ = Buffer;
        return Orientation.Apply(this, 2);
    }

    /// <summary>Creates a copy mirrored top to bottom.</summary>
    /// <returns>A new image, which the caller disposes.</returns>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <example><code>using Image reflection = image.FlipVertical();</code></example>
    public Image FlipVertical()
    {
        _ = Buffer;
        return Orientation.Apply(this, 4);
    }

    /// <summary>
    /// Removes transparency by drawing the image over a solid colour, for formats such as JPEG that can't carry alpha.
    /// </summary>
    /// <param name="background">The colour behind the image. Its alpha is ignored; 8-bit values are scaled for 16-bit images.</param>
    /// <returns>
    /// A new image, which the caller disposes: <see cref="PixelFormat.Rgb24"/> from an 8-bit image with alpha,
    /// <see cref="PixelFormat.Rgb48"/> from <see cref="PixelFormat.Rgba64"/>, and a copy of an image without alpha.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <remarks>
    /// Colours are blended in the stored values, as browsers draw a transparent image over a page, so the result looks
    /// as the image did on that background.
    /// </remarks>
    /// <example><code>using Image opaque = logo.Flatten(new Rgba32(255, 255, 255));</code></example>
    public Image Flatten(Rgba32 background)
    {
        _ = Buffer;
        switch (PixelFormat)
        {
            case PixelFormat.Rgba32 or PixelFormat.Bgra32:
            {
                var result = new Image(Width, Height, PixelFormat.Rgb24) { ColorSpace = ColorSpace, SourceMetadata = SourceMetadata };
                bool bgra = PixelFormat == PixelFormat.Bgra32;
                for (int y = 0; y < Height; y++)
                {
                    ReadOnlySpan<byte> from = GetRowBytes(y);
                    Span<byte> to = result.GetRowBytes(y);
                    for (int x = 0, i = 0, o = 0; x < Width; x++, i += 4, o += 3)
                    {
                        int a = from[i + 3];
                        int r = bgra ? from[i + 2] : from[i];
                        int b = bgra ? from[i] : from[i + 2];
                        to[o] = Blend8(r, background.R, a);
                        to[o + 1] = Blend8(from[i + 1], background.G, a);
                        to[o + 2] = Blend8(b, background.B, a);
                    }
                }

                return result;
            }

            case PixelFormat.Rgba64:
            {
                var result = new Image(Width, Height, PixelFormat.Rgb48) { ColorSpace = ColorSpace, SourceMetadata = SourceMetadata };
                for (int y = 0; y < Height; y++)
                {
                    ReadOnlySpan<ushort> from = MemoryMarshal.Cast<byte, ushort>(GetRowBytes(y));
                    Span<ushort> to = MemoryMarshal.Cast<byte, ushort>(result.GetRowBytes(y));
                    for (int x = 0, i = 0, o = 0; x < Width; x++, i += 4, o += 3)
                    {
                        int a = from[i + 3];
                        to[o] = Blend16(from[i], background.R * 257, a);
                        to[o + 1] = Blend16(from[i + 1], background.G * 257, a);
                        to[o + 2] = Blend16(from[i + 2], background.B * 257, a);
                    }
                }

                return result;
            }

            default:
                return Clone();
        }
    }

    // The colour as one pixel of a format.
    private static void Encode(Rgba32 colour, PixelFormat format, Span<byte> pixel)
    {
        int luma = ((2126 * colour.R) + (7152 * colour.G) + (722 * colour.B) + 5000) / 10000;
        switch (format)
        {
            case PixelFormat.Rgba32:
                pixel[0] = colour.R;
                pixel[1] = colour.G;
                pixel[2] = colour.B;
                pixel[3] = colour.A;
                break;
            case PixelFormat.Bgra32:
                pixel[0] = colour.B;
                pixel[1] = colour.G;
                pixel[2] = colour.R;
                pixel[3] = colour.A;
                break;
            case PixelFormat.Rgb24:
                pixel[0] = colour.R;
                pixel[1] = colour.G;
                pixel[2] = colour.B;
                break;
            case PixelFormat.L8:
                pixel[0] = (byte)luma;
                break;
            default:
                Span<ushort> wide = MemoryMarshal.Cast<byte, ushort>(pixel);
                if (format == PixelFormat.L16)
                {
                    wide[0] = (ushort)(luma * 257);
                    break;
                }

                wide[0] = (ushort)(colour.R * 257);
                wide[1] = (ushort)(colour.G * 257);
                wide[2] = (ushort)(colour.B * 257);
                if (format == PixelFormat.Rgba64)
                {
                    wide[3] = (ushort)(colour.A * 257);
                }

                break;
        }
    }

    private static byte Blend8(int colour, int background, int alpha) =>
        (byte)(((colour * alpha) + (background * (255 - alpha)) + 127) / 255);

    private static ushort Blend16(int colour, int background, int alpha) =>
        (ushort)((((long)colour * alpha) + ((long)background * (65535 - alpha)) + 32767) / 65535);

    /// <summary>The largest size inside a box with an image's aspect ratio, never larger than the image.</summary>
    internal static (int Width, int Height) Inside(int imageWidth, int imageHeight, int width, int height)
    {
        if (imageWidth <= width && imageHeight <= height)
        {
            return (imageWidth, imageHeight);
        }

        // Whichever side is tighter decides; the other is rounded to the nearest pixel and kept inside the box.
        if ((long)imageWidth * height > (long)imageHeight * width)
        {
            return (width, (int)Math.Clamp(((2L * imageHeight * width) + imageWidth) / (2L * imageWidth), 1, height));
        }

        return ((int)Math.Clamp(((2L * imageWidth * height) + imageHeight) / (2L * imageHeight), 1, width), height);
    }

    /// <summary>The smallest size with an image's aspect ratio that covers a box: what <see cref="Fill"/> resizes to before it crops.</summary>
    internal static (int Width, int Height) Covering(int imageWidth, int imageHeight, int width, int height) =>
        (long)imageWidth * height > (long)imageHeight * width
            ? ((int)Math.Min(((2L * imageWidth * height) + imageHeight) / (2L * imageHeight), int.MaxValue), height)
            : (width, (int)Math.Min(((2L * imageHeight * width) + imageWidth) / (2L * imageWidth), int.MaxValue));

    private (int Width, int Height) Inside(int width, int height) => Inside(Width, Height, width, height);

    /// <summary>Converts the pixels to sRGB, clipping colours outside it.</summary>
    /// <returns>A new image in the same pixel format, with <see cref="ColorSpace"/> sRGB, which the caller disposes; a copy when the image is sRGB already.</returns>
    /// <exception cref="NotSupportedException">
    /// The colour space is HDR (PQ or HLG), which needs tone mapping, or an ICC profile that isn't matrix/TRC.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <remarks>
    /// Each channel goes through its curve to linear light, through the ICC connection space into linear sRGB, and back
    /// through the sRGB curve. Encoders do this themselves under <see cref="ColorSpaceHandling"/>; this is for when the
    /// pixels themselves are wanted in sRGB.
    /// </remarks>
    /// <example><code>using Image srgb = photo.ToSrgb();</code></example>
    public Image ToSrgb()
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        if (ColorSpace.IsSrgb)
        {
            return Clone();
        }

        return SrgbConverter.Convert(this, onlyIfInside: false)
            ?? throw new NotSupportedException(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"The colour space {ColorSpace.Name} can't be converted to sRGB: HDR needs tone mapping, and only matrix/TRC ICC profiles are understood."));
    }
}
