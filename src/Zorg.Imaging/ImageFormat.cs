using System.Collections.Generic;

namespace Zorg.Imaging;

/// <summary>An image file format that <see cref="ImageFormats"/> can recognise from the first bytes of a file.</summary>
/// <remarks>
/// <para>
/// Each format has one instance, so formats can be compared with <c>==</c> or matched in a <c>switch</c>.
/// Recognising a format doesn't mean it can be decoded: check <see cref="IsSupported"/>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// ImageFormat? format = ImageFormats.Detect(head);
/// if (format == ImageFormat.Tiff)
/// {
///     return Results.Problem("TIFF isn't supported. Upload a PNG, JPEG, GIF or WebP image.", statusCode: 415);
/// }
/// </code>
/// </example>
public sealed class ImageFormat
{
    private ImageFormat(string name, string mimeType, bool isSupported, params string[] fileExtensions)
    {
        Name = name;
        MimeType = mimeType;
        IsSupported = isSupported;
        FileExtensions = fileExtensions;
    }

    /// <summary>PNG, including animated PNG (APNG).</summary>
    /// <example><code>bool png = ImageFormats.Detect(head) == ImageFormat.Png;</code></example>
    public static ImageFormat Png { get; } = new("PNG", "image/png", isSupported: true, ".png", ".apng");

    /// <summary>JPEG (JFIF and EXIF files).</summary>
    /// <example><code>bool jpeg = ImageFormats.Detect(head) == ImageFormat.Jpeg;</code></example>
    public static ImageFormat Jpeg { get; } = new("JPEG", "image/jpeg", isSupported: true, ".jpg", ".jpeg", ".jpe", ".jfif");

    /// <summary>GIF (87a and 89a), including animated GIF.</summary>
    /// <example><code>bool gif = ImageFormats.Detect(head) == ImageFormat.Gif;</code></example>
    public static ImageFormat Gif { get; } = new("GIF", "image/gif", isSupported: true, ".gif");

    /// <summary>WebP, lossy or lossless, still or animated.</summary>
    /// <example><code>bool webp = ImageFormats.Detect(head) == ImageFormat.Webp;</code></example>
    public static ImageFormat Webp { get; } = new("WebP", "image/webp", isSupported: true, ".webp");

    /// <summary>AVIF. Recognised, not supported.</summary>
    /// <example><code>bool avif = ImageFormats.Detect(head) == ImageFormat.Avif;</code></example>
    public static ImageFormat Avif { get; } = new("AVIF", "image/avif", isSupported: false, ".avif");

    /// <summary>HEIF, including HEIC from phone cameras. Recognised, not supported.</summary>
    /// <example><code>bool heif = ImageFormats.Detect(head) == ImageFormat.Heif;</code></example>
    public static ImageFormat Heif { get; } = new("HEIF", "image/heif", isSupported: false, ".heic", ".heif");

    /// <summary>JPEG XL, as a bare codestream or in its container. Recognised, not supported.</summary>
    /// <example><code>bool jxl = ImageFormats.Detect(head) == ImageFormat.JpegXl;</code></example>
    public static ImageFormat JpegXl { get; } = new("JPEG XL", "image/jxl", isSupported: false, ".jxl");

    /// <summary>TIFF, little- or big-endian. Recognised, not supported.</summary>
    /// <example><code>bool tiff = ImageFormats.Detect(head) == ImageFormat.Tiff;</code></example>
    public static ImageFormat Tiff { get; } = new("TIFF", "image/tiff", isSupported: false, ".tif", ".tiff");

    /// <summary>Windows bitmap (BMP). Recognised, not supported.</summary>
    /// <example><code>bool bmp = ImageFormats.Detect(head) == ImageFormat.Bmp;</code></example>
    public static ImageFormat Bmp { get; } = new("BMP", "image/bmp", isSupported: false, ".bmp", ".dib");

    /// <summary>Windows icon (ICO). Recognised, not supported.</summary>
    /// <example><code>bool ico = ImageFormats.Detect(head) == ImageFormat.Ico;</code></example>
    public static ImageFormat Ico { get; } = new("ICO", "image/vnd.microsoft.icon", isSupported: false, ".ico");

    /// <summary>Photoshop document (PSD). Recognised, not supported.</summary>
    /// <example><code>bool psd = ImageFormats.Detect(head) == ImageFormat.Psd;</code></example>
    public static ImageFormat Psd { get; } = new("PSD", "image/vnd.adobe.photoshop", isSupported: false, ".psd");

    /// <summary>Quite OK Image format (QOI). Recognised, not supported.</summary>
    /// <example><code>bool qoi = ImageFormats.Detect(head) == ImageFormat.Qoi;</code></example>
    public static ImageFormat Qoi { get; } = new("QOI", "image/qoi", isSupported: false, ".qoi");

    /// <summary>The short display name, such as <c>PNG</c> or <c>WebP</c>.</summary>
    /// <example><code>string message = $"{format.Name} isn't supported.";</code></example>
    public string Name { get; }

    /// <summary>The media type, such as <c>image/png</c>, for a <c>Content-Type</c> header.</summary>
    /// <example><code>response.ContentType = format.MimeType;</code></example>
    public string MimeType { get; }

    /// <summary>The usual file extensions, lower case with the leading dot, most common first.</summary>
    /// <example><code>string fileName = "upload" + format.FileExtensions[0];</code></example>
    public IReadOnlyList<string> FileExtensions { get; }

    /// <summary>Whether this library can identify and decode the format. True for PNG, JPEG, GIF and WebP.</summary>
    /// <example>
    /// <code>
    /// if (ImageFormats.Detect(head) is not { IsSupported: true })
    /// {
    ///     return Results.BadRequest("Upload a PNG, JPEG, GIF or WebP image.");
    /// }
    /// </code>
    /// </example>
    public bool IsSupported { get; }

    /// <summary>Returns <see cref="Name"/>.</summary>
    /// <returns>The format's display name.</returns>
    /// <example><code>Console.WriteLine(ImageFormat.Png); // PNG</code></example>
    public override string ToString() => Name;
}
