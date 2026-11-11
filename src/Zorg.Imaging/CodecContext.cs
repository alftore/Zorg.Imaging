using System;
using System.Threading;

namespace Zorg.Imaging;

/// <summary>What an <see cref="ImageCodec"/> is asked for: the limits, the pixel format, a size it may shrink to, and cancellation.</summary>
/// <example>
/// <code>
/// if (context.ShrinkTo(header.Width, header.Height) is (int width, int height))
/// {
///     // Decode at that size or larger, if the format can do it cheaply.
/// }
/// </code>
/// </example>
public sealed class CodecContext
{
    private readonly Func<int, int, (int Width, int Height)>? _shrinkTarget;
    private readonly int _orientation;

    internal CodecContext(ImageLimits limits, PixelFormat pixelFormat, Func<int, int, (int Width, int Height)>? shrinkTarget, int orientation, CancellationToken cancellationToken)
    {
        Limits = limits;
        PixelFormat = pixelFormat;
        _shrinkTarget = shrinkTarget;
        _orientation = orientation;
        CancellationToken = cancellationToken;
    }

    /// <summary>The limits the caller set. The library has already checked the header's size against them.</summary>
    /// <example><code>if (tiles > context.Limits.MaxFrames) throw new ImageLimitException("Too many tiles.");</code></example>
    public ImageLimits Limits { get; }

    /// <summary>The pixel format to return, or <see cref="PixelFormat.None"/> for whichever suits the file.</summary>
    /// <example><code>PixelFormat format = context.PixelFormat == PixelFormat.None ? PixelFormat.Rgba32 : context.PixelFormat;</code></example>
    public PixelFormat PixelFormat { get; }

    /// <summary>Cancels the work. Check it between steps; the library also checks it before and after the codec runs.</summary>
    /// <example><code>context.CancellationToken.ThrowIfCancellationRequested();</code></example>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// The smallest stored size the codec may decode to, when the caller will shrink the image anyway (as
    /// <see cref="ImagePipeline"/> does before a <c>Fit</c>), or <see langword="null"/> to decode at full size.
    /// </summary>
    /// <param name="width">The stored width.</param>
    /// <param name="height">The stored height.</param>
    /// <returns>The size, in the stored orientation, or <see langword="null"/>.</returns>
    /// <example><code>(int Width, int Height)? smallest = context.ShrinkTo(header.Width, header.Height);</code></example>
    public (int Width, int Height)? ShrinkTo(int width, int height)
    {
        if (_shrinkTarget is null)
        {
            return null;
        }

        // The target is of the upright size; the codec works in the stored one.
        bool swaps = _orientation is >= 5 and <= 8;
        (int w, int h) = swaps ? _shrinkTarget(height, width) : _shrinkTarget(width, height);
        return swaps ? (h, w) : (w, h);
    }
}
