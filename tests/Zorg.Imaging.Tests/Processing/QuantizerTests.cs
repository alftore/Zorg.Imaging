using System;
using Xunit;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Tests.Processing;

public class QuantizerTests
{
    public static TheoryData<string> Spaces() => ["sRGB", "Linear", "BT.709", "PQ", "HLG", "Gamma 1.8"];

    [Theory]
    [MemberData(nameof(Spaces))]
    public void Codes_are_the_exactly_rounded_encoded_values(string name)
    {
        TransferFunction transfer = TransferFunction.For(Space(name));
        foreach (bool sixteen in new[] { false, true })
        {
            int max = sixteen ? 65535 : 255;
            Quantizer quantizer = transfer.Encoder(sixteen);
            var random = new Random(max);
            for (int i = 0; i < 200_000; i++)
            {
                // Uniform in linear light, and uniform in encoded values (which is dense near black).
                float value = i % 2 == 0 ? random.NextSingle() : (float)transfer.ToLinear(random.NextDouble());
                double exact = transfer.FromLinear(value) * max;

                // Midpoints are floats, a few thousandths of a 16-bit code from exact: next to one, either code is right.
                if (Math.Abs(exact - Math.Floor(exact) - 0.5) < 0.01)
                {
                    continue;
                }

                Assert.Equal((int)Math.Round(exact), quantizer.Encode(value));
            }

            Assert.Equal(0, quantizer.Encode(0));
            Assert.Equal(0, quantizer.Encode(-1));
            Assert.Equal(0, quantizer.Encode(float.NaN));
            Assert.Equal(max, quantizer.Encode(1));
            Assert.Equal(max, quantizer.Encode(2));
        }
    }

    [Theory]
    [MemberData(nameof(Spaces))]
    public void Every_code_converts_to_linear_and_back_to_itself(string name)
    {
        TransferFunction transfer = TransferFunction.For(Space(name));
        foreach (bool sixteen in new[] { false, true })
        {
            float[] linear = transfer.LinearTable(sixteen);
            Quantizer quantizer = transfer.Encoder(sixteen);
            for (int code = 0; code < linear.Length; code++)
            {
                Assert.Equal(code, quantizer.Encode(linear[code]));
            }
        }
    }

    private static ColorSpace Space(string name) => name switch
    {
        "Linear" => ColorSpace.LinearSrgb,
        "BT.709" => ColorSpace.FromCicp(1, 1),
        "PQ" => ColorSpace.Rec2020Pq,
        "HLG" => ColorSpace.Rec2020Hlg,
        "Gamma 1.8" => ColorSpace.FromPngGamma(55556, null),
        _ => ColorSpace.Srgb,
    };
}
