using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Imaging;

/// <summary>
/// Load, change and save an image in one chain. Nothing is read until the result is asked for: <c>Load</c> records
/// where the file is, each step records what to do, and <c>SaveAs...Async</c> or <see cref="ToImageAsync"/> runs it.
/// </summary>
/// <remarks>
/// <para>
/// A pipeline is immutable: each step returns a new one, so a configured pipeline can be kept and reused, and the
/// steps' arguments are checked when the step is added, not later. Images made along the way are disposed as soon as
/// the next step has used them.
/// </para>
/// <para>
/// Knowing every step before decoding is what will let JPEG decode at a fraction of its size when the result is
/// small (shrink-on-load). PNG has no such shortcut, so for now the pipeline decodes and then runs the steps in order.
/// </para>
/// <para>
/// A pipeline loaded from a stream reads it when run, so it can run once; one loaded from bytes, a file or an
/// <see cref="Image"/> can run any number of times.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await ImagePipeline.Load(request.Body)
///     .Fit(1600, 1600)
///     .SaveAsPngAsync(response.Body, cancellationToken: cancellationToken);
/// </code>
/// </example>
public sealed class ImagePipeline
{
    private readonly Source _source;
    private readonly Step[] _steps;
    private readonly DecodeOptions _options;

    private ImagePipeline(Source source, Step[] steps, DecodeOptions options)
    {
        _source = source;
        _steps = steps;
        _options = options;
    }

    /// <summary>Starts a pipeline from a whole file in memory.</summary>
    /// <param name="data">The file. It isn't copied, so don't change it while the pipeline may run.</param>
    /// <returns>The pipeline, decoding with <see cref="DecodeOptions.Default"/> unless <see cref="WithDecodeOptions"/> says otherwise.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    /// <example><code>ImagePipeline thumbnail = ImagePipeline.Load(bytes).Fit(320, 320);</code></example>
    public static ImagePipeline Load(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new ImagePipeline(new BytesSource(data), [], DecodeOptions.Default);
    }

