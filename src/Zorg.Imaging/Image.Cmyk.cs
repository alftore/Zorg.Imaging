using System;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging;

/// <content>Decoding CMYK files as their inks.</content>
public sealed partial class Image
{
    /// <summary>
    /// Decodes a CMYK file (a four-component JPEG, including YCCK, or a separated TIFF) as its inks instead of
    /// converting it to sRGB: for print work, such as a PDF that keeps <c>DeviceCMYK</c>. <see cref="Decode(byte[], DecodeOptions?)"/>
    /// converts the same file to sRGB.
    /// </summary>
    /// <param name="data">The whole file. It isn't copied, so don't change it while decoding.</param>
    /// <param name="options">
    /// The limits and options, or <see langword="null"/> for <see cref="DecodeOptions.Default"/>. Orientation and
    /// <see cref="DecodeOptions.ShrinkTo"/> apply as for <see cref="Decode(byte[], DecodeOptions?)"/>;
    /// <see cref="DecodeOptions.PixelFormat"/> doesn't, as each ink is an <see cref="PixelFormat.L8"/> plane.
    /// </param>
    /// <returns>The inks, which the caller disposes, or <see langword="null"/> when the file is an image but not CMYK.</returns>
    /// <exception cref="UnsupportedImageFormatException">The data isn't an image format this version can decode.</exception>
    /// <exception cref="ImageFormatException">The file is damaged or ends early.</exception>
    /// <exception cref="ImageLimitException">The image is larger than <see cref="ImageLimits"/> allows.</exception>
    /// <example>
    /// <code>
    /// using CmykImage? inks = Image.DecodeCmyk(jpeg, new DecodeOptions { ShrinkTo = (1200, 1200) });
    /// if (inks is not null)
    /// {
    ///     using CmykImage fitted = inks.Map(plane => plane.Fit(1200, 1200));
    ///     byte[] deviceCmyk = fitted.ToInterleaved(); // C, M, Y, K per pixel, as a PDF image stream holds them
    /// }
    /// </code>
    /// </example>
    public static CmykImage? DecodeCmyk(ReadOnlyMemory<byte> data, DecodeOptions? options = null)
    {
        options ??= DecodeOptions.Default;

        // The header says whether the file is CMYK, so other images aren't decoded only to be thrown away. A file
        // Identify can't read goes on to Decode, which throws the exception that says why.
        if (Identify(data) is { Succeeded: true, Info.IsCmyk: false })
        {
            return null;
        }

        Image decoded = Decode(data, options.WithInks());
        try
        {
            if (decoded.InkProfile is not { } profile)
            {
                return null;
            }

            return CmykImage.Split(decoded, profile);
        }
        finally
        {
            decoded.Dispose();
        }
    }
}

/// <summary>
/// A CMYK picture as its four inks: cyan, magenta, yellow and black planes, each an <see cref="PixelFormat.L8"/> image
/// from 0 (no ink) to 255 (full ink), with the file's CMYK colour profile. From <see cref="Image.DecodeCmyk"/>.
/// </summary>
/// <remarks>
/// The planes are ordinary images in <see cref="ColorSpace.LinearSrgb"/>, so <see cref="Map"/> can resize, crop, turn and
/// flip them with the usual operations, and resizing blends ink amounts directly, as print software does, without the
/// gamma that sRGB values would get. <see cref="ToInterleaved"/> gives the bytes a PDF <c>DeviceCMYK</c> image holds;
/// <see cref="ToSrgb"/> a picture for the screen.
/// </remarks>
public sealed class CmykImage : IDisposable
{
    private readonly byte[] _profile;

    private CmykImage(Image cyan, Image magenta, Image yellow, Image black, byte[] profile)
    {
        (Cyan, Magenta, Yellow, Black, _profile) = (cyan, magenta, yellow, black, profile);
    }

    /// <summary>The cyan ink, 0 (none) to 255 (full).</summary>
    public Image Cyan { get; }

    /// <summary>The magenta ink, 0 (none) to 255 (full).</summary>
    public Image Magenta { get; }

    /// <summary>The yellow ink, 0 (none) to 255 (full).</summary>
    public Image Yellow { get; }

    /// <summary>The black ink, 0 (none) to 255 (full).</summary>
    public Image Black { get; }

    /// <summary>The width in pixels.</summary>
    public int Width => Cyan.Width;

    /// <summary>The height in pixels.</summary>
    public int Height => Cyan.Height;

    /// <summary>The file's CMYK ICC profile, or empty when it has none (the inks are then device CMYK).</summary>
    public ReadOnlyMemory<byte> IccProfile => _profile;

    /// <summary>Applies the same operation to each ink, such as <c>plane => plane.Fit(1200, 1200)</c>.</summary>
    /// <param name="operation">An operation from one image to a new one; it's called four times.</param>
    /// <returns>New inks, which the caller disposes; these are unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operation"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The operation returned the same image, another layout, or planes of different sizes.</exception>
    /// <example><code>using CmykImage turned = inks.Map(plane => plane.Rotate(90));</code></example>
    public CmykImage Map(Func<Image, Image> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Image[] planes = new Image[4];
        try
        {
            Image[] inputs = [Cyan, Magenta, Yellow, Black];
            for (int i = 0; i < 4; i++)
            {
                Image result = operation(inputs[i]);
                if (Array.IndexOf(inputs, result) >= 0)
                {
                    throw new InvalidOperationException("The operation must return a new image, not the one it was given.");
                }

                planes[i] = result;
                if (result is null || result.PixelFormat != PixelFormat.L8 || result.Width != planes[0].Width || result.Height != planes[0].Height)
                {
                    throw new InvalidOperationException("The operation must give every ink an L8 image of the same size.");
                }

                result.ColorSpace = ColorSpace.LinearSrgb;
            }

            var mapped = new CmykImage(planes[0], planes[1], planes[2], planes[3], _profile);
            planes = [];
            return mapped;
        }
        finally
        {
            foreach (Image? plane in planes)
            {
                plane?.Dispose();
            }
        }
    }

