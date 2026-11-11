using System;

namespace Zorg.Imaging;

/// <summary>
/// A decoder for a format the library recognises but doesn't decode itself, such as AVIF or HEIC, added by another
/// package. Add codecs to <see cref="DecodeOptions.Codecs"/>, <see cref="IdentifyOptions.Codecs"/> or
/// <see cref="ImageDefaults.Codecs"/>; nothing is discovered automatically.
/// </summary>
/// <remarks>
/// <para>
/// The library does the parts that keep hostile files safe, so every codec gets them:
/// </para>
/// <list type="number">
/// <item>It recognises the format from the first bytes (<see cref="ImageFormats.Detect"/>) and picks the first available
/// codec for it. A format the library decodes itself is never given to a codec.</item>
/// <item>It calls <see cref="ReadHeader"/> with at most <see cref="ImageLimits.MaxHeaderBytes"/> bytes from the start of the
/// file, then checks the size against <see cref="ImageLimits.MaxPixels"/> before anything else happens.</item>
/// <item>It reads the whole file, at most <see cref="ImageLimits.MaxFileBytes"/>, and calls <see cref="Decode"/>.</item>
/// <item>It checks that the image is the size and pixel format promised, then applies the orientation when
/// <see cref="DecodeOptions.ApplyOrientation"/> asks for it.</item>
/// <item>An exception other than an <see cref="ImageException"/> or <see cref="OperationCanceledException"/> becomes an
/// <see cref="ImageCodecException"/> naming the codec.</item>
/// </list>
/// <para>
/// A codec must be safe to call from several threads at once, allocate nothing beyond the limits in
/// <see cref="CodecContext.Limits"/>, and throw <see cref="ImageFormatException"/> for a damaged file. Both methods are
/// synchronous: <see cref="Image.Decode(byte[], DecodeOptions?)"/> must never wait on another thread.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var options = new DecodeOptions { Codecs = ImageCodecs.Empty.Add(new MyHeicCodec()) };
/// using Image photo = Image.Decode(bytes, options);
/// </code>
/// </example>
public abstract class ImageCodec
{
    /// <summary>Creates the codec.</summary>
    /// <example><code>public sealed class MyHeicCodec : ImageCodec { /* ... */ }</code></example>
    protected ImageCodec()
    {
    }

    /// <summary>The format this codec decodes. It must be one the library recognises (<see cref="ImageFormats.Known"/>).</summary>
    /// <example><code>public override ImageFormat Format => ImageFormat.Heif;</code></example>
    public abstract ImageFormat Format { get; }

    /// <summary>A short name for messages, such as <c>"HEIC (Windows Imaging Component)"</c>. Defaults to the type name.</summary>
    /// <example><code>public override string Name => "HEIC (ImageIO)";</code></example>
    public virtual string Name => GetType().Name;

    /// <summary>
    /// Whether the codec runs native code, such as an operating system decoder, which the library's limits can't fully
    /// protect. Defaults to <see langword="false"/>.
    /// </summary>
    /// <example><code>public override bool UsesNativeCode => true;</code></example>
    public virtual bool UsesNativeCode => false;

    /// <summary>
    /// Whether the codec can run on this machine, and why not. Defaults to <see cref="CodecAvailability.Available"/>.
    /// An unavailable codec is skipped, and a file only it could decode fails with its reason.
    /// </summary>
    /// <example><code>public override CodecAvailability Availability => OperatingSystem.IsMacOS() ? CodecAvailability.Available : CodecAvailability.Unavailable("HEIC decoding needs macOS.");</code></example>
    public virtual CodecAvailability Availability => CodecAvailability.Available;

    /// <summary>Reads the size and properties from the start of the file, without decoding pixels.</summary>
    /// <param name="head">The start of the file: all of it, or the first <see cref="ImageLimits.MaxHeaderBytes"/> bytes.</param>
    /// <param name="context">The limits and the cancellation token.</param>
    /// <returns>The stored size (before orientation) and what else the header says.</returns>
    /// <exception cref="ImageFormatException">The header is damaged, or needs more bytes than <paramref name="head"/> has.</exception>
    /// <example><code>public override ImageInfo ReadHeader(ReadOnlySpan&lt;byte&gt; head, CodecContext context) => new(ImageFormat.Heif, width, height) { Orientation = 6 };</code></example>
    public abstract ImageInfo ReadHeader(ReadOnlySpan<byte> head, CodecContext context);

    /// <summary>Decodes the pixels, at the stored orientation: the library turns them upright.</summary>
    /// <param name="data">The whole file.</param>
    /// <param name="header">What <see cref="ReadHeader"/> returned, already checked against the limits.</param>
    /// <param name="context">The pixel format wanted, the limits and the cancellation token.</param>
    /// <returns>
    /// An image of <paramref name="header"/>'s size, or smaller when <see cref="CodecContext.ShrinkTo"/> allows it, in
    /// <see cref="CodecContext.PixelFormat"/> unless that is <see cref="PixelFormat.None"/>.
    /// </returns>
    /// <exception cref="ImageFormatException">The file is damaged.</exception>
    /// <example><code>public override Image Decode(ReadOnlyMemory&lt;byte&gt; data, ImageInfo header, CodecContext context) => DecodeWithTheSystem(data.Span);</code></example>
    public abstract Image Decode(ReadOnlyMemory<byte> data, ImageInfo header, CodecContext context);

    /// <summary>The name.</summary>
    /// <returns><see cref="Name"/>.</returns>
    /// <example><code>Console.WriteLine(codec);</code></example>
    public override string ToString() => Name;
}