    /// <summary>Starts a pipeline from a stream, which is read when the pipeline runs.</summary>
    /// <param name="stream">
    /// A readable stream at the start of the file; it needn't seek, and it isn't disposed. The stream from
    /// <see cref="ImageFormats.PeekAsync"/> works here, including after <see cref="Image.IdentifyAsync"/>.
    /// </param>
    /// <returns>The pipeline, which can run once.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="stream"/> can't read.</exception>
    /// <example><code>await ImagePipeline.Load(request.Body).Fit(1600, 1600).SaveAsPngAsync(output);</code></example>
    public static ImagePipeline Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new NotSupportedException("The pipeline needs a readable stream.");
        }

        return new ImagePipeline(new StreamSource(stream), [], DecodeOptions.Default);
    }

    /// <summary>Starts a pipeline from a file, which is opened and read when the pipeline runs.</summary>
    /// <param name="path">The file's path.</param>
    /// <returns>The pipeline.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <remarks>A missing or unreadable file is reported when the pipeline runs, as the <see cref="IOException"/> opening it throws.</remarks>
    /// <example><code>using Image thumb = await ImagePipeline.Load("photo.png").Fit(320, 320).ToImageAsync();</code></example>
    public static ImagePipeline Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new ImagePipeline(new FileSource(path), [], DecodeOptions.Default);
    }

    /// <summary>Starts a pipeline from an image already in memory. The pipeline never changes or disposes it.</summary>
    /// <param name="image">The image.</param>
    /// <returns>The pipeline.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <example><code>using Image square = await ImagePipeline.From(image).Fill(256, 256).ToImageAsync();</code></example>
    public static ImagePipeline From(Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new ImagePipeline(new ImageSource(image), [], DecodeOptions.Default);
    }

    /// <summary>Sets the limits and options the source is decoded with. A pipeline from an <see cref="Image"/> ignores them.</summary>
    /// <param name="options">The options, such as tighter <see cref="ImageLimits"/> for uploads.</param>
    /// <returns>A new pipeline with the options.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// var uploads = new DecodeOptions { Limits = new ImageLimits { MaxPixels = 40_000_000 } };
    /// ImagePipeline pipeline = ImagePipeline.Load(request.Body).WithDecodeOptions(uploads).Fit(1600, 1600);
    /// </code>
    /// </example>
    public ImagePipeline WithDecodeOptions(DecodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new ImagePipeline(_source, _steps, options);
    }

    /// <summary>Adds <see cref="Image.Resize"/>.</summary>
    /// <param name="width">The new width, or 0 to keep the aspect ratio.</param>
    /// <param name="height">The new height, or 0 to keep the aspect ratio.</param>
    /// <param name="resampler">The filter, or <see langword="null"/> for the default.</param>
    /// <returns>A new pipeline with the step added.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is negative.</exception>
    /// <exception cref="ArgumentException">Both are 0.</exception>
    /// <example><code>ImagePipeline wide = ImagePipeline.Load(bytes).Resize(1200, 0);</code></example>
    public ImagePipeline Resize(int width, int height, Resampler? resampler = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        if (width == 0 && height == 0)
        {
            throw new ArgumentException("Give the width, the height or both; 0 for both leaves no size to keep.", nameof(width));
        }

        return Then(Describe($"Resize({width}, {height}", resampler), image => image.Resize(width, height, resampler));
    }

    /// <summary>Adds <see cref="Image.Fit"/>: shrink to fit inside a box, never enlarging.</summary>
    /// <param name="width">The box width, at least 1.</param>
    /// <param name="height">The box height, at least 1.</param>
    /// <param name="resampler">The filter, or <see langword="null"/> for <see cref="Resampler.Lanczos3"/>.</param>
    /// <returns>A new pipeline with the step added.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is less than 1.</exception>
    /// <example><code>ImagePipeline web = ImagePipeline.Load(bytes).Fit(1600, 1600);</code></example>
    public ImagePipeline Fit(int width, int height, Resampler? resampler = null)
    {
        CheckBox(width, height);
        return Then(Describe($"Fit({width}, {height}", resampler), image => image.Fit(width, height, resampler));
    }

    /// <summary>Adds <see cref="Image.Fill"/>: cover a box exactly, cropping the overhang from the centre.</summary>
    /// <param name="width">The width of the result, at least 1.</param>
    /// <param name="height">The height of the result, at least 1.</param>
    /// <param name="resampler">The filter, or <see langword="null"/> for the default.</param>
    /// <returns>A new pipeline with the step added.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is less than 1.</exception>
    /// <example><code>ImagePipeline avatar = ImagePipeline.Load(bytes).Fill(256, 256);</code></example>
    public ImagePipeline Fill(int width, int height, Resampler? resampler = null)
    {
        CheckBox(width, height);
        return Then(Describe($"Fill({width}, {height}", resampler), image => image.Fill(width, height, resampler));
    }

    /// <summary>Adds <see cref="Image.Pad"/>: fit inside a box, then centre on a background colour.</summary>
    /// <param name="width">The width of the result, at least 1.</param>
    /// <param name="height">The height of the result, at least 1.</param>
    /// <param name="background">The colour around the image.</param>
    /// <param name="resampler">The filter, or <see langword="null"/> for <see cref="Resampler.Lanczos3"/>.</param>
    /// <returns>A new pipeline with the step added.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is less than 1.</exception>
    /// <example><code>ImagePipeline tile = ImagePipeline.Load(bytes).Pad(300, 300, new Rgba32(255, 255, 255));</code></example>
    public ImagePipeline Pad(int width, int height, Rgba32 background, Resampler? resampler = null)
    {
        CheckBox(width, height);
        return Then(Describe($"Pad({width}, {height}, {background}", resampler), image => image.Pad(width, height, background, resampler));
    }

    /// <summary>Adds <see cref="Image.Crop"/>. Whether the rectangle fits is checked when the pipeline runs.</summary>
    /// <param name="x">The left edge, from 0.</param>
    /// <param name="y">The top edge, from 0.</param>
    /// <param name="width">The width, at least 1.</param>
    /// <param name="height">The height, at least 1.</param>
    /// <returns>A new pipeline with the step added.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is negative, or a side is less than 1.</exception>
    /// <example><code>ImagePipeline banner = ImagePipeline.Load(bytes).Crop(0, 100, 1200, 300);</code></example>
    public ImagePipeline Crop(int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        CheckBox(width, height);
        return Then(string.Create(CultureInfo.InvariantCulture, $"Crop({x}, {y}, {width}, {height})"), image => image.Crop(x, y, width, height));
    }

    /// <summary>Adds <see cref="Image.Rotate"/>: a clockwise turn by a multiple of 90°.</summary>
    /// <param name="degrees">90, 180 or 270; negative turns anticlockwise.</param>
    /// <returns>A new pipeline with the step added.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="degrees"/> isn't a multiple of 90.</exception>
    /// <example><code>ImagePipeline upright = ImagePipeline.Load(bytes).Rotate(90);</code></example>
    public ImagePipeline Rotate(int degrees)
    {
        if (degrees % 90 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "Only multiples of 90 degrees turn an image without resampling it.");
        }

        return Then(string.Create(CultureInfo.InvariantCulture, $"Rotate({degrees})"), image => image.Rotate(degrees));
    }

    /// <summary>Adds <see cref="Image.FlipHorizontal"/>: mirror left to right.</summary>
    /// <returns>A new pipeline with the step added.</returns>
    /// <example><code>ImagePipeline mirrored = ImagePipeline.Load(bytes).FlipHorizontal();</code></example>
    public ImagePipeline FlipHorizontal() => Then("FlipHorizontal()", image => image.FlipHorizontal());

    /// <summary>Adds <see cref="Image.FlipVertical"/>: mirror top to bottom.</summary>
    /// <returns>A new pipeline with the step added.</returns>
    /// <example><code>ImagePipeline reflected = ImagePipeline.Load(bytes).FlipVertical();</code></example>
    public ImagePipeline FlipVertical() => Then("FlipVertical()", image => image.FlipVertical());

    /// <summary>Adds <see cref="Image.Flatten"/>: draw over a solid colour, removing alpha.</summary>
    /// <param name="background">The colour behind the image.</param>
    /// <returns>A new pipeline with the step added.</returns>
    /// <example><code>ImagePipeline opaque = ImagePipeline.Load(bytes).Flatten(new Rgba32(255, 255, 255));</code></example>
    public ImagePipeline Flatten(Rgba32 background) => Then(string.Create(CultureInfo.InvariantCulture, $"Flatten({background})"), image => image.Flatten(background));

    /// <summary>Runs the pipeline and returns the result.</summary>
    /// <param name="cancellationToken">Cancels reading and stops between steps.</param>
    /// <returns>The image, which the caller disposes.</returns>
    /// <exception cref="UnsupportedImageFormatException">The source isn't an image format this version can decode.</exception>
    /// <exception cref="ImageFormatException">The file is damaged or ends early.</exception>
    /// <exception cref="ImageLimitException">The image is larger than the decode limits allow.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A <see cref="Crop"/> reaches outside the image it's given.</exception>
    /// <exception cref="InvalidOperationException">The pipeline was loaded from a stream and has already run.</exception>
    /// <exception cref="IOException">Reading the stream or file fails.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <example><code>using Image thumb = await ImagePipeline.Load(bytes).Fit(320, 320).ToImageAsync();</code></example>
    public async ValueTask<Image> ToImageAsync(CancellationToken cancellationToken = default)
    {
        (Image image, bool owned) = await _source.OpenAsync(_options, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (Step step in _steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Image next = step.Apply(image);
                if (owned)
                {
                    image.Dispose();
                }

                image = next;
                owned = true;
            }
        }
        catch
        {
            if (owned)
            {
                image.Dispose();
            }

            throw;
        }

        // With no steps, the caller's own image comes back as a copy, so the result is always the caller's to dispose.
        if (!owned)
        {
            return image.Clone();
        }

        return image;
    }

    /// <summary>Runs the pipeline and writes the result as a PNG file.</summary>
    /// <param name="stream">A writable stream; it stays open.</param>
    /// <param name="options">The PNG options, or <see langword="null"/> for <see cref="PngEncoderOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels reading, stops between steps, and cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><paramref name="stream"/> can't write.</exception>
    /// <exception cref="UnsupportedImageFormatException">The source isn't an image format this version can decode.</exception>
    /// <exception cref="ImageFormatException">The file is damaged or ends early.</exception>
    /// <exception cref="ImageLimitException">The image is larger than the decode limits allow.</exception>
    /// <exception cref="InvalidOperationException">The pipeline was loaded from a stream and has already run.</exception>
    /// <exception cref="IOException">Reading or writing fails.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>See <see cref="Image.SaveAsPng"/> for what is written.</remarks>
    /// <example><code>await ImagePipeline.Load(upload).Fit(1600, 1600).SaveAsPngAsync(output, cancellationToken: cancellationToken);</code></example>
    public async ValueTask SaveAsPngAsync(Stream stream, PngEncoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new NotSupportedException("SaveAsPngAsync needs a writable stream.");
        }

        if (_steps.Length == 0 && _source is ImageSource { Image: Image unchanged })
        {
            await unchanged.SaveAsPngAsync(stream, options, cancellationToken).ConfigureAwait(false);
            return;
        }

        using Image image = await ToImageAsync(cancellationToken).ConfigureAwait(false);
        await image.SaveAsPngAsync(stream, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Describes the source and the steps, such as <c>bytes → Fit(320, 320) → Flatten(Rgba32(255, 255, 255, 255))</c>.</summary>
    /// <returns>A short description.</returns>
    /// <example><code>Console.WriteLine(pipeline);</code></example>
    public override string ToString()
    {
        var text = new StringBuilder(_source.Name);
        foreach (Step step in _steps)
        {
            text.Append(" → ").Append(step.Name);
        }

        return text.ToString();
    }

    private static void CheckBox(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
    }

    private static string Describe(string start, Resampler? resampler) =>
        resampler is null ? start + ")" : start + ", " + resampler.Name + ")";

    private ImagePipeline Then(string name, Func<Image, Image> apply)
    {
        var steps = new Step[_steps.Length + 1];
        _steps.CopyTo(steps, 0);
        steps[^1] = new Step(name, apply);
        return new ImagePipeline(_source, steps, _options);
    }

    private sealed record Step(string Name, Func<Image, Image> Apply);

    /// <summary>Where the pixels come from. <c>Owned</c> says whether the pipeline must dispose the image it gets.</summary>
    private abstract class Source(string name)
    {
        public string Name { get; } = name;

        public abstract ValueTask<(Image Image, bool Owned)> OpenAsync(DecodeOptions options, CancellationToken cancellationToken);
    }

    private sealed class BytesSource(byte[] data) : Source("bytes")
    {
        public override ValueTask<(Image Image, bool Owned)> OpenAsync(DecodeOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult((Image.Decode(data, options), true));
        }
    }

    private sealed class StreamSource(Stream stream) : Source("stream")
    {
        private int _used;

        public override async ValueTask<(Image Image, bool Owned)> OpenAsync(DecodeOptions options, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _used, 1) != 0)
            {
                throw new InvalidOperationException("A pipeline loaded from a stream reads it when it runs, so it can run only once.");
            }

            return (await Image.DecodeAsync(stream, options, cancellationToken).ConfigureAwait(false), true);
        }
    }

    private sealed class FileSource(string path) : Source("file")
    {
        public override async ValueTask<(Image Image, bool Owned)> OpenAsync(DecodeOptions options, CancellationToken cancellationToken)
        {
            FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (file.ConfigureAwait(false))
            {
                return (await Image.DecodeAsync(file, options, cancellationToken).ConfigureAwait(false), true);
            }
        }
    }

    private sealed class ImageSource(Image image) : Source("image")
    {
        public Image Image { get; } = image;

        public override ValueTask<(Image Image, bool Owned)> OpenAsync(DecodeOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult((Image, false));
        }
    }
}
