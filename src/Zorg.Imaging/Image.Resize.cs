using System;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging;

/// <content>Resizing.</content>
public sealed partial class Image
{
    /// <summary>Creates a copy of the image at another size.</summary>
    /// <param name="width">The new width in pixels, or 0 to keep the aspect ratio from <paramref name="height"/>.</param>
    /// <param name="height">The new height in pixels, or 0 to keep the aspect ratio from <paramref name="width"/>.</param>
    /// <param name="resampler">
    /// The filter, or <see langword="null"/> for <see cref="Resampler.Lanczos3"/> along a side that shrinks and
    /// <see cref="Resampler.Mitchell"/> along a side that grows.
    /// </param>
    /// <returns>A new image with the same <see cref="PixelFormat"/> and <see cref="ColorSpace"/>, which the caller disposes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="width"/> or <paramref name="height"/> is negative, or the new image would be too large for an array.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="width"/> and <paramref name="height"/> are both 0.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <remarks>
    /// <para>
    /// When either side shrinks, pixels are blended in linear light, using the image's own transfer curve (sRGB,
    /// Display P3, Rec.2020, PQ or HLG), and alpha is premultiplied, so detail keeps its brightness and transparent
    /// pixels lend no colour to their neighbours. Enlarging blends the stored values, which avoids dark halos at edges.
    /// A colour that covers an area stays exactly the same, whichever filter.
    /// </para>
    /// <para>
    /// A side given as 0 is rounded to the nearest whole pixel, at least 1. The size is the caller's choice, so
    /// <see cref="ImageLimits"/> doesn't apply; the working memory is a few rows of the new width.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// using Image image = Image.Decode(bytes);
    /// using Image thumb = image.Resize(320, 0); // 320 wide, height to match
    /// </code>
    /// </example>
    public Image Resize(int width, int height, Resampler? resampler = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        if (width == 0 && height == 0)
        {
            throw new ArgumentException("Give the width, the height or both; 0 for both leaves no size to keep.", nameof(width));
        }

        _ = Buffer;
        if (width == 0)
        {
            width = Proportional(Width, height, Height, nameof(height));
        }
        else if (height == 0)
        {
            height = Proportional(Height, width, Width, nameof(width));
        }

        if (width == Width && height == Height)
        {
            return Clone();
        }

        return Resizer.Resize(this, width, height, resampler);
    }

    // side × given / of, rounded half up, at least 1.
    private static int Proportional(int side, int given, int of, string parameter)
    {
        long value = Math.Max(((2L * side * given) + of) / (2L * of), 1);
        if (value > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameter, given, "The other side would be more pixels than an image can have.");
        }

        return (int)value;
    }
}
