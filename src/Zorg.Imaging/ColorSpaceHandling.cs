namespace Zorg.Imaging;

/// <summary>What an encoder does with an image whose <see cref="Image.ColorSpace"/> isn't sRGB.</summary>
/// <remarks>
/// Only colour spaces that can be converted are: Display P3, Rec. 2020 and other spaces known by their primaries with
/// an SDR curve, PNG gamma and chromaticities, and ICC matrix/TRC profiles. HDR (PQ, HLG) and LUT-based profiles are
/// always kept as they are, whatever the setting, because converting them would shift colours.
/// </remarks>
/// <example>
/// <code>
/// image.SaveAsJpeg(output, new JpegEncoderOptions { ColorSpaceHandling = ColorSpaceHandling.Keep });
/// </code>
/// </example>
public enum ColorSpaceHandling
{
    /// <summary>
    /// Convert to sRGB and write no profile when every pixel fits inside sRGB (to within one 8-bit step), which most
    /// photos do; otherwise keep the colour space, so saturated colours aren't clipped. Small files without shifting
    /// colours: the default for JPEG, WebP and GIF.
    /// </summary>
    SrgbWhenLossless = 0,

    /// <summary>Always convert to sRGB, clipping colours outside it. The smallest files, and what every viewer shows alike.</summary>
    Srgb = 1,

    /// <summary>Never convert: write the colour space as it is. The default for PNG, where keeping it costs a few bytes.</summary>
    Keep = 2,
}
