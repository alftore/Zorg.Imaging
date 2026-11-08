using System;
using System.Globalization;
using System.Numerics;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging;

/// <content>Blurring, sharpening, drawing one image on another, trimming borders, and BlurHash.</content>
public sealed partial class Image
{
    /// <summary>The largest blur or sharpen radius, as a standard deviation in pixels.</summary>
    /// <example><code>double most = Image.MaxSigma;</code></example>
    public const double MaxSigma = 1000;

    /// <summary>Blurs the image with a Gaussian.</summary>
    /// <param name="sigma">The standard deviation in pixels, more than 0 and at most <see cref="MaxSigma"/>; the blur reaches about three times as far.</param>
    /// <returns>A new image of the same size, format and colour space, which the caller disposes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sigma"/> is out of range.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <remarks>
    /// The blur is done in linear light with alpha premultiplied, as resizing is, so bright detail doesn't turn dark
    /// and transparent pixels don't bleed their colour; CSS's <c>blur()</c> blends in linear light too. At the edges only
    /// the pixels inside the image count.
    /// </remarks>
    /// <example><code>using Image backdrop = photo.Fit(800, 800).Blur(12);</code></example>
    public Image Blur(double sigma)
    {
        CheckSigma(sigma);
        _ = Buffer;
        return Resizer.Blur(this, sigma, linear: true);
    }

