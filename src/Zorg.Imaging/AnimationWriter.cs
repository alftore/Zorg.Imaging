using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Codecs.Gif;
using Zorg.Imaging.Codecs.Webp;
using Zorg.Imaging.Metadata;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging;

/// <summary>Writes an animated GIF or WebP frame by frame.</summary>
/// <remarks>
/// <para>
/// Frames are given whole, all the size of the first, as <see cref="AnimationReader"/> gives them; the writer codes
/// only the rectangle that changed from the frame before, and joins a frame identical to the one before into it by
/// adding the durations. One frame is held back until the next arrives, so <see cref="Complete"/> (or
/// <see cref="CompleteAsync"/>) must be called to write the last one and finish the file.
/// </para>
/// <para>
/// GIF: each frame gets its own colour table of up to <see cref="GifEncoderOptions.MaxColors"/> colours; pixels that
/// didn't change are left transparent so the frame before shows through, and a frame is cleared after it shows when
/// the next has transparency where it had none. Durations are rounded to hundredths of a second. GIF frames are written
/// to the stream as they are made.
/// </para>
/// <para>
/// WebP: each frame is lossy or lossless as <see cref="WebpEncoderOptions.Lossless"/> says, with its rectangle starting
/// on even coordinates as the format needs, replacing what was there (no blending). A frame is cleared after it shows
/// where the next makes pixels transparent or translucent, so a decoder that blends every frame anyway, as ImageMagick
/// does, shows the same. Durations are whole milliseconds. The file is written when completed, as
/// its header holds the total size; until then the coded frames are kept in memory.
/// </para>
/// <para>
/// Colour: unless the options keep colour spaces (<see cref="ColorSpaceHandling.Keep"/>), frames in another colour
/// space are converted to sRGB, since one profile covers the whole file; with Keep, the first frame's space is written.
/// Metadata isn't written.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// AnimationReader reader = await AnimationReader.OpenAsync(input);
/// AnimationWriter writer = AnimationWriter.CreateWebp(output, reader.LoopCount);
/// while (await reader.ReadFrameAsync() is { } frame)
/// {
///     using (frame)
///     using (Image small = frame.Image.Fit(320, 320))
///     {
///         await writer.WriteFrameAsync(small, frame.Duration);
///     }
/// }
///
/// await writer.CompleteAsync();
/// </code>
/// </example>
public sealed class AnimationWriter
{
    private readonly Stream _stream;
    private readonly GifEncoderOptions? _gif;
    private readonly WebpEncoderOptions? _webp;
    private readonly int _loopCount;
    private readonly ArrayBufferWriter<byte> _output = new();
    private uint[] _canvas = [];
    private uint[]? _pending;
    private TimeSpan _pendingDuration;
    private ReadOnlyMemory<byte> _profile;
    private bool _alpha;
    private bool _completed;
    private bool _started;

    private AnimationWriter(Stream stream, int loopCount, GifEncoderOptions? gif, WebpEncoderOptions? webp)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new NotSupportedException("An animation needs a writable stream.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(loopCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(loopCount, gif is null ? ushort.MaxValue : ushort.MaxValue + 1);
        _stream = stream;
        _loopCount = loopCount;
        _gif = gif;
        _webp = webp;
    }

    /// <summary>The canvas width, set by the first frame; 0 before it.</summary>
    /// <example><code>Console.WriteLine(writer.Width);</code></example>
    public int Width { get; private set; }

    /// <summary>The canvas height, set by the first frame; 0 before it.</summary>
    /// <example><code>Console.WriteLine(writer.Height);</code></example>
    public int Height { get; private set; }

    /// <summary>Starts an animated GIF.</summary>
    /// <param name="stream">A writable stream. Frames are written to it as they are coded.</param>
    /// <param name="loopCount">How many times the animation plays: 0 (the default) for ever, up to 65,536.</param>
    /// <param name="options">The colours and dithering, or <see langword="null"/> for <see cref="GifEncoderOptions.Default"/>.</param>
    /// <returns>The writer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="stream"/> can't write.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="loopCount"/> is out of range.</exception>
    /// <example><code>AnimationWriter writer = AnimationWriter.CreateGif(output, loopCount: 0);</code></example>
    public static AnimationWriter CreateGif(Stream stream, int loopCount = 0, GifEncoderOptions? options = null) =>
        new(stream, loopCount, options ?? GifEncoderOptions.Default, null);

