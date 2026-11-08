using System;
using System.Runtime.InteropServices;
using Zorg.Imaging.Metadata;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Codecs.Jpeg;

/// <summary>
/// Turns a four-component JPEG's samples into sRGB: through the file's CMYK profile when it has one, else with the
/// usual device conversion R = (1 - C)(1 - K), G = (1 - M)(1 - K), B = (1 - Y)(1 - K).
/// </summary>
/// <remarks>
/// <para>
/// How the samples hold ink follows the Adobe marker, as libjpeg, browsers and ImageMagick read it: with an Adobe
/// marker the values are inverted (255 is no ink), as Photoshop writes them; transform 2 means YCCK, where the first
/// three components are YCbCr of the inverted C, M and Y; without the marker the values are ink as they are.
/// </para>
/// <para>
/// There is no CMYK pixel format, so the result is always sRGB: converted by the profile's own perceptual table, which
/// is what the profile says the colours are, or by the device formula when there is no profile to say.
/// </para>
/// </remarks>
internal sealed class JpegCmyk
{
    private readonly Storage _storage;
    private readonly IccLookup? _profile;
    private readonly Quantizer _encode = TransferFunction.For(ColorSpace.Srgb).Encoder(sixteen: false);

    private JpegCmyk(Storage storage, IccLookup? profile)
    {
        _storage = storage;
        _profile = profile;
    }

    /// <summary>How the four components hold the inks.</summary>
    public enum Storage
    {
        /// <summary>C, M, Y, K, 255 full ink: no Adobe marker.</summary>
        Plain,

        /// <summary>C, M, Y, K, 255 no ink: Adobe marker, transform 0.</summary>
        Inverted,

        /// <summary>YCbCr of the inverted C, M, Y, then the inverted K: Adobe marker, transform 2.</summary>
        Ycck,
    }

    /// <summary>The conversion for a file's Adobe transform (-1 when it has no Adobe marker) and its ICC profile.</summary>
    public static JpegCmyk For(int adobeTransform, ReadOnlySpan<byte> profile)
    {
        Storage storage = adobeTransform switch
        {
            < 0 => Storage.Plain,
            2 => Storage.Ycck,
            _ => Storage.Inverted,
        };

        IccLookup? lookup = null;
        if (!profile.IsEmpty && IccProfileReader.Read(profile) is { LutChannels: 4, Lut: { } lut })
        {
            lookup = IccLookup.Build(lut, ColourMath.Invert(ColourMath.RgbToPcs(ColourMath.Chromaticities(1)!)));
        }

        return new JpegCmyk(storage, lookup);
    }

    /// <summary>Whether a profile does the conversion (otherwise the device formula).</summary>
    public bool HasProfile => _profile is not null;

    /// <summary>Writes one row: four planes' samples in, pixels in <paramref name="format"/> out.</summary>
    public void WriteRow(byte[][] rows, Span<byte> output, PixelFormat format, int width)
    {
        Span<float> device = stackalloc float[4];
        for (int x = 0; x < width; x++)
        {
            (int c, int m, int y, int k) = Inks(rows[0][x], rows[1][x], rows[2][x], rows[3][x]);
            int r, g, b;
            if (_profile is { } profile)
            {
                device[0] = c / 255f;
                device[1] = m / 255f;
                device[2] = y / 255f;
                device[3] = k / 255f;
                (float lr, float lg, float lb) = profile.Lookup(device);
                (r, g, b) = (_encode.Encode(lr), _encode.Encode(lg), _encode.Encode(lb));
            }
            else
            {
                (r, g, b) = Device(c, m, y, k);
            }

            Put(output, format, x, (byte)r, (byte)g, (byte)b);
        }
    }

    /// <summary>The inks, 0 (none) to 255 (full), from one pixel's four samples.</summary>
    public (int C, int M, int Y, int K) Inks(int s0, int s1, int s2, int s3)
    {
        switch (_storage)
        {
            case Storage.Plain:
                return (s0, s1, s2, s3);
            case Storage.Inverted:
                return (255 - s0, 255 - s1, 255 - s2, 255 - s3);
            default:
                // YCbCr to RGB (JFIF), which here gives the inverted inks' complements, so the ink is the RGB value.
                int cb = s1 - 128;
                int cr = s2 - 128;
                int r = Math.Clamp(s0 + (((91881 * cr) + 32768) >> 16), 0, 255);
                int g = Math.Clamp(s0 + (((-22554 * cb) - (46802 * cr) + 32768) >> 16), 0, 255);
                int b = Math.Clamp(s0 + (((116130 * cb) + 32768) >> 16), 0, 255);
                return (r, g, b, 255 - s3);
        }
    }

    /// <summary>The device conversion, rounded: R = (255 - C)(255 - K) / 255, and the same for G and B.</summary>
    public static (int R, int G, int B) Device(int c, int m, int y, int k)
    {
        int paper = 255 - k;
        return ((((255 - c) * paper) + 127) / 255, (((255 - m) * paper) + 127) / 255, (((255 - y) * paper) + 127) / 255);
    }

    private static void Put(Span<byte> output, PixelFormat format, int x, byte r, byte g, byte b)
    {
        switch (format)
        {
            case PixelFormat.Rgb24:
                output[3 * x] = r;
                output[(3 * x) + 1] = g;
                output[(3 * x) + 2] = b;
                break;
            case PixelFormat.Bgra32:
                output[4 * x] = b;
                output[(4 * x) + 1] = g;
                output[(4 * x) + 2] = r;
                output[(4 * x) + 3] = 255;
                break;
            case PixelFormat.Rgba64:
                Span<ushort> wide = MemoryMarshal.Cast<byte, ushort>(output);
                wide[4 * x] = (ushort)(r * 257);
                wide[(4 * x) + 1] = (ushort)(g * 257);
                wide[(4 * x) + 2] = (ushort)(b * 257);
                wide[(4 * x) + 3] = 65535;
                break;
            default:
                output[4 * x] = r;
                output[(4 * x) + 1] = g;
                output[(4 * x) + 2] = b;
                output[(4 * x) + 3] = 255;
                break;
        }
    }
}
