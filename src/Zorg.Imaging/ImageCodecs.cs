using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging;

/// <summary>
/// An unchangeable list of <see cref="ImageCodec"/>s added by other packages. <see cref="Add"/> returns a new list, so a
/// list can be shared between threads. For a format, the first codec that is available wins.
/// </summary>
/// <example>
/// <code>
/// // Once, at start-up:
/// ImageDefaults.Codecs = ImageCodecs.Empty.Add(SystemCodecs.Heif, SystemCodecs.Avif);
///
/// // Or for one call:
/// using Image photo = Image.Decode(bytes, new DecodeOptions { Codecs = ImageCodecs.Empty.Add(SystemCodecs.Heif) });
/// </code>
/// </example>
public sealed class ImageCodecs : IReadOnlyList<ImageCodec>
{
    private readonly ImageCodec[] _codecs;

    private ImageCodecs(ImageCodec[] codecs) => _codecs = codecs;

    /// <summary>No added codecs: only the formats the library decodes itself.</summary>
    /// <example><code>ImageCodecs codecs = ImageCodecs.Empty;</code></example>
    public static ImageCodecs Empty { get; } = new([]);

    /// <summary>How many codecs there are.</summary>
    /// <example><code>int count = codecs.Count;</code></example>
    public int Count => _codecs.Length;

    /// <summary>The codec at a position.</summary>
    /// <param name="index">The position, from 0.</param>
    /// <returns>The codec.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside the list.</exception>
    /// <example><code>ImageCodec first = codecs[0];</code></example>
    public ImageCodec this[int index] => _codecs[index];

    /// <summary>A new list with these codecs after the existing ones.</summary>
    /// <param name="codecs">The codecs to add.</param>
    /// <returns>The new list.</returns>
    /// <exception cref="ArgumentNullException">A codec is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A codec is for a format the library decodes itself, or one it doesn't recognise.</exception>
    /// <example><code>ImageCodecs codecs = ImageCodecs.Empty.Add(heic, avif);</code></example>
    public ImageCodecs Add(params ReadOnlySpan<ImageCodec> codecs)
    {
        if (codecs.IsEmpty)
        {
            return this;
        }

        var all = new ImageCodec[_codecs.Length + codecs.Length];
        _codecs.CopyTo(all, 0);
        for (int i = 0; i < codecs.Length; i++)
        {
            ImageCodec codec = codecs[i] ?? throw new ArgumentNullException(nameof(codecs), "A codec is null.");
            ImageFormat format = codec.Format;
            if (CodecTable.For(format) is not null)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"{codec.Name} is for {format.Name}, which the library decodes itself."),
                    nameof(codecs));
            }

            if (!Contains(ImageFormats.Known, format))
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"{codec.Name} is for a format the library doesn't recognise."),
                    nameof(codecs));
            }

            all[_codecs.Length + i] = codec;
        }

        return new ImageCodecs(all);
    }

    /// <summary>Whether a codec in the list decodes this format on this machine.</summary>
    /// <param name="format">The format.</param>
    /// <returns><see langword="true"/> when an available codec is for it.</returns>
    /// <example><code>bool heic = ImageDefaults.Codecs.Supports(ImageFormat.Heif);</code></example>
    public bool Supports(ImageFormat format) => Find(format, out _) is not null;

    /// <summary>Goes through the codecs in order.</summary>
    /// <returns>The enumerator.</returns>
    /// <example><code>foreach (ImageCodec codec in codecs) Console.WriteLine(codec);</code></example>
    public IEnumerator<ImageCodec> GetEnumerator() => ((IEnumerable<ImageCodec>)_codecs).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// The first available codec for a format, or <see langword="null"/>. When codecs for the format exist but none is
    /// available, <paramref name="unavailable"/> is the first one's reason.
    /// </summary>
    internal ImageCodec? Find(ImageFormat? format, out string? unavailable)
    {
        unavailable = null;
        foreach (ImageCodec codec in _codecs)
        {
            if (codec.Format != format)
            {
                continue;
            }

            CodecAvailability availability = codec.Availability;
            if (availability.IsAvailable)
            {
                return codec;
            }

            unavailable ??= availability.Reason;
        }

        return null;
    }

    private static bool Contains(IReadOnlyList<ImageFormat> formats, ImageFormat format)
    {
        foreach (ImageFormat known in formats)
        {
            if (known == format)
            {
                return true;
            }
        }

        return false;
    }
}