    /// <summary>Starts an animated WebP.</summary>
    /// <param name="stream">A writable stream. The file is written to it when the animation is completed.</param>
    /// <param name="loopCount">How many times the animation plays: 0 (the default) for ever, up to 65,535.</param>
    /// <param name="options">Lossy or lossless, quality and effort, or <see langword="null"/> for <see cref="WebpEncoderOptions.Default"/>.</param>
    /// <returns>The writer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="stream"/> can't write.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="loopCount"/> is out of range.</exception>
    /// <example><code>AnimationWriter writer = AnimationWriter.CreateWebp(output, options: new WebpEncoderOptions { Quality = 75 });</code></example>
    public static AnimationWriter CreateWebp(Stream stream, int loopCount = 0, WebpEncoderOptions? options = null) =>
        new(stream, loopCount, null, options ?? WebpEncoderOptions.Default);

    /// <summary>Adds a frame.</summary>
    /// <param name="image">The whole canvas for this frame; the first frame sets the canvas size. It isn't kept.</param>
    /// <param name="duration">How long the frame shows.</param>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="image"/> isn't the size of the first frame.</exception>
    /// <exception cref="NotSupportedException">The first frame is larger than the format can hold.</exception>
    /// <exception cref="InvalidOperationException">The animation is already completed.</exception>
    /// <example><code>writer.WriteFrame(image, TimeSpan.FromMilliseconds(80));</code></example>
    public void WriteFrame(Image image, TimeSpan duration)
    {
        Add(image, duration);
        Flush();
    }

