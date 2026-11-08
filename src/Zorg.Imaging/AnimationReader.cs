using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Codecs.Gif;
using Zorg.Imaging.Codecs.Webp;
using Zorg.Imaging.Formats;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging;

/// <summary>Reads an animated GIF or WebP frame by frame; any other image reads as one frame.</summary>
/// <remarks>
/// <para>
/// Each frame is the whole canvas as a browser shows it at that point: drawn over what the frames before left, after
/// their disposal. Only one canvas is held, so memory doesn't grow with the number of frames; the frame count is
/// checked against <see cref="ImageLimits.MaxFrames"/> and the canvas against <see cref="ImageLimits.MaxPixels"/>.
/// </para>
/// <para>
/// Frames come out as <see cref="PixelFormat.Rgba32"/> unless <see cref="DecodeOptions.PixelFormat"/> asks for another.
/// A still image, an APNG (its first frame, for now) or a GIF or WebP that isn't animated gives one frame of duration
/// zero, decoded as <see cref="Image.Decode"/> would.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await using FileStream input = File.OpenRead("cat.gif");
/// AnimationReader reader = await AnimationReader.OpenAsync(input);
/// while (await reader.ReadFrameAsync() is { } frame)
/// {
///     using (frame)
///     {
///         Console.WriteLine($"{frame.Duration.TotalMilliseconds} ms");
///     }
/// }
/// </code>
/// </example>
public sealed class AnimationReader : IDisposable
{
    private readonly DecodeOptions _options;
    private readonly bool _sync;
    private GifReader? _gif;
    private WebpDecoder.Animation? _webp;
    private AnimationFrame? _first;
    private bool _ended;

    private AnimationReader(DecodeOptions options, bool sync, ImageFormat format)
    {
        _options = options;
        _sync = sync;
        Format = format;
    }

    /// <summary>The file's format.</summary>
    /// <example><code>Console.WriteLine(reader.Format.Name); // GIF</code></example>
    public ImageFormat Format { get; }

    /// <summary>The canvas width: every frame's width.</summary>
    /// <example><code>Console.WriteLine(reader.Width);</code></example>
    public int Width { get; private set; }

    /// <summary>The canvas height: every frame's height.</summary>
    /// <example><code>Console.WriteLine(reader.Height);</code></example>
    public int Height { get; private set; }

    /// <summary>How many times the animation plays: 0 for ever, 1 for a still image or a GIF without a loop extension.</summary>
    /// <remarks>
    /// A GIF's NETSCAPE2.0 extension counts repeats after the first play, as browsers read it, so a GIF that says 2
    /// plays 3 times. WebP counts plays.
    /// </remarks>
    /// <example><code>bool forever = reader.LoopCount == 0;</code></example>
    public int LoopCount { get; private set; } = 1;

    /// <summary>Whether the file is an animation rather than a still image read as one frame.</summary>
    /// <example><code>if (!reader.IsAnimated) { /* save as a still */ }</code></example>
    public bool IsAnimated { get; private set; }

