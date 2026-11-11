using Zorg.Imaging.OsCodecs;

namespace Zorg.Imaging;

/// <summary>
/// AVIF and HEIC decoding through the operating system's own decoders: Windows Imaging Component on Windows, ImageIO
/// on macOS. Add the codecs once at start-up, or per call.
/// </summary>
/// <remarks>
/// <para>
/// Zorg.Imaging reads each file's boxes itself and checks the size and tile count against <see cref="ImageLimits"/>
/// before the operating system sees a byte. The decoding itself is native code: it runs in your process, can't be
/// cancelled once started, and a fault in it isn't something managed code can catch. The operating system's updates
/// keep it patched. See the package readme for what each system needs.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// ImageDefaults.Codecs = SystemCodecs.All;
/// using Image photo = Image.Decode(File.ReadAllBytes("IMG_0001.HEIC"));
/// </code>
/// </example>
public static class SystemCodecs
{
    private static readonly SystemDecoder Decoder = SystemDecoder.ForThisSystem();

    /// <summary>HEIC (HEVC in HEIF), as iPhones and many Android phones save photos.</summary>
    /// <example><code>var options = new DecodeOptions { Codecs = ImageCodecs.Empty.Add(SystemCodecs.Heif) };</code></example>
    public static ImageCodec Heif { get; } = new SystemCodec(ImageFormat.Heif, Decoder);

    /// <summary>AVIF (AV1 in HEIF).</summary>
    /// <example><code>var options = new DecodeOptions { Codecs = ImageCodecs.Empty.Add(SystemCodecs.Avif) };</code></example>
    public static ImageCodec Avif { get; } = new SystemCodec(ImageFormat.Avif, Decoder);

    /// <summary>Both codecs, <see cref="Heif"/> and <see cref="Avif"/>.</summary>
    /// <example><code>ImageDefaults.Codecs = SystemCodecs.All;</code></example>
    public static ImageCodecs All { get; } = ImageCodecs.Empty.Add(Heif, Avif);
}
