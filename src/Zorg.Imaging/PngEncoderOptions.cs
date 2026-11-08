using System;

namespace Zorg.Imaging;

/// <summary>Options for <see cref="Image.SaveAsPng"/> and <see cref="Image.SaveAsPngAsync"/>.</summary>
/// <example>
/// <code>
/// var options = new PngEncoderOptions { CompressionLevel = 9 };
/// image.SaveAsPng(output, options);
/// </code>
/// </example>
public sealed class PngEncoderOptions
{
    /// <summary>The default for <see cref="CompressionLevel"/>: 6, zlib's own default.</summary>
    /// <example><code>int level = PngEncoderOptions.DefaultCompressionLevel;</code></example>
    public const int DefaultCompressionLevel = 6;

    /// <summary>The default options.</summary>
    /// <example><code>PngEncoderOptions options = PngEncoderOptions.Default;</code></example>
    public static PngEncoderOptions Default { get; } = new();

    /// <summary>
    /// The zlib compression level, 0 to 9. 0 stores the data uncompressed and unfiltered, the fastest and largest; 9 is
    /// the smallest and slowest. The output is lossless at every level.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value isn't 0 to 9.</exception>
    /// <example><code>var fast = new PngEncoderOptions { CompressionLevel = 1 };</code></example>
    public int CompressionLevel
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 9);
            field = value;
        }
    } = DefaultCompressionLevel;

    /// <summary>
    /// Whether to write the image in the smallest PNG layout that holds it exactly (the default): RGBA whose alpha is
    /// all opaque as RGB, colour where every pixel is grey as grey, up to 256 colours as a palette, 16-bit samples that
    /// are all exact 8-bit values as 8-bit, and grey or palette indices in 1, 2 or 4 bits when they fit. With
    /// <see langword="false"/>, the file has the image's own layout: grey, RGB or RGBA at 8 or 16 bits.
    /// </summary>
    /// <remarks>
    /// Every pixel decodes to the same value either way. A file read back may have a different
    /// <see cref="Image.PixelFormat"/> from the image saved, for example <see cref="PixelFormat.Rgb24"/> for an opaque
    /// <see cref="PixelFormat.Rgba32"/> image. When the image has a colour space other than sRGB, grey stays grey and
    /// colour stays colour, since a profile describes one or the other.
    /// </remarks>
    /// <example><code>var options = new PngEncoderOptions { ReduceColorType = false };</code></example>
    public bool ReduceColorType { get; init; } = true;

    /// <summary>
    /// Whether fully transparent pixels may be written as transparent black. Their colour can't be seen, but it costs
    /// bytes, and when the colours differ they stop transparency being written as a single <c>tRNS</c> colour or a
    /// palette entry. Off by default, since some programs read the hidden colour back.
    /// </summary>
    /// <example><code>var options = new PngEncoderOptions { CleanTransparentPixels = true };</code></example>
    public bool CleanTransparentPixels { get; init; }

    /// <summary>How each row's filter is chosen. Defaults to <see cref="PngFilter.Adaptive"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value isn't a <see cref="PngFilter"/>.</exception>
    /// <example><code>var options = new PngEncoderOptions { Filter = PngFilter.Paeth };</code></example>
    public PngFilter Filter
    {
        get;
        init
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Not a PngFilter value.");
            }

            field = value;
        }
    }

    /// <summary>
    /// Whether to search for the smallest file, as oxipng does: when the image fits a palette, the palette and colour
    /// layouts are tried against each other; each band of rows is compressed with several filter strategies and zlib
    /// strategies at level 9, and the best filter's rows again with a slow encoder that weighs every match by its cost
    /// in bits (Zopfli's approach), the smallest kept. <see cref="Filter"/> and <see cref="CompressionLevel"/> are then
    /// not used. Files come out about 3% smaller than level 9 on photos and within a few per cent of oxipng; encoding
    /// takes 30 to 40 times as long (about 5 s for 12 megapixels on 16 cores). Meant for files written once and served
    /// many times.
    /// </summary>
    /// <example><code>var options = new PngEncoderOptions { Optimize = true, CleanTransparentPixels = true };</code></example>
    public bool Optimize { get; init; }

    /// <summary>
    /// How many cores this encode may use, or 0 (the default) for <see cref="ImageDefaults.MaxDegreeOfParallelism"/>.
    /// The file is the same, byte for byte, whatever the value.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    /// <example><code>var options = new PngEncoderOptions { MaxDegreeOfParallelism = 1 };</code></example>
    public int MaxDegreeOfParallelism
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    }

    /// <summary>
    /// What to do when the image's <see cref="Image.ColorSpace"/> isn't sRGB. Defaults to
    /// <see cref="ColorSpaceHandling.Keep"/>: PNG keeps any colour space exactly, for a few bytes.
    /// </summary>
    /// <example><code>var options = new PngEncoderOptions { ColorSpaceHandling = ColorSpaceHandling.Srgb };</code></example>
    public ColorSpaceHandling ColorSpaceHandling
    {
        get;
        init
        {
            if (!System.Enum.IsDefined(value))
            {
                throw new System.ArgumentOutOfRangeException(nameof(value), value, "Not a ColorSpaceHandling value.");
            }

            field = value;
        }
    } = ColorSpaceHandling.Keep;

    /// <summary>
    /// Which of the source file's EXIF and XMP to write. Defaults to <see cref="MetadataOptions.Strip"/>: none, the safe
    /// choice for publishing.
    /// </summary>
    /// <exception cref="System.ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <example><code>var options = new PngEncoderOptions { Metadata = MetadataOptions.Keep.Without(ExifGroup.Location) };</code></example>
    public MetadataOptions Metadata
    {
        get;
        init
        {
            System.ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    } = MetadataOptions.Strip;
}
