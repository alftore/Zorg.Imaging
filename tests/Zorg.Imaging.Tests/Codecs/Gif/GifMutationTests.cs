using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Formats;
using Zorg.Imaging.Codecs.Gif;
using Zorg.Imaging.Tests.Codecs.Jpeg;
using static Zorg.Imaging.Tests.Codecs.DecoderFuzz;
using static Zorg.Imaging.Tests.Codecs.Gif.GifTestFiles;

namespace Zorg.Imaging.Tests.Codecs.Gif;

/// <summary>
/// Fuzzing, first tier, for the GIF decoder: the first frame through <see cref="Image.Decode(byte[], DecodeOptions?)"/>
/// and every frame composited by <see cref="GifReader"/>, so disposal and transparency see the damage too (guarantee 1).
/// A sub-block length is a single byte, so setting every byte to edge values covers the length fields.
/// </summary>
public class GifMutationTests
{
    public static TheoryData<string> Samples => new(SampleFiles().Keys);

    [Theory]
    [MemberData(nameof(Samples))]
    public void Every_truncation_throws_only_our_exceptions(string name)
    {
        byte[] gif = SampleFiles()[name];
        Truncations(gif, name, Decode);
        Truncations(gif, name + " all frames", DecodeAllFrames);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Edge_values_in_every_byte_throw_only_our_exceptions(string name)
    {
        byte[] gif = SampleFiles()[name];
        EdgeBytes(gif, name, Decode);
        EdgeBytes(gif, name + " all frames", DecodeAllFrames);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Random_byte_damage_throws_only_our_exceptions(string name)
    {
        byte[] gif = SampleFiles()[name];
        RandomDamage(gif, name, 2000, Decode);
        RandomDamage(gif, name + " all frames", 1000, DecodeAllFrames);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Corrupted_sizes_throw_only_our_exceptions(string name)
    {
        byte[] gif = SampleFiles()[name];
        var fields = new List<(int, string)> { (6, "screen width"), (8, "screen height") };
        for (int at = 13; at + 9 < gif.Length; at++)
        {
            // Image descriptors whose size matches the screen's or is inside it: good enough to find the frames here.
            if (gif[at] == 0x2C && gif[at + 5] + (gif[at + 6] << 8) is > 0 and <= 64)
            {
                fields.Add((at + 1, $"frame {at} left"));
                fields.Add((at + 5, $"frame {at} width"));
                fields.Add((at + 7, $"frame {at} height"));
            }
        }

        uint[] values = [0, 1, 0x7FFF, 0x8000, 0xFFFF];
        Fields(gif, name, fields, values, (copy, at, value) =>
        {
            copy[at] = (byte)value;
            copy[at + 1] = (byte)(value >> 8);
        }, data =>
        {
            Decode(data);
            DecodeAllFrames(data);
        });
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Damage_through_a_stream_throws_only_our_exceptions(string name) => await StreamDamage(SampleFiles()[name], name, 300);

    private static void DecodeAllFrames(byte[] data)
    {
        GifReader reader = GifReader.OpenAsync(DataReader.FromMemory(data), Small.Limits, TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult();
        while (reader.NextFrameAsync(TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult())
        {
        }
    }

    private static Dictionary<string, byte[]> SampleFiles()
    {
        byte[] four = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255];
        var random = new Random(3);
        byte[] Indices(int count, int colours)
        {
            byte[] indices = new byte[count];
            for (int i = 0; i < count; i++)
            {
                indices[i] = (byte)random.Next(colours);
            }

            return indices;
        }

        using Image photo = JpegEncoderTests.Photo(40, 30);
        return new Dictionary<string, byte[]>
        {
            ["animated-disposal"] = File(
                Screen(8, 6, four),
                Control(disposal: 1, delay: 5),
                Frame(0, 0, 8, 6, Indices(48, 4), codeSize: 2),
                Control(disposal: 2, transparent: 1),
                Frame(2, 1, 4, 3, Indices(12, 4), codeSize: 2),
                Control(disposal: 3),
                Frame(1, 1, 6, 4, Indices(24, 4), interlaced: true, codeSize: 2),
                Frame(0, 0, 3, 2, Indices(6, 4), palette: four, codeSize: 2),
                Trailer),
            ["interlaced-local-palette"] = File(
                Screen(16, 12),
                Frame(0, 0, 16, 12, Indices(192, 4), palette: four, interlaced: true, codeSize: 2),
                Trailer),
            ["encoded-quantised"] = GifEncoderTests.Save(photo),
        };
    }
}
