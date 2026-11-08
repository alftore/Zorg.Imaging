using System;
using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Zorg.Imaging;

/// <content>The pixel buffer.</content>
public sealed partial class Image : IDisposable
{
    private readonly int _length;
    private byte[]? _buffer;

    /// <summary>Creates an image with every pixel zero: transparent black, or black when the format has no alpha.</summary>
    /// <param name="width">The width in pixels, at least 1.</param>
    /// <param name="height">The height in pixels, at least 1.</param>
    /// <param name="pixelFormat">The pixel layout.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="width"/> or <paramref name="height"/> is less than 1, <paramref name="pixelFormat"/> isn't a
    /// format, or the pixels would need more than <see cref="Array.MaxLength"/> bytes.
    /// </exception>
    /// <remarks>
    /// The size is the caller's choice, so <see cref="ImageLimits"/> doesn't apply here; decoders check it before they
    /// create an image from a file.
    /// </remarks>
    /// <example><code>using var canvas = new Image(1200, 630, PixelFormat.Rgba32);</code></example>
    public Image(int width, int height, PixelFormat pixelFormat)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        int bytesPerPixel = GetBytesPerPixel(pixelFormat);
        long length = (long)width * height * bytesPerPixel;
        if (length > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                string.Create(CultureInfo.InvariantCulture, $"A {width} x {height} {pixelFormat} image needs {length} bytes, more than an array can hold ({Array.MaxLength})."));
        }

        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        BytesPerPixel = bytesPerPixel;
        _length = (int)length;

        // Rented memory holds whatever its last user left there: clear it, so no other image's pixels leak into this one.
        _buffer = ArrayPool<byte>.Shared.Rent(_length);
        _buffer.AsSpan(0, _length).Clear();
    }

    /// <summary>The width in pixels.</summary>
    /// <example><code>int width = image.Width;</code></example>
    public int Width { get; }

    /// <summary>The height in pixels.</summary>
    /// <example><code>int height = image.Height;</code></example>
    public int Height { get; }

    /// <summary>
    /// What the pixel values mean: which red, green and blue, and how values map to brightness. New images are
    /// <see cref="ColorSpace.Srgb"/>; decoders set the file's. Setting it labels the pixels differently, it doesn't
    /// change them. Encoders write it into the file, so the image looks the same wherever it's shown.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <example><code>bool vivid = image.ColorSpace == ColorSpace.DisplayP3;</code></example>
    public ColorSpace ColorSpace
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    } = ColorSpace.Srgb;

    /// <summary>The metadata from the file this image was decoded from, carried by operations, for the encoders.</summary>
    internal Metadata.SourceMetadata? SourceMetadata { get; set; }

    /// <summary>
    /// The metadata the image was decoded with (EXIF, XMP and IPTC, and the facts most wanted from them), or
    /// <see langword="null"/> when the file had none or the image was made in code. Operations carry it to the images
    /// they make. Encoders write it back only when their <see cref="MetadataOptions"/> say so.
    /// </summary>
    /// <example><code>string? camera = photo.Metadata?.CameraModel;</code></example>
    public ImageMetadata? Metadata
    {
        get
        {
            if (SourceMetadata is not { } source)
            {
                return null;
            }

            // Made once for each source; operations share the source, so their images share the view's work too.
            ImageMetadata? view = _metadataView;
            if (view is null || !ReferenceEquals(view.Source, source))
            {
                view = new ImageMetadata(source);
                _metadataView = view;
            }

            return view;
        }
    }

    private ImageMetadata? _metadataView;

    /// <summary>How each pixel is laid out.</summary>
    /// <example><code>bool hasAlpha = image.PixelFormat is PixelFormat.Rgba32 or PixelFormat.Bgra32 or PixelFormat.Rgba64;</code></example>
    public PixelFormat PixelFormat { get; }

    /// <summary>The bytes per pixel: 1 to 4 for 8-bit formats, 2 to 8 for 16-bit ones.</summary>
    /// <example><code>int bytesPerPixel = image.BytesPerPixel;</code></example>
    public int BytesPerPixel { get; }

    /// <summary>The bytes per row: <see cref="Width"/> times <see cref="BytesPerPixel"/>. Rows have no padding.</summary>
    /// <example><code>int stride = image.Stride;</code></example>
    public int Stride => Width * BytesPerPixel;

    /// <summary>Gets every pixel's bytes, row after row.</summary>
    /// <returns>The pixel bytes, <see cref="Stride"/> times <see cref="Height"/> long.</returns>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <example><code>await output.WriteAsync(image.GetPixelBytes().ToArray(), cancellationToken);</code></example>
    public Span<byte> GetPixelBytes() => Buffer.AsSpan(0, _length);

    /// <summary>Gets one row's bytes.</summary>
    /// <param name="y">The row, from 0 at the top.</param>
    /// <returns>The row, <see cref="Stride"/> bytes long.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="y"/> is outside the image.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <example><code>Span&lt;byte&gt; row = image.GetRowBytes(0);</code></example>
    public Span<byte> GetRowBytes(int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return Buffer.AsSpan(y * Stride, Stride);
    }

    /// <summary>Gets one row as pixels of the image's format.</summary>
    /// <typeparam name="T">
    /// The pixel type that matches <see cref="PixelFormat"/>: <see cref="Rgba32"/>, <see cref="Bgra32"/>,
    /// <see cref="Rgb24"/>, <see cref="L8"/>, <see cref="Rgba64"/>, <see cref="Rgb48"/> or <see cref="L16"/>.
    /// </typeparam>
    /// <param name="y">The row, from 0 at the top.</param>
    /// <returns>The row, <see cref="Width"/> pixels long.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="y"/> is outside the image.</exception>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> isn't the pixel type of <see cref="PixelFormat"/>.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <example>
    /// <code>
    /// foreach (ref Rgba32 pixel in image.GetRow&lt;Rgba32&gt;(y))
    /// {
    ///     pixel.A = 255;
    /// }
    /// </code>
    /// </example>
    public Span<T> GetRow<T>(int y)
        where T : unmanaged
    {
        PixelFormat requested = FormatOf<T>();
        if (requested != PixelFormat)
        {
            throw new InvalidOperationException(requested == PixelFormat.None
                ? $"{typeof(T).Name} isn't a pixel type. Use Rgba32, Bgra32, Rgb24, L8, Rgba64, Rgb48 or L16."
                : $"The image is {PixelFormat}, so its rows are {PixelFormat} pixels, not {typeof(T).Name}.");
        }

        return MemoryMarshal.Cast<byte, T>(GetRowBytes(y));
    }

    /// <summary>Creates a copy with its own pixel memory.</summary>
    /// <returns>The copy, which the caller disposes.</returns>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    /// <example><code>using Image backup = image.Clone();</code></example>
    public Image Clone()
    {
        Span<byte> source = GetPixelBytes();
        var copy = new Image(Width, Height, PixelFormat) { ColorSpace = ColorSpace, SourceMetadata = SourceMetadata };
        source.CopyTo(copy.GetPixelBytes());
        return copy;
    }

    /// <summary>Returns the pixel memory to the pool. The size and format stay readable; the pixels don't.</summary>
    /// <example><code>image.Dispose();</code></example>
    public void Dispose()
    {
        byte[]? buffer = _buffer;
        _buffer = null;
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Returns the format and size, such as <c>Rgba32 640x480</c>.</summary>
    /// <returns>A short description.</returns>
    /// <example><code>Console.WriteLine(image); // Rgba32 640x480</code></example>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{PixelFormat} {Width}x{Height}");

    internal static int GetBytesPerPixel(PixelFormat pixelFormat) => pixelFormat switch
    {
        PixelFormat.Rgba64 => 8,
        PixelFormat.Rgb48 => 6,
        PixelFormat.Rgba32 or PixelFormat.Bgra32 => 4,
        PixelFormat.Rgb24 => 3,
        PixelFormat.L16 => 2,
        PixelFormat.L8 => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat), pixelFormat, "Not a pixel format."),
    };

    // Each comparison is a constant once T is known, so the JIT and Native AOT reduce this to a single return.
    private static PixelFormat FormatOf<T>()
        where T : unmanaged
    {
        if (typeof(T) == typeof(Rgba32))
        {
            return PixelFormat.Rgba32;
        }

        if (typeof(T) == typeof(Bgra32))
        {
            return PixelFormat.Bgra32;
        }

        if (typeof(T) == typeof(Rgb24))
        {
            return PixelFormat.Rgb24;
        }

        if (typeof(T) == typeof(L8))
        {
            return PixelFormat.L8;
        }

        if (typeof(T) == typeof(Rgba64))
        {
            return PixelFormat.Rgba64;
        }

        if (typeof(T) == typeof(Rgb48))
        {
            return PixelFormat.Rgb48;
        }

        return typeof(T) == typeof(L16) ? PixelFormat.L16 : PixelFormat.None;
    }

    private byte[] Buffer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_buffer is null, this);
            return _buffer;
        }
    }
}