    /// <summary>The inks interleaved: C, M, Y, K for each pixel, row by row, 0 no ink to 255 full, as a PDF <c>DeviceCMYK</c> image holds them.</summary>
    /// <returns>A new array of <see cref="Width"/> × <see cref="Height"/> × 4 bytes.</returns>
    /// <exception cref="InvalidOperationException">The picture is too large for one array.</exception>
    /// <exception cref="ObjectDisposedException">The inks have been disposed.</exception>
    /// <example><code>byte[] deviceCmyk = inks.ToInterleaved();</code></example>
    public byte[] ToInterleaved()
    {
        long size = (long)Width * Height * 4;
        if (size > Array.MaxLength)
        {
            throw new InvalidOperationException($"{Width} x {Height} CMYK is more than one array can hold.");
        }

        byte[] result = new byte[size];
        int width = Width;
        Rows(width, Height, (top, bottom) =>
        {
            for (int y = top; y < bottom; y++)
            {
                ReadOnlySpan<byte> c = Cyan.GetRowBytes(y), m = Magenta.GetRowBytes(y), ye = Yellow.GetRowBytes(y), k = Black.GetRowBytes(y);
                Span<byte> row = result.AsSpan(y * width * 4, width * 4);
                for (int x = 0; x < width; x++)
                {
                    (row[4 * x], row[(4 * x) + 1], row[(4 * x) + 2], row[(4 * x) + 3]) = (c[x], m[x], ye[x], k[x]);
                }
            }
        });
        return result;
    }

    /// <summary>
    /// The picture in sRGB, for the screen: through the CMYK profile when there is one, else by the device formula, as
    /// <see cref="Image.Decode(byte[], DecodeOptions?)"/> converts the file.
    /// </summary>
    /// <returns>A new <see cref="PixelFormat.Rgb24"/> image, which the caller disposes.</returns>
    /// <exception cref="ObjectDisposedException">The inks have been disposed.</exception>
    /// <example><code>using Image preview = inks.ToSrgb();</code></example>
    public Image ToSrgb()
    {
        var convert = Codecs.Jpeg.JpegCmyk.For(-1, _profile);
        var result = new Image(Width, Height, PixelFormat.Rgb24);
        try
        {
            int width = Width;
            Rows(width, Height, (top, bottom) =>
            {
                byte[][] rows = [new byte[width], new byte[width], new byte[width], new byte[width]];
                for (int y = top; y < bottom; y++)
                {
                    Cyan.GetRowBytes(y).CopyTo(rows[0]);
                    Magenta.GetRowBytes(y).CopyTo(rows[1]);
                    Yellow.GetRowBytes(y).CopyTo(rows[2]);
                    Black.GetRowBytes(y).CopyTo(rows[3]);
                    convert.WriteRow(rows, result.GetRowBytes(y), PixelFormat.Rgb24, width);
                }
            });
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>Returns the planes' memory to the pool.</summary>
    /// <example><code>inks.Dispose();</code></example>
    public void Dispose()
    {
        Cyan.Dispose();
        Magenta.Dispose();
        Yellow.Dispose();
        Black.Dispose();
    }

    /// <summary>Runs <paramref name="body"/> over bands of rows, in parallel for large pictures.</summary>
    private static void Rows(int width, int height, Action<int, int> body)
    {
        int bands = Parallelism.Bands((long)width * height, height / 16);
        Parallelism.For(bands, band => body((int)((long)height * band / bands), (int)((long)height * (band + 1) / bands)));
    }

    /// <summary>Splits a decoder's interleaved inks (C, M, Y, K in the four bytes of an Rgba32 image) into planes.</summary>
    internal static CmykImage Split(Image inks, byte[] profile)
    {
        int width = inks.Width, height = inks.Height;
        Image[] planes = new Image[4];
        try
        {
            for (int i = 0; i < 4; i++)
            {
                planes[i] = new Image(width, height, PixelFormat.L8) { ColorSpace = ColorSpace.LinearSrgb };
            }

            Image[] p = planes;
            Rows(width, height, (top, bottom) =>
            {
                for (int y = top; y < bottom; y++)
                {
                    ReadOnlySpan<byte> source = inks.GetRowBytes(y);
                    Span<byte> c = p[0].GetRowBytes(y), m = p[1].GetRowBytes(y), ye = p[2].GetRowBytes(y), k = p[3].GetRowBytes(y);
                    for (int x = 0; x < width; x++)
                    {
                        (c[x], m[x], ye[x], k[x]) = (source[4 * x], source[(4 * x) + 1], source[(4 * x) + 2], source[(4 * x) + 3]);
                    }
                }
            });

            var result = new CmykImage(planes[0], planes[1], planes[2], planes[3], profile);
            planes = [];
            return result;
        }
        finally
        {
            foreach (Image? plane in planes)
            {
                plane?.Dispose();
            }
        }
    }
}
