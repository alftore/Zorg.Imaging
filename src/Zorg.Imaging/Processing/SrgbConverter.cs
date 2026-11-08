using System;
using System.Runtime.InteropServices;
using Zorg.Imaging.Metadata;

namespace Zorg.Imaging.Processing;

/// <summary>
/// Converts an image's pixels from its colour space to sRGB: each channel through its curve to linear light, through
/// the D50 connection space (Bradford-adapted, as ICC profiles are) into linear sRGB, and back through the sRGB curve.
/// </summary>
/// <remarks>
/// <para>
/// Convertible: the built-in and CICP spaces with known primaries and an SDR curve, PNG gamma and chromaticities,
/// and ICC matrix/TRC and grey profiles. Not convertible: HDR (PQ, HLG), which needs tone mapping, and LUT-based or
/// malformed profiles.
/// </para>
/// <para>
/// Colours outside sRGB are clipped. A pixel counts as inside when clipping moves it less than one 8-bit step, so a
/// photo that came from sRGB and was tagged with a wider space still fits. Grey images keep the grey axis, which every
/// display profile maps to the connection space's white, so only the curve changes and one table does all of it.
/// </para>
/// </remarks>
internal static class SrgbConverter
{
    // Linear-light bounds of one 8-bit sRGB step outside 0 to 1: 1/255 below 0 on the linear segment, and the sRGB
    // curve's slope at 1 (2.4 / 1.055 in encoded terms) above.
    private const float Low = -1f / 255 / 12.92f;
    private const float High = 1 + (1f / 255 * 2.4f / 1.055f);

    private static readonly double[] FromPcs = ColourMath.Invert(ColourMath.RgbToPcs(ColourMath.Chromaticities(1)!));

    /// <summary>Whether an image in this colour space can be converted: not sRGB already, not HDR, and understood.</summary>
    public static bool CanConvert(ColorSpace colorSpace) => !colorSpace.IsSrgb && Model(colorSpace) is not null;