    /// <summary>Opens an image file held in memory.</summary>
    /// <param name="data">The whole file.</param>
    /// <param name="options">The limits and options, or <see langword="null"/> for <see cref="DecodeOptions.Default"/>.</param>
    /// <returns>A reader at the first frame, whose frames <see cref="ReadFrame"/> gives.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    /// <exception cref="UnsupportedImageFormatException">The data isn't an image format this version can decode.</exception>
    /// <exception cref="ImageFormatException">The file is damaged or ends early.</exception>
    /// <exception cref="ImageLimitException">The canvas is larger than <see cref="ImageLimits"/> allows.</exception>
    /// <example>
    /// <code>
    /// AnimationReader reader = AnimationReader.Open(File.ReadAllBytes("cat.webp"));
    /// while (reader.ReadFrame() is { } frame)
    /// {
    ///     frame.Dispose();
    /// }
    /// </code>
    /// </example>
    public static AnimationReader Open(byte[] data, DecodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ValueTask<AnimationReader> task = OpenCoreAsync(DataReader.FromMemory(data), options ?? DecodeOptions.Default, sync: true, CancellationToken.None);
        Debug.Assert(task.IsCompleted, "Opening over memory must complete synchronously.");
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Opens an image file from a stream.</summary>
    /// <param name="stream">A readable stream at the start of the file. Frames are read from it as they are asked for.</param>
    /// <param name="options">The limits and options, or <see langword="null"/> for <see cref="DecodeOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A reader at the first frame, whose frames <see cref="ReadFrameAsync"/> gives.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="stream"/> can't read.</exception>
    /// <exception cref="UnsupportedImageFormatException">The data isn't an image format this version can decode.</exception>
    /// <exception cref="ImageFormatException">The file is damaged or ends early.</exception>
    /// <exception cref="ImageLimitException">The canvas is larger than <see cref="ImageLimits"/> allows.</exception>
    /// <exception cref="IOException">The stream itself fails.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <example>
    /// <code>
    /// await using FileStream input = File.OpenRead("cat.gif");
    /// AnimationReader reader = await AnimationReader.OpenAsync(input, cancellationToken: cancellationToken);
    /// </code>
    /// </example>
    public static async ValueTask<AnimationReader> OpenAsync(Stream stream, DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new NotSupportedException("OpenAsync needs a readable stream.");
        }

        return await OpenCoreAsync(DataReader.FromStream(stream), options ?? DecodeOptions.Default, sync: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the next frame of a reader made by <see cref="Open"/>.</summary>
    /// <returns>The frame, which the caller disposes, or <see langword="null"/> after the last.</returns>
    /// <exception cref="InvalidOperationException">The reader was made by <see cref="OpenAsync"/>; use <see cref="ReadFrameAsync"/>.</exception>
    /// <exception cref="ImageFormatException">The file is damaged.</exception>
    /// <exception cref="ImageLimitException">The file has more frames than <see cref="ImageLimits.MaxFrames"/>.</exception>
    /// <example><code>using AnimationFrame? frame = reader.ReadFrame();</code></example>
    public AnimationFrame? ReadFrame()
    {
        if (!_sync)
        {
            throw new InvalidOperationException("This reader reads from a stream; use ReadFrameAsync.");
        }

        ValueTask<AnimationFrame?> task = ReadFrameAsync(CancellationToken.None);
        Debug.Assert(task.IsCompleted, "Reading over memory must complete synchronously.");
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Reads the next frame.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The frame, which the caller disposes, or <see langword="null"/> after the last.</returns>
    /// <exception cref="ImageFormatException">The file is damaged.</exception>
    /// <exception cref="ImageLimitException">The file has more frames than <see cref="ImageLimits.MaxFrames"/>.</exception>
    /// <exception cref="IOException">The stream itself fails.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <example><code>using AnimationFrame? frame = await reader.ReadFrameAsync(cancellationToken);</code></example>
    public async ValueTask<AnimationFrame?> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        if (_first is { } first)
        {
            _first = null;
            return first;
        }

        if (_ended)
        {
            return null;
        }

        using Parallelism.Scope scope = Parallelism.Use(_options.MaxDegreeOfParallelism);
        AnimationFrame? frame = await NextAsync(cancellationToken).ConfigureAwait(false);
        _ended = frame is null;
        return frame;
    }

    /// <summary>Disposes a frame read ahead and not yet taken. The stream isn't closed.</summary>
    /// <example><code>reader.Dispose();</code></example>
    public void Dispose()
    {
        _first?.Dispose();
        _first = null;
        _ended = true;
    }

    private static async ValueTask<AnimationReader> OpenCoreAsync(DataReader reader, DecodeOptions options, bool sync, CancellationToken cancellationToken)
    {
        using Parallelism.Scope scope = Parallelism.Use(options.MaxDegreeOfParallelism);
        ReadOnlyMemory<byte> head = await reader.PeekAsync(Math.Max(32, ImageFormats.MaxSignatureLength), cancellationToken).ConfigureAwait(false);
        ImageFormat? format = ImageFormats.Detect(head.Span);
        if (format == ImageFormat.Gif)
        {
            GifReader gif = await GifReader.OpenAsync(reader, options.Limits, cancellationToken).ConfigureAwait(false);
            AnimationFrame first = await ReadGifAsync(gif, options, cancellationToken).ConfigureAwait(false)
                ?? throw ImageErrors.Corrupt("The GIF file has no image.");

            // A GIF of one frame is still an animation file, but plays as a still.
            return new AnimationReader(options, sync, format)
            {
                _gif = gif,
                _first = first,
                Width = gif.Width,
                Height = gif.Height,
                LoopCount = gif.Repeats is int repeats ? (repeats == 0 ? 0 : repeats + 1) : 1,
                IsAnimated = true,
            };
        }

        if (format == ImageFormat.Webp && IsAnimatedWebp(head.Span))
        {
            WebpDecoder.Animation webp = await WebpDecoder.Animation.OpenAsync(reader, options, cancellationToken).ConfigureAwait(false);
            return new AnimationReader(options, sync, format)
            {
                _webp = webp,
                Width = webp.Width,
                Height = webp.Height,
                LoopCount = webp.Loops,
                IsAnimated = true,
            };
        }

        Image still = await Image.DecodeCoreAsync(reader, options, sync, cancellationToken).ConfigureAwait(false);
        return new AnimationReader(options, sync, format!)
        {
            _first = new AnimationFrame(still, TimeSpan.Zero),
            _ended = true,
            Width = still.Width,
            Height = still.Height,
        };
    }

    // RIFF, size, WEBP, then a VP8X chunk whose flags have the animation bit.
    private static bool IsAnimatedWebp(ReadOnlySpan<byte> head) =>
        head.Length >= 21 && head.Slice(12, 4).SequenceEqual("VP8X"u8) && (head[20] & 0x02) != 0;

    private async ValueTask<AnimationFrame?> NextAsync(CancellationToken cancellationToken) =>
        _gif is not null ? await ReadGifAsync(_gif, _options, cancellationToken).ConfigureAwait(false)
        : _webp is not null ? await ReadWebpAsync(_webp, _options, cancellationToken).ConfigureAwait(false)
        : null;

    private static async ValueTask<AnimationFrame?> ReadGifAsync(GifReader gif, DecodeOptions options, CancellationToken cancellationToken) =>
        await gif.NextFrameAsync(cancellationToken).ConfigureAwait(false)
            ? ToFrame(gif.Canvas, gif.Width, gif.Height, gif.ColorSpace, TimeSpan.FromMilliseconds(gif.Delay * 10), options)
            : null;

    private static async ValueTask<AnimationFrame?> ReadWebpAsync(WebpDecoder.Animation webp, DecodeOptions options, CancellationToken cancellationToken) =>
        await webp.NextFrameAsync(cancellationToken).ConfigureAwait(false)
            ? ToFrame(webp.Canvas, webp.Width, webp.Height, webp.ColorSpace, TimeSpan.FromMilliseconds(webp.Duration), options)
            : null;

    private static AnimationFrame ToFrame(uint[] canvas, int width, int height, ColorSpace colorSpace, TimeSpan duration, DecodeOptions options)
    {
        PixelFormat format = options.PixelFormat != PixelFormat.None ? options.PixelFormat : PixelFormat.Rgba32;
        Image image = Argb.ToImage(canvas, width, height, hasAlpha: true, format);
        image.ColorSpace = colorSpace;
        return new AnimationFrame(image, duration);
    }
}
