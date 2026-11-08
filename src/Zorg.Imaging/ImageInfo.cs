using System.Globalization;

namespace Zorg.Imaging;

/// <summary>What an image's headers say about it, read by <see cref="Image.Identify"/> without decoding any pixels.</summary>
/// <example>
/// <code>
/// IdentifyResult result = Image.Identify(bytes);
/// if (result.Succeeded &amp;&amp; result.Info.IsAnimated)
/// {
///     Console.WriteLine("Only the first frame will be used.");
/// }
/// </code>
/// </example>
public sealed class ImageInfo
{
    internal ImageInfo(ImageFormat format, int width, int height)
    {
        Format = format;
        Width = width;
        Height = height;
    }

    /// <summary>The file format.</summary>
    /// <example><code>response.ContentType = info.Format.MimeType;</code></example>
    public ImageFormat Format { get; }

    /// <summary>The width of the stored pixels, before orientation is applied. For an animation, the canvas width.</summary>
    /// <example><code>long pixels = (long)info.Width * info.Height;</code></example>
    public int Width { get; }

    /// <summary>The height of the stored pixels, before orientation is applied. For an animation, the canvas height.</summary>
    /// <example><code>long pixels = (long)info.Width * info.Height;</code></example>
    public int Height { get; }

    /// <summary>
    /// The EXIF orientation, 1 to 8, with 1 (as stored) when the file has none or an invalid value. Decoding applies
    /// it by default.
    /// </summary>
    /// <example><code>bool rotated = info.Orientation != 1;</code></example>
    public int Orientation { get; internal init; } = 1;

    /// <summary>The width once orientation is applied: <see cref="Height"/> for orientations 5 to 8, which turn the image a quarter.</summary>
    /// <example><code>string size = $"{info.DisplayWidth} x {info.DisplayHeight}";</code></example>
    public int DisplayWidth => SwapsAxes ? Height : Width;

    /// <summary>The height once orientation is applied: <see cref="Width"/> for orientations 5 to 8, which turn the image a quarter.</summary>
    /// <example><code>string size = $"{info.DisplayWidth} x {info.DisplayHeight}";</code></example>
    public int DisplayHeight => SwapsAxes ? Width : Height;

    /// <summary>
    /// Whether the image can have transparent pixels: an alpha channel, a transparent colour or a transparent palette
    /// entry. It says what the format allows, not that any pixel is actually transparent.
    /// </summary>
    /// <example><code>ImageFormat output = info.HasAlpha ? ImageFormat.Png : ImageFormat.Jpeg;</code></example>
    public bool HasAlpha { get; internal init; }

    /// <summary>Whether the file declares more than one frame: APNG, animated WebP, or a GIF with a loop extension or several frames.</summary>
    /// <example><code>bool animated = info.IsAnimated;</code></example>
    public bool IsAnimated { get; internal init; }

    /// <summary>
    /// The number of frames, or <see langword="null"/> when knowing it would mean reading past the headers and
    /// <see cref="IdentifyOptions.CountFrames"/> was <see langword="false"/> (GIF and animated WebP).
    /// </summary>
    /// <example><code>int frames = info.FrameCount ?? 1;</code></example>
    public int? FrameCount { get; internal init; }

    /// <summary>Whether the file carries an ICC colour profile.</summary>
    /// <example><code>bool profiled = info.HasIccProfile;</code></example>
    public bool HasIccProfile { get; internal init; }

    /// <summary>
    /// The colour space decoding will give the pixels, when <see cref="IdentifyOptions.ReadColorSpace"/> asked for it;
    /// otherwise <see langword="null"/>. A CMYK JPEG reports sRGB, since decoding converts it.
    /// </summary>
    /// <example><code>bool wide = info.ColorSpace is { IsSrgb: false };</code></example>
    public ColorSpace? ColorSpace { get; internal init; }

    /// <summary>Whether the file carries an EXIF block. Decoding strips it by default.</summary>
    /// <example><code>bool exif = info.HasExif;</code></example>
    public bool HasExif { get; internal init; }

    private bool SwapsAxes => Orientation is >= 5 and <= 8;

    /// <summary>Returns the format and display size, such as <c>JPEG 3024x4032</c>.</summary>
    /// <returns>A short summary.</returns>
    /// <example><code>Console.WriteLine(info); // JPEG 3024x4032</code></example>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Format.Name} {DisplayWidth}x{DisplayHeight}");
}
