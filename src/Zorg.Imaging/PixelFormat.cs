namespace Zorg.Imaging;

/// <summary>How an <see cref="Image"/> lays out each pixel in memory: 8 or 16 bits per channel.</summary>
/// <remarks>
/// Colour values are encoded as the image's <see cref="Image.ColorSpace"/> says, as stored in the file: usually sRGB.
/// 16-bit channels are unsigned, in the machine's byte order. Alpha is straight, not premultiplied: a half-transparent
/// red is <c>(255, 0, 0, 128)</c>.
/// </remarks>
/// <example><code>using var image = new Image(640, 480, PixelFormat.Rgba32);</code></example>
public enum PixelFormat
{
    /// <summary>No format: the default value, never the format of an image.</summary>
    None = 0,

    /// <summary>Red, green, blue and alpha, one byte each: <see cref="Imaging.Rgba32"/>. The usual format for decoding.</summary>
    Rgba32 = 1,

    /// <summary>Blue, green, red and alpha, one byte each: <see cref="Imaging.Bgra32"/>. The layout Windows and Skia use.</summary>
    Bgra32 = 2,

    /// <summary>Red, green and blue, one byte each, no alpha: <see cref="Imaging.Rgb24"/>. What a colour JPEG holds.</summary>
    Rgb24 = 3,

    /// <summary>One byte of luminance, no alpha: <see cref="Imaging.L8"/>. What a greyscale JPEG holds.</summary>
    L8 = 4,

    /// <summary>One 16-bit luminance, no alpha: <see cref="Imaging.L16"/>. What a 16-bit greyscale PNG holds.</summary>
    L16 = 5,

    /// <summary>Red, green and blue, 16 bits each, no alpha: <see cref="Imaging.Rgb48"/>. What a 16-bit RGB PNG holds.</summary>
    Rgb48 = 6,

    /// <summary>
    /// Red, green, blue and alpha, 16 bits each: <see cref="Imaging.Rgba64"/>. Holds any 8- or 16-bit file exactly, and
    /// HDR and wide-gamut photos without banding.
    /// </summary>
    Rgba64 = 7,
}
