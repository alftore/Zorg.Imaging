using System;
using System.Numerics;

namespace Zorg.Imaging.Processing;

/// <summary>
/// The BlurHash encoding (blurha.sh, MIT): the image as a few 2-D cosine frequencies of its linear-light colours, the
/// average quantised to 8-bit sRGB and the others to 19 levels on a square-root scale, all in a base-83 string.
/// </summary>
internal static class BlurHash
{
    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz#$%*+,-.:;=?@[]^_{|}~";

    public static string Encode(Image image, int componentsX, int componentsY)
    {
        int width = image.Width;
        int height = image.Height;

        // The colours in linear light, by the sRGB curve, as the algorithm defines them.
        var linear = new Vector3[width * height];
        var row = new Vector4[width];
        for (int y = 0; y < height; y++)
        {
            FloatRows.Read(image, y, row);
            for (int x = 0; x < width; x++)
            {
                linear[(y * width) + x] = new Vector3(ToLinear(row[x].X), ToLinear(row[x].Y), ToLinear(row[x].Z));
            }
        }

        var factors = new Vector3[componentsX * componentsY];
        double[] cosX = new double[width];
        double[] cosY = new double[height];
        for (int j = 0; j < componentsY; j++)
        {
            for (int y = 0; y < height; y++)
            {
                cosY[y] = Math.Cos(Math.PI * j * y / height);
            }

            for (int i = 0; i < componentsX; i++)
            {
                for (int x = 0; x < width; x++)
                {
                    cosX[x] = Math.Cos(Math.PI * i * x / width);
                }

                double r = 0, g = 0, b = 0;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        double basis = cosX[x] * cosY[y];
                        Vector3 c = linear[(y * width) + x];
                        r += basis * c.X;
                        g += basis * c.Y;
                        b += basis * c.Z;
                    }
                }

                double scale = (i == 0 && j == 0 ? 1.0 : 2.0) / (width * height);
                factors[(j * componentsX) + i] = new Vector3((float)(r * scale), (float)(g * scale), (float)(b * scale));
            }
        }

        var hash = new System.Text.StringBuilder(6 + (2 * (factors.Length - 1)));
        Append(hash, (componentsX - 1) + ((componentsY - 1) * 9), 1);

        double maximum = 1;
        if (factors.Length > 1)
        {
            float largest = 0;
            for (int k = 1; k < factors.Length; k++)
            {
                Vector3 f = Vector3.Abs(factors[k]);
                largest = Math.Max(largest, Math.Max(f.X, Math.Max(f.Y, f.Z)));
            }

            int quantised = (int)Math.Max(0, Math.Min(82, Math.Floor((largest * 166) - 0.5)));
            maximum = (quantised + 1) / 166.0;
            Append(hash, quantised, 1);
        }
        else
        {
            Append(hash, 0, 1);
        }

        Vector3 dc = factors[0];
        Append(hash, (FromLinear(dc.X) << 16) | (FromLinear(dc.Y) << 8) | FromLinear(dc.Z), 4);
        for (int k = 1; k < factors.Length; k++)
        {
            Vector3 f = factors[k];
            int value = (Ac(f.X, maximum) * 19 * 19) + (Ac(f.Y, maximum) * 19) + Ac(f.Z, maximum);
            Append(hash, value, 2);
        }

        return hash.ToString();
    }

    // An AC value as one of 19 levels, on a square-root scale of its share of the maximum.
    private static int Ac(double value, double maximum)
    {
        double share = value / maximum;
        double curved = Math.CopySign(Math.Sqrt(Math.Abs(share)), share);
        return (int)Math.Max(0, Math.Min(18, Math.Floor((curved * 9) + 9.5)));
    }

    private static void Append(System.Text.StringBuilder hash, int value, int length)
    {
        for (int k = length - 1; k >= 0; k--)
        {
            int divisor = 1;
            for (int d = 0; d < k; d++)
            {
                divisor *= 83;
            }

            hash.Append(Digits[value / divisor % 83]);
        }
    }

    private static float ToLinear(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    private static int FromLinear(float v)
    {
        float c = Math.Clamp(v, 0, 1);
        float encoded = c <= 0.0031308f ? c * 12.92f : (1.055f * MathF.Pow(c, 1 / 2.4f)) - 0.055f;
        return (int)((encoded * 255) + 0.5f);
    }
}
