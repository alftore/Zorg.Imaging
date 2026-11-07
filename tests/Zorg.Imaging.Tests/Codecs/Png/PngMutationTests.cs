using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;
using static Zorg.Imaging.Tests.Codecs.Png.PngTestFiles;

namespace Zorg.Imaging.Tests.Codecs.Png;

/// <summary>
/// Fuzzing, first tier: truncations, bit flips and corrupted length fields over a few files. Whatever the damage,
/// decoding either succeeds or throws an <see cref="ImageException"/>, and never takes long (guarantee 1).
/// </summary>
public class PngMutationTests
{
    private static readonly DecodeOptions Small = new() { Limits = new ImageLimits { MaxPixels = 1_000_000 } };

    public static TheoryData<string> Samples => new(SampleFiles().Keys);

    [Theory]
    [MemberData(nameof(Samples))]
    public void Every_truncation_throws_only_our_exceptions(string name)
    {
        byte[] png = SampleFiles()[name];
        // Every length through the headers and well into the data; past that, every 97th.
        for (int length = 0; length < png.Length; length += length < 4096 ? 1 : 97)
        {
            AssertSafe(png[..length], $"{name} cut to {length}");
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Random_byte_damage_throws_only_our_exceptions(string name)
    {
        byte[] png = SampleFiles()[name];
        var random = new Random(name.Length);
        for (int round = 0; round < 3000; round++)
        {
            byte[] copy = (byte[])png.Clone();
            int flips = 1 + random.Next(4);
            for (int f = 0; f < flips; f++)
            {
                int at = random.Next(copy.Length);
                copy[at] = random.Next(3) == 0 ? (byte)random.Next(256) : (byte)(copy[at] ^ (1 << random.Next(8)));
            }

            AssertSafe(copy, $"{name} round {round}");
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Corrupted_length_fields_throw_only_our_exceptions(string name)
    {
        byte[] png = SampleFiles()[name];
        uint[] lengths = [0, 1, 12, 0x7FFF_FFFF, 0x8000_0000, 0xFFFF_FFFF, 0x0010_0000];
        foreach ((int offset, string type) in Chunks(png))
        {
            foreach (uint length in lengths)
            {
                byte[] copy = (byte[])png.Clone();
                BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(offset), length);
                AssertSafe(copy, $"{name} {type} length {length}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Damage_through_a_stream_throws_only_our_exceptions(string name)
    {
        byte[] png = SampleFiles()[name];
        var random = new Random(7);
        for (int round = 0; round < 300; round++)
        {
            byte[] copy = (byte[])png.Clone();
            copy[random.Next(copy.Length)] ^= (byte)(1 << random.Next(8));
            byte[] cut = copy[..random.Next(copy.Length + 1)];
            Exception? error = await Record.ExceptionAsync(async () =>
            {
                using Image image = await Image.DecodeAsync(new ForwardOnlyStream(cut), Small, TestContext.Current.CancellationToken);
            });
            Assert.True(error is null or ImageException, $"{name} round {round}: {error}");
        }
    }

    private static void AssertSafe(byte[] data, string what)
    {
        long started = Stopwatch.GetTimestamp();
        Exception? error = Record.Exception(() =>
        {
            using Image image = Image.Decode(data, Small);
        });
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

        Assert.True(error is null or ImageException, $"{what}: {error}");
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"{what} took {elapsed}");
    }

    private static Dictionary<string, byte[]> SampleFiles()
    {
        byte[] palette = PngDecoderTests.RandomPalette(16, seed: 1);
        using Image encoded = PngEncoderTests.Smooth(160, 120, PixelFormat.Rgba32, seed: 1);
        return new Dictionary<string, byte[]>
        {
            ["palette-trns-adam7"] = Build(17, 13, 4, 3, RandomSamples(17, 13, 4, 3, 1, 16), true, ("PLTE", palette), ("tRNS", [0, 50, 100]), ("eXIf", ExifWithOrientation(6))),
            ["rgba16"] = Build(9, 7, 16, 6, RandomSamples(9, 7, 16, 6, 2)),
            ["grey1-adam7"] = Build(21, 5, 1, 0, RandomSamples(21, 5, 1, 0, 3), true),
            ["encoder-multi-idat"] = PngEncoderTests.Save(encoded, new PngEncoderOptions { CompressionLevel = 0 }),
        };
    }
}
