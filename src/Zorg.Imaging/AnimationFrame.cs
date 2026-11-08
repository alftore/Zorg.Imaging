using System;

namespace Zorg.Imaging;

/// <summary>One frame of an animation: the whole canvas as it shows, and for how long.</summary>
/// <remarks>
/// <see cref="AnimationReader"/> gives frames already drawn over what came before (disposal and blending applied), each
/// the size of the canvas, so they can be resized, cropped or saved like any image. <see cref="AnimationWriter"/> takes
/// frames the same way and works out what changed.
/// </remarks>
public sealed class AnimationFrame : IDisposable
{
    /// <summary>Makes a frame.</summary>
    /// <param name="image">The whole canvas. The frame owns it from now on and disposes it.</param>
    /// <param name="duration">How long the frame shows.</param>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> is negative.</exception>
    /// <example><code>using var frame = new AnimationFrame(image, TimeSpan.FromMilliseconds(100));</code></example>
    public AnimationFrame(Image image, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        Image = image;
        Duration = duration;
    }

    /// <summary>The whole canvas as it shows during this frame.</summary>
    /// <example><code>using Image small = frame.Image.Fit(200, 200);</code></example>
    public Image Image { get; }

    /// <summary>How long the frame shows, as stored: GIF counts in hundredths of a second, WebP in milliseconds.</summary>
    /// <remarks>Browsers show GIF frames of 0 or 10 ms for 100 ms; the value here is the file's own.</remarks>
    /// <example><code>Console.WriteLine(frame.Duration.TotalMilliseconds);</code></example>
    public TimeSpan Duration { get; }

    /// <summary>Disposes the image.</summary>
    /// <example><code>frame.Dispose();</code></example>
    public void Dispose() => Image.Dispose();
}
