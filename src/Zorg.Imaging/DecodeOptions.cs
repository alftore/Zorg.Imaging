using System;

namespace Zorg.Imaging;

/// <summary>Options for <see cref="Image.Decode(byte[], DecodeOptions?)"/> and <see cref="Image.DecodeAsync"/>.</summary>
/// <example>
/// <code>
/// var options = new DecodeOptions { PixelFormat = PixelFormat.Rgba32 };
/// using Image image = Image.Decode(bytes, options);
/// </code>
/// </example>
public sealed class DecodeOptions
{
    /// <summary>The default options: default limits, the file's own layout, orientation applied.</summary>
    /// <example><code>DecodeOptions options = DecodeOptions.Default;</code></example>
    public static DecodeOptions Default { get; } = new();

    /// <summary>The limits to apply. Defaults to <see cref="ImageLimits.Default"/>.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <example><code>var options = new DecodeOptions { Limits = new ImageLimits { MaxPixels = 25_000_000 } };</code></example>
    public ImageLimits Limits
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    } = ImageLimits.Default;

    /// <summary>
    /// The layout of the decoded image. <see cref="PixelFormat.None"/>, the default, picks the layout that holds the
    /// file without loss: <see cref="PixelFormat.L8"/> for grey, <see cref="PixelFormat.Rgb24"/> for colour, and
    /// <see cref="PixelFormat.Rgba32"/> when the file has any transparency, or <see cref="PixelFormat.L16"/>,
    /// <see cref="PixelFormat.Rgb48"/> and <see cref="PixelFormat.Rgba64"/> for 16-bit files.
    /// <see cref="PixelFormat.Rgba64"/> holds any file exactly. <see cref="PixelFormat.Rgba32"/> and
    /// <see cref="PixelFormat.Bgra32"/> hold any 8-bit file exactly, and round 16-bit samples to the nearest 8-bit value.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is <see cref="PixelFormat.Rgb24"/>, <see cref="PixelFormat.L8"/>, <see cref="PixelFormat.Rgb48"/> or
    /// <see cref="PixelFormat.L16"/>, which would drop alpha or colour, or isn't a format.
    /// </exception>
    /// <example><code>var options = new DecodeOptions { PixelFormat = PixelFormat.Bgra32 };</code></example>
    public PixelFormat PixelFormat
    {
        get;
        init
        {
            if (value is not (PixelFormat.None or PixelFormat.Rgba32 or PixelFormat.Bgra32 or PixelFormat.Rgba64))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    value,
                    "Decoding can ask for None, Rgba32, Bgra32 or Rgba64. The formats without alpha, or without colour, would drop it.");
            }

            field = value;
        }
    }

    /// <summary>
    /// Whether to turn the pixels upright using the file's EXIF orientation, so the image looks the way a browser shows
    /// it. Defaults to <see langword="true"/>. The decoded size is then <see cref="ImageInfo.DisplayWidth"/> by
    /// <see cref="ImageInfo.DisplayHeight"/>.
    /// </summary>
    /// <example><code>var options = new DecodeOptions { ApplyOrientation = false }; // pixels as stored</code></example>
    public bool ApplyOrientation { get; init; } = true;

    /// <summary>
    /// How many cores this decode may use, or 0 (the default) for <see cref="ImageDefaults.MaxDegreeOfParallelism"/>.
    /// 1 keeps the work on the calling thread. The pixels are the same whatever the value.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    /// <example><code>var options = new DecodeOptions { MaxDegreeOfParallelism = 1 };</code></example>
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
    /// The box the image will be fitted into afterwards, in the upright orientation (as stored, when
    /// <see cref="ApplyOrientation"/> is off), or <see langword="null"/> (the default) to decode at full size. A decoder that can decode smaller cheaply then does: JPEG at 1/2, 1/4 or 1/8 of
    /// its size, staying at least twice the fitted size so a resize keeps its quality, as <see cref="ImagePipeline"/>
    /// does before a <c>Fit</c>. Codecs from other packages see it as <see cref="CodecContext.ShrinkTo"/>. Other formats
    /// decode at full size: decoding never resizes, so the result is whatever size the decoder chose. Fit it with
    /// <see cref="Image.Fit"/>. <see cref="ImagePipeline"/> ignores it: its own steps say what size it needs.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A side is less than 1.</exception>
    /// <example>
    /// <code>
    /// using Image large = Image.Decode(bytes, new DecodeOptions { ShrinkTo = (400, 300) }); // a 4000x3000 JPEG decodes at 1000x750
    /// using Image thumbnail = large.Fit(400, 300);
    /// </code>
    /// </example>
    public (int Width, int Height)? ShrinkTo
    {
        get;
        init
        {
            if (value is { } box)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(box.Width, 1, nameof(value));
                ArgumentOutOfRangeException.ThrowIfLessThan(box.Height, 1, nameof(value));
                _shrinkToTarget = (width, height) => Image.Inside(width, height, box.Width, box.Height);
            }
            else
            {
                _shrinkToTarget = null;
            }

            field = value;
        }
    }

    /// <summary>
    /// Codecs from other packages for formats the library doesn't decode itself, such as HEIC. <see langword="null"/>
    /// (the default) uses <see cref="ImageDefaults.Codecs"/>.
    /// </summary>
    /// <example><code>var options = new DecodeOptions { Codecs = ImageCodecs.Empty.Add(heicCodec) };</code></example>
    public ImageCodecs? Codecs { get; init; }

    /// <summary>
    /// For shrink-on-load: given the upright size, the size the first step will make. A decoder that can decode
    /// smaller (JPEG) then does, staying at least twice that size. <see cref="ImagePipeline"/> sets it from its steps;
    /// otherwise it comes from <see cref="ShrinkTo"/>.
    /// </summary>
    internal Func<int, int, (int Width, int Height)>? ShrinkTarget
    {
        get => field ?? _shrinkToTarget;
        init;
    }

    private readonly Func<int, int, (int Width, int Height)>? _shrinkToTarget;

    /// <summary>
    /// For <see cref="Image.DecodeCmyk"/>: a CMYK file's pixels come out as its inks, C, M, Y and K in the four bytes of
    /// an <see cref="PixelFormat.Rgba32"/> image, 0 no ink to 255 full, with <see cref="Image.InkProfile"/> set.
    /// </summary>
    internal bool KeepInks { get; init; }

    internal DecodeOptions WithInks() => new()
    {
        Limits = Limits,
        ApplyOrientation = ApplyOrientation,
        MaxDegreeOfParallelism = MaxDegreeOfParallelism,
        Codecs = Codecs,
        ShrinkTo = ShrinkTo,
        ShrinkTarget = ShrinkTarget,
        KeepInks = true,
    };

    internal DecodeOptions WithShrinkTarget(Func<int, int, (int Width, int Height)>? target) => new()
    {
        Limits = Limits,
        PixelFormat = PixelFormat,
        ApplyOrientation = ApplyOrientation,
        MaxDegreeOfParallelism = MaxDegreeOfParallelism,
        Codecs = Codecs,
        ShrinkTarget = target, // not ShrinkTo: the target given replaces it, even when null
        KeepInks = KeepInks,
    };
}
