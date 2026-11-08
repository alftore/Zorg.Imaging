using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Processing;

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
/// Knowing the steps before decoding lets JPEG decode at 1/2, 1/4 or 1/8 of its size when the first step makes it
/// much smaller (shrink-on-load): a 12-megapixel photo fitted to 400 x 300 decodes at 1/4, still at least twice the
/// target, and the resampler does the rest. PNG has no such shortcut. The steps then run in order.
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
    private readonly int _parallelism;

    private ImagePipeline(Source source, Step[] steps, DecodeOptions options, int parallelism = 0)
    {
        _source = source;
        _steps = steps;
        _options = options;
        _parallelism = parallelism;
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
        return new ImagePipeline(_source, _steps, options, _parallelism);
    }

    /// <summary>
    /// Sets how many cores the whole run (decode, steps and save) may use, overriding
    /// <see cref="ImageDefaults.MaxDegreeOfParallelism"/>. 1 keeps everything on the calling thread.
    /// </summary>
    /// <param name="maxDegreeOfParallelism">The number of cores, at least 1.</param>
    /// <returns>A new pipeline with the setting.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxDegreeOfParallelism"/> is less than 1.</exception>
    /// <example><code>await ImagePipeline.Load(upload).WithMaxDegreeOfParallelism(1).Fit(1600, 1600).SaveAsJpegAsync(output);</code></example>
    public ImagePipeline WithMaxDegreeOfParallelism(int maxDegreeOfParallelism)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegreeOfParallelism, 1);
        return new ImagePipeline(_source, _steps, _options, maxDegreeOfParallelism);
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

        return Sized(
            Describe($"Resize({width}, {height}", resampler),
            image => image.Resize(width, height, resampler),
            (w, h) => (width == 0 ? Math.Max((int)Math.Min(((2L * w * height) + h) / (2L * h), int.MaxValue), 1) : width,
                       height == 0 ? Math.Max((int)Math.Min(((2L * h * width) + w) / (2L * w), int.MaxValue), 1) : height));
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
        return Sized(Describe($"Fit({width}, {height}", resampler), image => image.Fit(width, height, resampler), (w, h) => Image.Inside(w, h, width, height));
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
        return Sized(Describe($"Fill({width}, {height}", resampler), image => image.Fill(width, height, resampler), (w, h) => Image.Covering(w, h, width, height));
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
        return Sized(Describe($"Pad({width}, {height}, {background}", resampler), image => image.Pad(width, height, background, resampler), (w, h) => Image.Inside(w, h, width, height));
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
        return Then(string.Create(CultureInfo.InvariantCulture, $"Crop({x}, {y}, {width}, {height})"), image => image.Crop(x, y, width, height), Shape.Other);
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

        return Then(
            string.Create(CultureInfo.InvariantCulture, $"Rotate({degrees})"),
            image => image.Rotate(degrees),
            degrees % 180 == 0 ? Shape.Same : Shape.Swapped);
    }

    /// <summary>Adds <see cref="Image.FlipHorizontal"/>: mirror left to right.</summary>
    /// <returns>A new pipeline with the step added.</returns>
    /// <example><code>ImagePipeline mirrored = ImagePipeline.Load(bytes).FlipHorizontal();</code></example>
    public ImagePipeline FlipHorizontal() => Then("FlipHorizontal()", image => image.FlipHorizontal(), Shape.Same);

    /// <summary>Adds <see cref="Image.FlipVertical"/>: mirror top to bottom.</summary>
    /// <returns>A new pipeline with the step added.</returns>
    /// <example><code>ImagePipeline reflected = ImagePipeline.Load(bytes).FlipVertical();</code></example>
    public ImagePipeline FlipVertical() => Then("FlipVertical()", image => image.FlipVertical(), Shape.Same);

    /// <summary>Adds <see cref="Image.Flatten"/>: draw over a solid colour, removing alpha.</summary>
    /// <param name="background">The colour behind the image.</param>
    /// <returns>A new pipeline with the step added.</returns>
    /// <example><code>ImagePipeline opaque = ImagePipeline.Load(bytes).Flatten(new Rgba32(255, 255, 255));</code></example>
    public ImagePipeline Flatten(Rgba32 background) =>
        Then(string.Create(CultureInfo.InvariantCulture, $"Flatten({background})"), image => image.Flatten(background), Shape.Same);

    /// <summary>Adds <see cref="Image.Blur"/>: a Gaussian blur in linear light.</summary>
    /// <param name="sigma">The standard deviation in pixels, more than 0 and at most <see cref="Image.MaxSigma"/>.</param>
    /// <returns>A new pipeline with the step added.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sigma"/> is out of range.</exception>
    /// <remarks>The radius is in the pixels the step gets, so a blur after a resize is measured at the new size.</remarks>
    /// <example><code>ImagePipeline backdrop = ImagePipeline.Load(bytes).Fit(800, 800).Blur(12);</code></example>
    public ImagePipeline Blur(double sigma)
    {
        CheckSigma(sigma);
        return Then(string.Create(CultureInfo.InvariantCulture, $"Blur({sigma})"), image => image.Blur(sigma), Shape.Other);
    }

    /// <summary>Adds <see cref="Image.Sharpen"/>: an unsharp mask.</summary>
    /// <param name="sigma">The blur's standard deviation in pixels, more than 0 and at most <see cref="Image.MaxSigma"/>.</param>
    /// <param name="amount">How far to move from the blur, more than 0 and at most 10.</param>
    /// <returns>A new pipeline with the step added.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sigma"/> or <paramref name="amount"/> is out of range.</exception>
    /// <example><code>ImagePipeline crisp = ImagePipeline.Load(bytes).Fit(800, 800).Sharpen(0.7, 0.5);</code></example>
    public ImagePipeline Sharpen(double sigma = 1, double amount = 1)
    {
        CheckSigma(sigma);
        if (!(amount > 0 && amount <= 10))
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "The amount must be more than 0 and at most 10.");
        }

        return Then(string.Create(CultureInfo.InvariantCulture, $"Sharpen({sigma}, {amount})"), image => image.Sharpen(sigma, amount), Shape.Other);
    }

    /// <summary>Adds <see cref="Image.DrawImage"/>: draw another image over this one, as a watermark.</summary>
    /// <param name="overlay">The image to draw. The pipeline uses it when it runs, so it must not be disposed before then; it isn't changed.</param>
    /// <param name="x">Where its left edge goes, in the image the step gets.</param>
    /// <param name="y">Where its top edge goes.</param>
    /// <param name="opacity">How opaque to draw it, more than 0 and at most 1.</param>
    /// <returns>A new pipeline with the step added.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="overlay"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="opacity"/> is out of range.</exception>
    /// <example><code>ImagePipeline marked = ImagePipeline.Load(bytes).Fit(1200, 1200).DrawImage(logo, 16, 16, opacity: 0.6);</code></example>
    public ImagePipeline DrawImage(Image overlay, int x, int y, double opacity = 1)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (!(opacity > 0 && opacity <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(opacity), opacity, "The opacity must be more than 0 and at most 1.");
        }

        return Then(string.Create(CultureInfo.InvariantCulture, $"DrawImage({overlay}, {x}, {y}, {opacity})"), image => image.DrawImage(overlay, x, y, opacity), Shape.Other);
    }

    /// <summary>Adds <see cref="Image.Trim"/>: crop away borders of the top-left pixel's colour.</summary>
    /// <param name="threshold">How far a channel may be from the border colour and still match, 0 to 255.</param>
    /// <returns>A new pipeline with the step added.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threshold"/> is out of range.</exception>
    /// <example><code>ImagePipeline tight = ImagePipeline.Load(bytes).Trim().Fit(800, 800);</code></example>
    public ImagePipeline Trim(int threshold = 10)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(threshold);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(threshold, 255);
        return Then(string.Create(CultureInfo.InvariantCulture, $"Trim({threshold})"), image => image.Trim(threshold), Shape.Other);
    }

    /// <summary>
    /// Adds <see cref="Image.ToSrgb"/>: convert the pixels to sRGB, clipping colours outside it. Saving doesn't need
    /// this step, since the encoders follow their <see cref="ColorSpaceHandling"/>; add it when the pixels themselves
    /// must be sRGB, such as before <see cref="ToImageAsync"/>.
    /// </summary>
    /// <returns>A new pipeline with the step added.</returns>
    /// <remarks>An HDR source, or an ICC profile that isn't matrix/TRC, fails with <see cref="NotSupportedException"/> when the pipeline runs.</remarks>
    /// <example><code>using Image srgb = await ImagePipeline.Load(bytes).Fit(800, 800).ToSrgb().ToImageAsync();</code></example>
    public ImagePipeline ToSrgb() => Then("ToSrgb()", image => image.ToSrgb(), Shape.Same);

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
        using Parallelism.Scope scope = Parallelism.Use(_parallelism);
        // When a resize comes before anything that needs every pixel (a crop), the decoder learns its target and may
        // decode smaller (JPEG shrink-on-load).
        DecodeOptions options = ShrinkTarget(_steps) is { } target ? _options.WithShrinkTarget(target) : _options;
        (Image image, bool owned) = await _source.OpenAsync(options, cancellationToken).ConfigureAwait(false);
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
    public ValueTask SaveAsPngAsync(Stream stream, PngEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(stream, "SaveAsPngAsync", (image, output, token) => image.SaveAsPngAsync(output, options, token), cancellationToken);

    /// <summary>Runs the pipeline and writes the result as a JPEG file.</summary>
    /// <param name="stream">A writable stream; it stays open.</param>
    /// <param name="options">The JPEG options, or <see langword="null"/> for <see cref="JpegEncoderOptions.Default"/>: quality 80, 4:2:0.</param>
    /// <param name="cancellationToken">Cancels reading, stops between steps, and cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="stream"/> can't write, the result is too large for JPEG, or its colour space can't be written.
    /// </exception>
    /// <exception cref="UnsupportedImageFormatException">The source isn't an image format this version can decode.</exception>
    /// <exception cref="ImageFormatException">The file is damaged or ends early.</exception>
    /// <exception cref="ImageLimitException">The image is larger than the decode limits allow.</exception>
    /// <exception cref="InvalidOperationException">The pipeline was loaded from a stream and has already run.</exception>
    /// <exception cref="IOException">Reading or writing fails.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>See <see cref="Image.SaveAsJpeg"/> for what is written.</remarks>
    /// <example><code>await ImagePipeline.Load(upload).Fit(1600, 1600).SaveAsJpegAsync(output, cancellationToken: cancellationToken);</code></example>
    public ValueTask SaveAsJpegAsync(Stream stream, JpegEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(stream, "SaveAsJpegAsync", (image, output, token) => image.SaveAsJpegAsync(output, options, token), cancellationToken);

    /// <summary>Runs the pipeline and writes the result as a WebP file.</summary>
    /// <param name="stream">A writable stream. The file is written at its current position; the stream stays open.</param>
    /// <param name="options">The WebP options, or <see langword="null"/> for <see cref="WebpEncoderOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels reading, stops between steps, and cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="stream"/> can't write, the result is too large for WebP, or its colour space can't be written.
    /// </exception>
    /// <exception cref="UnsupportedImageFormatException">The source isn't an image format this version can decode.</exception>
    /// <exception cref="ImageFormatException">The file is damaged or ends early.</exception>
    /// <exception cref="ImageLimitException">The image is larger than the decode limits allow.</exception>
    /// <exception cref="InvalidOperationException">The pipeline was loaded from a stream and has already run.</exception>
    /// <exception cref="IOException">Reading or writing fails.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>See <see cref="Image.SaveAsWebp"/> for what is written.</remarks>
    /// <example><code>await ImagePipeline.Load(upload).Fit(1600, 1600).SaveAsWebpAsync(output, cancellationToken: cancellationToken);</code></example>
    public ValueTask SaveAsWebpAsync(Stream stream, WebpEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(stream, "SaveAsWebpAsync", (image, output, token) => image.SaveAsWebpAsync(output, options, token), cancellationToken);

    /// <summary>Runs the pipeline and writes the result as a GIF file.</summary>
    /// <param name="stream">A writable stream. The file is written at its current position; the stream stays open.</param>
    /// <param name="options">The GIF options, or <see langword="null"/> for <see cref="GifEncoderOptions.Default"/>.</param>
    /// <param name="cancellationToken">Cancels reading, stops between steps, and cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="stream"/> can't write, the result is too large for GIF, or its colour space can't be written.
    /// </exception>
    /// <exception cref="UnsupportedImageFormatException">The source isn't an image format this version can decode.</exception>
    /// <exception cref="ImageFormatException">The file is damaged or ends early.</exception>
    /// <exception cref="ImageLimitException">The image is larger than the decode limits allow.</exception>
    /// <exception cref="InvalidOperationException">The pipeline was loaded from a stream and has already run.</exception>
    /// <exception cref="IOException">Reading or writing fails.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>See <see cref="Image.SaveAsGif"/> for what is written.</remarks>
    /// <example><code>await ImagePipeline.Load(upload).Fit(64, 64).SaveAsGifAsync(output, cancellationToken: cancellationToken);</code></example>
    public ValueTask SaveAsGifAsync(Stream stream, GifEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(stream, "SaveAsGifAsync", (image, output, token) => image.SaveAsGifAsync(output, options, token), cancellationToken);

    /// <summary>
    /// Runs the pipeline and writes the result in the given format with that format's default options, for when the
    /// format is chosen at run time, such as from a request's <c>Accept</c> header.
    /// </summary>
    /// <param name="stream">A writable stream. The file is written at its current position; the stream stays open.</param>
    /// <param name="format">
    /// <see cref="ImageFormat.Png"/>, <see cref="ImageFormat.Jpeg"/>, <see cref="ImageFormat.Webp"/> or
    /// <see cref="ImageFormat.Gif"/>. For options, call <see cref="SaveAsPngAsync"/> and the others instead.
    /// </param>
    /// <param name="cancellationToken">Cancels reading, stops between steps, and cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="format"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">This version can't write <paramref name="format"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="stream"/> can't write, the result is too large for the format, or its colour space can't be written.
    /// </exception>
    /// <exception cref="UnsupportedImageFormatException">The source isn't an image format this version can decode.</exception>
    /// <exception cref="ImageFormatException">The file is damaged or ends early.</exception>
    /// <exception cref="ImageLimitException">The image is larger than the decode limits allow.</exception>
    /// <exception cref="InvalidOperationException">The pipeline was loaded from a stream and has already run.</exception>
    /// <exception cref="IOException">Reading or writing fails.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <example>
    /// <code>
    /// ImageFormat format = accept.Contains("image/webp") ? ImageFormat.Webp : ImageFormat.Jpeg;
    /// response.ContentType = format.MimeType;
    /// await ImagePipeline.Load(path).Fit(800, 800).SaveAsAsync(response.Body, format, cancellationToken);
    /// </code>
    /// </example>
    public ValueTask SaveAsAsync(Stream stream, ImageFormat format, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(format);
        Func<Image, Stream, CancellationToken, ValueTask> save = Saver(format)
            ?? throw new ArgumentException($"This version can't write {format.Name} files; it writes PNG, JPEG, WebP and GIF.", nameof(format));
        return SaveCoreAsync(stream, "SaveAsAsync", save, cancellationToken);
    }

    /// <summary>
    /// Runs the pipeline and writes the result to a file, in the format its extension names, with that format's default
    /// options. The file is replaced only once the whole result is written, so a failure leaves any earlier file as it was.
    /// </summary>
    /// <param name="path">
    /// The file to write, ending in <c>.png</c>, <c>.jpg</c>, <c>.jpeg</c>, <c>.webp</c> or <c>.gif</c> (any case).
    /// </param>
    /// <param name="cancellationToken">Cancels reading, stops between steps, and cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty, or its extension isn't a format this version writes.</exception>
    /// <exception cref="NotSupportedException">The result is too large for the format, or its colour space can't be written.</exception>
    /// <exception cref="UnsupportedImageFormatException">The source isn't an image format this version can decode.</exception>
    /// <exception cref="ImageFormatException">The file is damaged or ends early.</exception>
    /// <exception cref="ImageLimitException">The image is larger than the decode limits allow.</exception>
    /// <exception cref="InvalidOperationException">The pipeline was loaded from a stream and has already run.</exception>
    /// <exception cref="IOException">Reading or writing fails.</exception>
    /// <exception cref="UnauthorizedAccessException">The file or its folder can't be written.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <example><code>await ImagePipeline.Load("photo.jpg").Fit(1200, 1200).SaveAsync("photo.webp");</code></example>
    public async ValueTask SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string extension = Path.GetExtension(path);
        Func<Image, Stream, CancellationToken, ValueTask> save = Saver(FormatOf(extension))
            ?? throw new ArgumentException(
                $"The extension '{extension}' doesn't name a format this version writes: use .png, .jpg, .jpeg, .webp or .gif.",
                nameof(path));

        string full = Path.GetFullPath(path);
        string temporary = Path.Combine(
            Path.GetDirectoryName(full) ?? ".",
            "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await using (file.ConfigureAwait(false))
            {
                await SaveCoreAsync(file, "SaveAsync", save, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, full, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
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

    private static ImageFormat? FormatOf(string extension)
    {
        foreach (ImageFormat format in (ReadOnlySpan<ImageFormat>)[ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Webp, ImageFormat.Gif])
        {
            foreach (string known in format.FileExtensions)
            {
                if (string.Equals(known, extension, StringComparison.OrdinalIgnoreCase))
                {
                    return format;
                }
            }
        }

        return null;
    }

    private static Func<Image, Stream, CancellationToken, ValueTask>? Saver(ImageFormat? format) =>
        format == ImageFormat.Png ? (image, stream, token) => image.SaveAsPngAsync(stream, null, token)
        : format == ImageFormat.Jpeg ? (image, stream, token) => image.SaveAsJpegAsync(stream, null, token)
        : format == ImageFormat.Webp ? (image, stream, token) => image.SaveAsWebpAsync(stream, null, token)
        : format == ImageFormat.Gif ? (image, stream, token) => image.SaveAsGifAsync(stream, null, token)
        : null;

    /// <summary>
    /// The size the first resize makes, seen from the decoded (upright) size, when only steps that keep every pixel
    /// (turns, flips, flattening, colour conversion) come before it; <see langword="null"/> when a crop or nothing
    /// resizes first.
    /// </summary>
    private static Func<int, int, (int Width, int Height)>? ShrinkTarget(Step[] steps)
    {
        bool swapped = false;
        foreach (Step step in steps)
        {
            switch (step.Shape)
            {
                case Shape.Same:
                    continue;
                case Shape.Swapped:
                    swapped = !swapped;
                    continue;
                case Shape.Sized when step.Size is { } size:
                    return swapped
                        ? (width, height) =>
                        {
                            (int w, int h) = size(height, width);
                            return (h, w);
                        }
                    : size;
                default:
                    return null;
            }
        }

        return null;
    }

    private async ValueTask SaveCoreAsync(Stream stream, string method, Func<Image, Stream, CancellationToken, ValueTask> save, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new NotSupportedException(method + " needs a writable stream.");
        }

        using Parallelism.Scope scope = Parallelism.Use(_parallelism);
        if (_steps.Length == 0 && _source is ImageSource { Image: Image unchanged })
        {
            cancellationToken.ThrowIfCancellationRequested();
            await save(unchanged, stream, cancellationToken).ConfigureAwait(false);
            return;
        }

        using Image image = await ToImageAsync(cancellationToken).ConfigureAwait(false);
        await save(image, stream, cancellationToken).ConfigureAwait(false);
    }

    private static void CheckSigma(double sigma)
    {
        if (!(sigma > 0 && sigma <= Image.MaxSigma))
        {
            throw new ArgumentOutOfRangeException(nameof(sigma), sigma, "The standard deviation must be more than 0 and at most Image.MaxSigma pixels.");
        }
    }

    private static void CheckBox(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
    }

    private static string Describe(string start, Resampler? resampler) =>
        resampler is null ? start + ")" : start + ", " + resampler.Name + ")";

    private ImagePipeline Sized(string name, Func<Image, Image> apply, Func<int, int, (int Width, int Height)> size) =>
        Then(name, apply, Shape.Sized, size);

    private ImagePipeline Then(string name, Func<Image, Image> apply, Shape shape, Func<int, int, (int Width, int Height)>? size = null)
    {
        var steps = new Step[_steps.Length + 1];
        _steps.CopyTo(steps, 0);
        steps[^1] = new Step(name, apply, shape, size);
        return new ImagePipeline(_source, steps, _options, _parallelism);
    }

    /// <summary>What a step does to the image's size, which decides whether the decoder may shrink before it.</summary>
    private enum Shape
    {
        /// <summary>Needs every source pixel where it is, such as a crop.</summary>
        Other,

        /// <summary>Keeps the size and treats every pixel alike.</summary>
        Same,

        /// <summary>Swaps width and height, a quarter turn.</summary>
        Swapped,

        /// <summary>Resizes; <c>Size</c> gives the size it makes from the size it gets.</summary>
        Sized,
    }

    /// <summary>One step. <c>Size</c>, for steps that resize, gives the size the step makes from the size it gets.</summary>
    private sealed record Step(string Name, Func<Image, Image> Apply, Shape Shape, Func<int, int, (int Width, int Height)>? Size);

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