    /// <summary>Sharpens the image with an unsharp mask: each pixel moves away from its blurred surroundings.</summary>
    /// <param name="sigma">The blur's standard deviation in pixels, more than 0 and at most <see cref="MaxSigma"/>: about 0.5 to 1 for detail after a resize, more for broader contrast.</param>
    /// <param name="amount">How far to move, more than 0 and at most 10: 1 doubles the difference from the blur.</param>
    /// <returns>A new image of the same size, format and colour space, which the caller disposes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sigma"/> or <paramref name="amount"/> is out of range.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <remarks>
    /// Sharpening works on the stored values, as image editors' unsharp masks do, and leaves alpha alone. A light
    /// sharpen after shrinking (sigma 0.5 to 1, amount 0.3 to 0.7) restores the crispness that resampling softens.
    /// </remarks>
    /// <example><code>using Image crisp = photo.Fit(800, 800).Sharpen(0.7, 0.5);</code></example>
    public Image Sharpen(double sigma = 1, double amount = 1)
    {
        CheckSigma(sigma);
        if (!(amount > 0 && amount <= 10))
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "The amount must be more than 0 and at most 10.");
        }

        _ = Buffer;
        using Image blurred = Resizer.Blur(this, sigma, linear: false);
        var result = new Image(Width, Height, PixelFormat) { ColorSpace = ColorSpace, SourceMetadata = SourceMetadata };
        int bands = Parallelism.Bands((long)Width * Height, Height);
        try
        {
            SharpenRows(blurred, result, (float)amount, bands);
        }
        catch
        {
            result.Dispose();
            throw;
        }

        return result;
    }

    private void SharpenRows(Image blurred, Image result, float k, int bands) =>
        Parallelism.For(bands, band =>
        {
            var row = new Vector4[Width];
            var blur = new Vector4[Width];
            for (int y = Height * band / bands; y < Height * (band + 1) / bands; y++)
            {
                FloatRows.Read(this, y, row);
                FloatRows.Read(blurred, y, blur);
                for (int x = 0; x < Width; x++)
                {
                    Vector4 p = row[x];
                    Vector4 sharp = p + (k * (p - blur[x]));
                    row[x] = new Vector4(sharp.X, sharp.Y, sharp.Z, p.W);
                }

                FloatRows.Write(result, y, row);
            }
        });

    /// <summary>Draws another image over this one, as for a watermark or a badge.</summary>
    /// <param name="overlay">The image to draw; its transparency is kept. It isn't changed.</param>
    /// <param name="x">Where its left edge goes; it may start outside the image, and only the part inside is drawn.</param>
    /// <param name="y">Where its top edge goes, likewise.</param>
    /// <param name="opacity">How opaque to draw it, more than 0 and at most 1 (the default).</param>
    /// <returns>
    /// A new image of this one's size and colour space, which the caller disposes: in this image's format, except that a
    /// grey image takes colour when the overlay has it.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="overlay"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="opacity"/> is out of range.</exception>
    /// <exception cref="ObjectDisposedException">An image has been disposed.</exception>
    /// <remarks>
    /// Colours are blended in the stored values, as browsers layer images, and taken as they are: give the overlay in
    /// this image's colour space (both sRGB, usually). To place it from the right or bottom, use
    /// <c>image.Width - overlay.Width - margin</c>.
    /// </remarks>
    /// <example>
    /// <code>
    /// using Image logo = Image.Decode(logoBytes);
    /// using Image marked = photo.DrawImage(logo, photo.Width - logo.Width - 16, photo.Height - logo.Height - 16, opacity: 0.6);
    /// </code>
    /// </example>
    public Image DrawImage(Image overlay, int x, int y, double opacity = 1)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (!(opacity > 0 && opacity <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(opacity), opacity, "The opacity must be more than 0 and at most 1.");
        }

        _ = Buffer;
        _ = overlay.Buffer;
        PixelFormat format = PixelFormat;
        if (FloatRows.IsGrey(format) && !FloatRows.IsGrey(overlay.PixelFormat))
        {
            bool sixteen = FloatRows.IsSixteen(format);
            format = sixteen ? PixelFormat.Rgb48 : PixelFormat.Rgb24;
        }

        var result = new Image(Width, Height, format) { ColorSpace = ColorSpace, SourceMetadata = SourceMetadata };
        int left = Math.Max(x, 0);
        int right = (int)Math.Min((long)x + overlay.Width, Width);
        int top = Math.Max(y, 0);
        int bottom = (int)Math.Min((long)y + overlay.Height, Height);
        float alpha = (float)opacity;
        var row = new Vector4[Width];
        var over = new Vector4[overlay.Width];
        for (int r = 0; r < Height; r++)
        {
            FloatRows.Read(this, r, row);
            if (r >= top && r < bottom && right > left)
            {
                FloatRows.Read(overlay, r - y, over);
                for (int c = left; c < right; c++)
                {
                    row[c] = Over(over[c - x], row[c], alpha);
                }
            }

            FloatRows.Write(result, r, row);
        }

        return result;
    }

    /// <summary>
    /// Crops away borders of one colour: rows and columns at the edges whose pixels all match the top-left pixel.
    /// </summary>
    /// <param name="threshold">How far a channel may be from the border colour and still match, 0 to 255 (in 8-bit steps, for 16-bit images too).</param>
    /// <returns>
    /// A new image, which the caller disposes: the smallest rectangle holding every pixel that doesn't match, or a copy
    /// when the whole image matches.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threshold"/> is out of range.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <remarks>Fully transparent pixels match each other whatever colour they carry.</remarks>
    /// <example><code>using Image tight = scan.Trim(threshold: 12);</code></example>
    public Image Trim(int threshold = 10)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(threshold);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(threshold, 255);
        _ = Buffer;
        var row = new Vector4[Width];
        FloatRows.Read(this, 0, row);
        Vector4 border = row[0];
        float limit = (threshold + 0.5f) / 255;
        int left = Width;
        int right = 0;
        int top = Height;
        int bottom = 0;
        for (int y = 0; y < Height; y++)
        {
            FloatRows.Read(this, y, row);
            for (int x = 0; x < Width; x++)
            {
                Vector4 p = row[x];
                bool matches = (p.W == 0 && border.W == 0) || Vector4.Abs(p - border) is { } d && Math.Max(Math.Max(d.X, d.Y), Math.Max(d.Z, d.W)) < limit;
                if (!matches)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x + 1);
                    top = Math.Min(top, y);
                    bottom = y + 1;
                }
            }
        }

        return right > left ? Crop(left, top, right - left, bottom - top) : Clone();
    }

    /// <summary>A BlurHash of the image: a short string a page can turn into a blurred placeholder before the image loads.</summary>
    /// <param name="componentsX">How many horizontal frequencies, 1 to 9: more keep more detail, in a longer string.</param>
    /// <param name="componentsY">How many vertical frequencies, 1 to 9.</param>
    /// <returns>The hash, 6 + 2 × (componentsX × componentsY - 1) characters of BlurHash's base 83.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A component count is out of range.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <remarks>
    /// The BlurHash algorithm (blurha.sh): the image's colours as a few cosine frequencies in linear light. A large
    /// image is first shrunk to fit in 64 x 64, which changes the result very little and costs far less. Alpha is ignored,
    /// so transparent pixels count with the colour they carry; flatten first when that matters.
    /// </remarks>
    /// <example><code>string placeholder = photo.ToBlurHash(); // e.g. "LEHV6nWB2yk8pyo0adR*.7kCMdnj"</code></example>
    public string ToBlurHash(int componentsX = 4, int componentsY = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(componentsX, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(componentsX, 9);
        ArgumentOutOfRangeException.ThrowIfLessThan(componentsY, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(componentsY, 9);
        _ = Buffer;
        if (Width > 64 || Height > 64)
        {
            using Image small = Fit(64, 64);
            return BlurHash.Encode(small, componentsX, componentsY);
        }

        return BlurHash.Encode(this, componentsX, componentsY);
    }

    private static void CheckSigma(double sigma)
    {
        if (!(sigma > 0 && sigma <= MaxSigma))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sigma),
                sigma,
                string.Create(CultureInfo.InvariantCulture, $"The standard deviation must be more than 0 and at most {MaxSigma} pixels."));
        }
    }

    // Straight-alpha "over": the overlay, at the opacity, over the pixel.
    private static Vector4 Over(Vector4 source, Vector4 target, float opacity)
    {
        float sa = source.W * opacity;
        float behind = target.W * (1 - sa);
        float a = sa + behind;
        if (a <= 0)
        {
            return Vector4.Zero;
        }

        Vector3 colour = ((new Vector3(source.X, source.Y, source.Z) * sa) + (new Vector3(target.X, target.Y, target.Z) * behind)) / a;
        return new Vector4(colour, a);
    }
}