    /// <summary>Adds a frame.</summary>
    /// <param name="image">The whole canvas for this frame; the first frame sets the canvas size. It isn't kept.</param>
    /// <param name="duration">How long the frame shows.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the frame is coded and any finished bytes are written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="image"/> isn't the size of the first frame.</exception>
    /// <exception cref="NotSupportedException">The first frame is larger than the format can hold.</exception>
    /// <exception cref="InvalidOperationException">The animation is already completed.</exception>
    /// <example><code>await writer.WriteFrameAsync(image, TimeSpan.FromMilliseconds(80), cancellationToken);</code></example>
    public async ValueTask WriteFrameAsync(Image image, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        Add(image, duration);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the last frame and the end of the file.</summary>
    /// <exception cref="InvalidOperationException">No frame was written, or the animation is already completed.</exception>
    /// <example><code>writer.Complete();</code></example>
    public void Complete()
    {
        Finish();
        Flush();
    }

    /// <summary>Writes the last frame and the end of the file.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <exception cref="InvalidOperationException">No frame was written, or the animation is already completed.</exception>
    /// <example><code>await writer.CompleteAsync(cancellationToken);</code></example>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        Finish();
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Add(Image image, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        if (_completed)
        {
            throw new InvalidOperationException("The animation is already completed.");
        }

        if (Width == 0)
        {
            int most = _gif is not null ? GifEncoder.MaxSide : _webp!.Lossless ? Vp8lEncoder.MaxSide : WebpEncoder.MaxLossySide;
            if (image.Width > most || image.Height > most)
            {
                throw new NotSupportedException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"An animated {(_gif is null ? "WebP" : "GIF")} can't hold {image.Width} x {image.Height} frames: each side must be at most {most} pixels."));
            }
        }
        else if (image.Width != Width || image.Height != Height)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Every frame must be {Width} x {Height}, the size of the first; this one is {image.Width} x {image.Height}."),
                nameof(image));
        }

        ColorSpaceHandling handling = _gif?.ColorSpaceHandling ?? _webp!.ColorSpaceHandling;
        int degree = _gif?.MaxDegreeOfParallelism ?? _webp!.MaxDegreeOfParallelism;
        using Parallelism.Scope scope = Parallelism.Use(degree);
        Image? converted = handling != ColorSpaceHandling.Keep && SrgbConverter.CanConvert(image.ColorSpace)
            ? SrgbConverter.Convert(image, onlyIfInside: false)
            : null;
        uint[] argb;
        try
        {
            Image source = converted ?? image;
            (argb, _) = Argb.FromImage(source);
            if (Width == 0)
            {
                Width = image.Width;
                Height = image.Height;
                _canvas = new uint[argb.Length];
                _profile = IccProfileWriter.For(source.ColorSpace, _gif is null ? "WebP" : "GIF");
            }
        }
        finally
        {
            converted?.Dispose();
        }

        if (_gif is not null)
        {
            OneBitAlpha.Apply(argb, image.Width, image.Height, _gif);
        }

        if (_pending is not null && Same(_pending, argb))
        {
            _pendingDuration += duration;
            return;
        }

        if (_pending is not null)
        {
            Emit(argb);
        }

        _pending = argb;
        _pendingDuration = duration;
    }

    private void Finish()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The animation is already completed.");
        }

        if (_pending is null)
        {
            throw new InvalidOperationException("An animation needs at least one frame.");
        }

        int degree = _gif?.MaxDegreeOfParallelism ?? _webp!.MaxDegreeOfParallelism;
        using (Parallelism.Use(degree))
        {
            Emit(next: null);
        }

        _completed = true;
        if (_gif is not null)
        {
            _output.Write<byte>([0x3B]);
            return;
        }

        byte[] frames = _output.WrittenSpan.ToArray();
        _output.ResetWrittenCount();
        WebpEncoder.AnimationContainer(_output, Width, Height, _alpha, _profile.Span, _loopCount, frames);
    }

    // Writes out what is coded so far: everything for a GIF, and for a WebP only once the file is complete.
    private void Flush()
    {
        if (_gif is not null || _completed)
        {
            _stream.Write(_output.WrittenSpan);
            _output.ResetWrittenCount();
        }
    }

    private async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        if (_gif is not null || _completed)
        {
            await _stream.WriteAsync(_output.WrittenMemory, cancellationToken).ConfigureAwait(false);
            _output.ResetWrittenCount();
        }
    }

    // Codes the pending frame, now that the one after it (or the end) is known, and moves the canvas on past it.
    private void Emit(uint[]? next)
    {
        uint[] frame = _pending!;
        if (_gif is not null)
        {
            EmitGif(frame, next);
        }
        else
        {
            EmitWebp(frame, next);
        }
    }

    private void EmitGif(uint[] frame, uint[]? next)
    {
        if (!_started)
        {
            GifEncoder.WriteAnimationHeader(_output, Width, Height, _profile.Span, _loopCount == 1 ? -1 : _loopCount == 0 ? 0 : _loopCount - 1);
            _started = true;
        }

        uint[] before = _canvas;
        (int x0, int y0, int x1, int y1) changed = Bounds(before, frame, (b, f) => !SameGif(b, f));

        // Where the next frame is transparent but this one leaves colour, this frame is cleared after it shows.
        (int, int, int, int) clear = next is null
            ? Empty
            : Bounds(frame, next, before, static (f, n, b) => !Opaque(n) && (Opaque(f) || Opaque(b)));
        (int left, int top, int right, int bottom) = Union(changed, clear);
        if (right <= left)
        {
            (left, top, right, bottom) = (0, 0, 1, 1);
        }

        int disposal = clear.Item3 > clear.Item1 ? 2 : 1;
        int width = right - left;
        int height = bottom - top;
        uint[] region = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((top + y) * Width) + left + x;
                uint f = frame[i];

                // Unchanged colour is left transparent, so the frame before shows through and the frame codes smaller.
                region[(y * width) + x] = !Opaque(f) || SameGif(before[i], f) ? 0u : f;
            }
        }

        Palettizer.Result result = Palettizer.Run(region, width, height, _gif!.MaxColors, _gif.Dithering);
        int delay = (int)Math.Clamp(Math.Round(_pendingDuration.TotalMilliseconds / 10, MidpointRounding.AwayFromZero), 0, ushort.MaxValue);
        GifEncoder.WriteAnimationFrame(_output, result, left, top, width, height, delay, disposal);

        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                int i = (y * Width) + x;
                uint shown = Opaque(frame[i]) ? frame[i] : before[i];
                before[i] = disposal == 2 ? 0u : shown;
            }
        }
    }

    private void EmitWebp(uint[] frame, uint[]? next)
    {
        uint[] before = _canvas;
        (int, int, int, int) changed = Bounds(before, frame, (b, f) => !SameWebp(b, f));

        // Where the next frame changes a pixel to one that isn't opaque, this frame is cleared after it shows. A decoder
        // then draws the next frame on transparency there, which gives the same pixels whether or not it honours the
        // no-blend flag (ImageMagick doesn't).
        (int, int, int, int) clear = next is null
            ? Empty
            : Bounds(frame, next, static (f, n) => !SameWebp(f, n) && n < 0xFF000000u && f >= 0x01000000u);
        (int left, int top, int right, int bottom) = Union(changed, clear);
        if (right <= left)
        {
            (left, top, right, bottom) = (0, 0, 1, 1);
        }

        bool dispose = clear.Item3 > clear.Item1;

        // Frames start on even coordinates (the offsets are stored halved).
        left &= ~1;
        top &= ~1;
        int width = right - left;
        int height = bottom - top;
        uint[] region = new uint[width * height];
        bool alpha = false;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<uint> row = frame.AsSpan(((top + y) * Width) + left, width);
            row.CopyTo(region.AsSpan(y * width));
            Span<uint> shown = before.AsSpan(((top + y) * Width) + left, width);
            if (dispose)
            {
                shown.Clear();
            }
            else
            {
                row.CopyTo(shown);
            }
            alpha |= row.ContainsAnyInRange(0u, 0xFEFFFFFFu);
        }

        _alpha |= alpha;
        (byte[] data, byte[] alphaChunk) = WebpEncoder.EncodePicture(region, width, height, alpha, _webp!);
        int duration = (int)Math.Clamp(Math.Round(_pendingDuration.TotalMilliseconds, MidpointRounding.AwayFromZero), 0, 0xFFFFFF);
        byte[] payload = WebpEncoder.AnimationFrame(left, top, width, height, duration, dispose, _webp!.Lossless, data, alphaChunk);
        Span<byte> header = stackalloc byte[8];
        "ANMF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], payload.Length);
        _output.Write(header);
        _output.Write(payload);
        if ((payload.Length & 1) != 0)
        {
            _output.Write<byte>([0]);
        }
    }

    private static readonly (int, int, int, int) Empty = (0, 0, 0, 0);

    private static bool Opaque(uint pixel) => pixel >= 0x80000000u;

    // Whether two pixels show the same in a GIF: both transparent, or the same colour.
    private static bool SameGif(uint a, uint b) => Opaque(a) ? Opaque(b) && (a | 0xFF000000u) == (b | 0xFF000000u) : !Opaque(b);

    // Whether two pixels are the same in a WebP: equal, or both fully transparent.
    private static bool SameWebp(uint a, uint b) => a == b || (a < 0x01000000u && b < 0x01000000u);

    private bool Same(uint[] a, uint[] b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (_gif is not null ? !SameGif(a[i], b[i]) : !SameWebp(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    // The smallest rectangle holding every pixel where differs is true, as left, top, right and bottom (exclusive).
    private (int, int, int, int) Bounds(uint[] a, uint[] b, Func<uint, uint, bool> differs) =>
        Bounds(a, b, a, (x, y, _) => differs(x, y));

    private (int, int, int, int) Bounds(uint[] a, uint[] b, uint[] c, Func<uint, uint, uint, bool> differs)
    {
        int left = Width;
        int top = Height;
        int right = 0;
        int bottom = 0;
        for (int y = 0; y < Height; y++)
        {
            int row = y * Width;
            for (int x = 0; x < Width; x++)
            {
                if (differs(a[row + x], b[row + x], c[row + x]))
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x + 1);
                    top = Math.Min(top, y);
                    bottom = y + 1;
                }
            }
        }

        return right > left ? (left, top, right, bottom) : Empty;
    }

    private static (int, int, int, int) Union((int L, int T, int R, int B) a, (int L, int T, int R, int B) b) =>
        a.R <= a.L ? b : b.R <= b.L ? a : (Math.Min(a.L, b.L), Math.Min(a.T, b.T), Math.Max(a.R, b.R), Math.Max(a.B, b.B));
}
