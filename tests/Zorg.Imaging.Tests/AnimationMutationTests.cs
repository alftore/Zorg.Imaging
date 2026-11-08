using System.Collections.Generic;
using Xunit;
using Zorg.Imaging.Tests.Codecs.Webp;
using static Zorg.Imaging.Tests.Codecs.DecoderFuzz;

namespace Zorg.Imaging.Tests;

/// <summary>
/// Fuzzing, first tier, for reading every frame of an animation (guarantee 1): damaged files may only throw our
/// exception types, whatever frame the damage is in.
/// </summary>
public class AnimationMutationTests
{
    public static TheoryData<string> Samples => new(SampleFiles().Keys);

    [Theory]
    [MemberData(nameof(Samples))]
    public void Every_truncation_throws_only_our_exceptions(string name) => Truncations(SampleFiles()[name], name, ReadAll);

    [Theory]
    [MemberData(nameof(Samples))]
    public void Edge_values_in_every_byte_throw_only_our_exceptions(string name) => EdgeBytes(SampleFiles()[name], name, ReadAll);

    [Theory]
    [MemberData(nameof(Samples))]
    public void Random_byte_damage_throws_only_our_exceptions(string name) => RandomDamage(SampleFiles()[name], name, 1000, ReadAll);

    private static void ReadAll(byte[] file)
    {
        using AnimationReader reader = AnimationReader.Open(file, new DecodeOptions { Limits = new ImageLimits { MaxPixels = 1 << 20, MaxFrames = 64 } });
        while (reader.ReadFrame() is { } frame)
        {
            frame.Dispose();
        }
    }

    private static Dictionary<string, byte[]> SampleFiles()
    {
        var frames = new List<(Image, int)>();
        for (int step = 0; step < 4; step++)
        {
            frames.Add((AnimationTests.Scene(40, 24, step, transparentEdge: step is 1 or 3), 50));
        }

        try
        {
            return new Dictionary<string, byte[]>
            {
                ["ours.gif"] = AnimationTests.Write(stream => AnimationWriter.CreateGif(stream, 2), frames),
                ["ours-lossless.webp"] = AnimationTests.Write(stream => AnimationWriter.CreateWebp(stream, 0, new WebpEncoderOptions { Lossless = true }), frames),
                ["ours-lossy.webp"] = AnimationTests.Write(stream => AnimationWriter.CreateWebp(stream, 0, new WebpEncoderOptions { Quality = 50 }), frames),
                ["libwebp-animated.webp"] = WebpDecoderTests.Sample("animated"),
            };
        }
        finally
        {
            frames.ForEach(f => f.Item1.Dispose());
        }
    }
}