    /// <summary>
    /// The image in sRGB, a new image in the same pixel format; or null when <paramref name="onlyIfInside"/> is set and
    /// some pixel lies outside sRGB, or when the colour space can't be converted.
    /// </summary>
    public static Image? Convert(Image image, bool onlyIfInside)
    {
        IccProfileReader.Model? model = Model(image.ColorSpace);
        bool grey = image.PixelFormat is PixelFormat.L8 or PixelFormat.L16;
        if (model is null || (model.IsGrey && !grey))
        {
            // Not understood, or a grey profile on a colour image, which doesn't say what its colours mean.
            return null;
        }

        bool sixteen = image.PixelFormat is PixelFormat.L16 or PixelFormat.Rgb48 or PixelFormat.Rgba64;
        int max = sixteen ? 65535 : 255;
        Quantizer encode = TransferFunction.For(ColorSpace.Srgb).Encoder(sixteen);
        var result = new Image(image.Width, image.Height, image.PixelFormat) { ColorSpace = ColorSpace.Srgb, Metadata = image.Metadata };
        try
        {
            bool inside = grey
                ? ConvertGrey(image, result, model, sixteen, max, encode)
                : ConvertColour(image, result, model, sixteen, max, encode, onlyIfInside);
            if (onlyIfInside && !inside)
            {
                result.Dispose();
                return null;
            }

            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    // The model of a colour space: CICP first, then an ICC profile, then PNG gamma and chromaticities, as the PNG
    // specification ranks them. Null for sRGB, HDR and what isn't understood.
    internal static IccProfileReader.Model? Model(ColorSpace colorSpace)
    {
        if (colorSpace.IsSrgb || colorSpace.IsHdr)
        {
            return null;
        }

        if (colorSpace.CicpPrimaries is int primaries && colorSpace.CicpTransfer is int transfer)
        {
            double[]? chromaticities = ColourMath.Chromaticities(primaries);
            if (chromaticities is null || transfer is not (1 or 4 or 5 or 6 or 8 or 13 or 14 or 15))
            {
                return null;
            }

            Func<double, double> curve = TransferFunction.For(colorSpace).ToLinear;
            return new IccProfileReader.Model(ColourMath.RgbToPcs(chromaticities), [curve, curve, curve]);
        }

        if (!colorSpace.IccProfile.IsEmpty)
        {
            return IccProfileReader.Read(colorSpace.IccProfile.Span);
        }

        if (colorSpace.PngGamma > 0 || !colorSpace.PngChromaticities.IsEmpty)
        {
            double[] chromaticities = colorSpace.PngChromaticities.IsEmpty ? ColourMath.Chromaticities(1)! : ColourMath.ToDoubles(colorSpace.PngChromaticities);
            Func<double, double> curve = TransferFunction.For(colorSpace).ToLinear;
            return new IccProfileReader.Model(ColourMath.RgbToPcs(chromaticities), [curve, curve, curve]);
        }

        return null;
    }

    private static bool ConvertGrey(Image image, Image result, IccProfileReader.Model model, bool sixteen, int max, Quantizer encode)
    {
        // A grey image under an RGB profile uses its green curve, the one nearest luminance.
        Func<double, double> curve = model.IsGrey ? model.Curves[0] : model.Curves[1];
        ushort[] table = new ushort[max + 1];
        for (int v = 0; v <= max; v++)
        {
            table[v] = (ushort)encode.Encode((float)Math.Clamp(Clean(curve((double)v / max)), 0, 1));
        }

        int width = image.Width;
        Rows(image.Height, image.Width, stopOnFalse: false, y =>
        {
            if (sixteen)
            {
                ReadOnlySpan<ushort> source = MemoryMarshal.Cast<byte, ushort>(image.GetRowBytes(y));
                Span<ushort> target = MemoryMarshal.Cast<byte, ushort>(result.GetRowBytes(y));
                for (int x = 0; x < width; x++)
                {
                    target[x] = table[source[x]];
                }
            }
            else
            {
                ReadOnlySpan<byte> source = image.GetRowBytes(y);
                Span<byte> target = result.GetRowBytes(y);
                for (int x = 0; x < width; x++)
                {
                    target[x] = (byte)table[source[x]];
                }
            }

            return true;
        });
        return true;
    }

    private static bool ConvertColour(Image image, Image result, IccProfileReader.Model model, bool sixteen, int max, Quantizer encode, bool stopOutside)
    {
        float[][] linear = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            linear[c] = new float[max + 1];
            for (int v = 0; v <= max; v++)
            {
                linear[c][v] = (float)Clean(model.Curves[c]((double)v / max));
            }
        }

        double[] m = ColourMath.Multiply(FromPcs, model.ToPcs!);
        float m0 = (float)m[0], m1 = (float)m[1], m2 = (float)m[2], m3 = (float)m[3], m4 = (float)m[4], m5 = (float)m[5], m6 = (float)m[6], m7 = (float)m[7], m8 = (float)m[8];
        (int channels, int red, int blue, int alpha) = image.PixelFormat switch
        {
            PixelFormat.Rgb24 or PixelFormat.Rgb48 => (3, 0, 2, -1),
            PixelFormat.Bgra32 => (4, 2, 0, 3),
            _ => (4, 0, 2, 3),
        };
        int width = image.Width;
        var matrix = new Matrix(m0, m1, m2, m3, m4, m5, m6, m7, m8);
        var layout = new Layout(channels, red, blue, alpha);
        var tables = new Tables(linear[0], linear[1], linear[2], encode);
        return Rows(image.Height, width, stopOutside, y => sixteen
            ? Row16(MemoryMarshal.Cast<byte, ushort>(image.GetRowBytes(y)), MemoryMarshal.Cast<byte, ushort>(result.GetRowBytes(y)), width, layout, tables, matrix, stopOutside)
            : Row8(image.GetRowBytes(y), result.GetRowBytes(y), width, layout, tables, matrix, stopOutside));
    }

    // One row of 8-bit pixels. Everything the loop reads is a parameter or a local, so the JIT keeps it in registers.
    private static bool Row8(ReadOnlySpan<byte> source, Span<byte> target, int width, Layout layout, Tables tables, Matrix m, bool stopOutside)
    {
        (int channels, int red, int blue, int alpha) = (layout.Channels, layout.Red, layout.Blue, layout.Alpha);
        float[] lr = tables.Red, lg = tables.Green, lb = tables.Blue;
        Quantizer encode = tables.Encode;
        bool inside = true;
        for (int at = 0; at < width * channels; at += channels)
        {
            float fr = lr[source[at + red]], fg = lg[source[at + 1]], fb = lb[source[at + blue]];
            float sr = (m.M0 * fr) + (m.M1 * fg) + (m.M2 * fb);
            float sg = (m.M3 * fr) + (m.M4 * fg) + (m.M5 * fb);
            float sb = (m.M6 * fr) + (m.M7 * fg) + (m.M8 * fb);
            if (sr < Low || sg < Low || sb < Low || sr > High || sg > High || sb > High)
            {
                inside = false;
                if (stopOutside)
                {
                    return false;
                }
            }

            target[at + red] = (byte)encode.Encode(sr);
            target[at + 1] = (byte)encode.Encode(sg);
            target[at + blue] = (byte)encode.Encode(sb);
            if (alpha >= 0)
            {
                target[at + alpha] = source[at + alpha];
            }
        }

        return inside;
    }

    private static bool Row16(ReadOnlySpan<ushort> source, Span<ushort> target, int width, Layout layout, Tables tables, Matrix m, bool stopOutside)
    {
        (int channels, int red, int blue, int alpha) = (layout.Channels, layout.Red, layout.Blue, layout.Alpha);
        float[] lr = tables.Red, lg = tables.Green, lb = tables.Blue;
        Quantizer encode = tables.Encode;
        bool inside = true;
        for (int at = 0; at < width * channels; at += channels)
        {
            float fr = lr[source[at + red]], fg = lg[source[at + 1]], fb = lb[source[at + blue]];
            float sr = (m.M0 * fr) + (m.M1 * fg) + (m.M2 * fb);
            float sg = (m.M3 * fr) + (m.M4 * fg) + (m.M5 * fb);
            float sb = (m.M6 * fr) + (m.M7 * fg) + (m.M8 * fb);
            if (sr < Low || sg < Low || sb < Low || sr > High || sg > High || sb > High)
            {
                inside = false;
                if (stopOutside)
                {
                    return false;
                }
            }

            target[at + red] = (ushort)encode.Encode(sr);
            target[at + 1] = (ushort)encode.Encode(sg);
            target[at + blue] = (ushort)encode.Encode(sb);
            if (alpha >= 0)
            {
                target[at + alpha] = source[at + alpha];
            }
        }

        return inside;
    }

    private readonly record struct Matrix(float M0, float M1, float M2, float M3, float M4, float M5, float M6, float M7, float M8);

    private readonly record struct Layout(int Channels, int Red, int Blue, int Alpha);

    private readonly record struct Tables(float[] Red, float[] Green, float[] Blue, Quantizer Encode);

    // Runs rows in bands across cores; true when every row's body returned true. With stopOnFalse, a false stops
    // the other bands early.
    private static bool Rows(int height, int width, bool stopOnFalse, Func<int, bool> body)
    {
        int bands = Parallelism.Bands((long)width * height, height);
        int failed = 0;
        Parallelism.For(bands, band =>
        {
            int from = (int)((long)height * band / bands);
            int to = (int)((long)height * (band + 1) / bands);
            for (int y = from; y < to && !(stopOnFalse && System.Threading.Volatile.Read(ref failed) != 0); y++)
            {
                if (!body(y))
                {
                    System.Threading.Volatile.Write(ref failed, 1);
                }
            }
        });
        return failed == 0;
    }

    private static double Clean(double value) => double.IsNaN(value) ? 0 : value;
}
