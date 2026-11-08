using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static Zorg.Imaging.Tests.Codecs.DecoderFuzz;

namespace Zorg.Imaging.Tests.Codecs.Jpeg;

/// <summary>Fuzzing, first tier, for the JPEG decoder at full size and when shrinking on load (guarantee 1).</summary>
public class JpegMutationTests
{
    private static readonly DecodeOptions Shrunk = Small.WithShrinkTarget((width, height) => (Math.Max(width / 8, 1), Math.Max(height / 8, 1)));

    public static TheoryData<string> Samples => new(SampleFiles().Keys);

    [Theory]
    [MemberData(nameof(Samples))]
    public void Every_truncation_throws_only_our_exceptions(string name)
    {
        byte[] jpeg = SampleFiles()[name];
        Truncations(jpeg, name, Decode);
        Truncations(jpeg, name + " shrunk", DecodeShrunk);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Edge_values_in_every_byte_throw_only_our_exceptions(string name) => EdgeBytes(SampleFiles()[name], name, Decode);

    [Theory]
    [MemberData(nameof(Samples))]
    public void Random_byte_damage_throws_only_our_exceptions(string name)
    {
        byte[] jpeg = SampleFiles()[name];
        RandomDamage(jpeg, name, 2000, Decode);
        RandomDamage(jpeg, name + " shrunk", 1000, DecodeShrunk);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Corrupted_lengths_and_sizes_throw_only_our_exceptions(string name)
    {
        byte[] jpeg = SampleFiles()[name];
        var fields = new List<(int, string)>();
        foreach ((int offset, byte marker) in JpegTestFiles.Segments(jpeg))
        {
            fields.Add((offset + 2, $"FF{marker:X2} length"));
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                fields.Add((offset + 5, "frame height"));
                fields.Add((offset + 7, "frame width"));
            }
        }

        uint[] values = [0, 1, 2, 3, 4, 8, 0x100, 0x7FFF, 0x8000, 0xFFFF];
        Fields(jpeg, name, fields, values, (copy, at, value) =>
        {
            copy[at] = (byte)(value >> 8);
            copy[at + 1] = (byte)value;
        }, Decode);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Damage_through_a_stream_throws_only_our_exceptions(string name) => await StreamDamage(SampleFiles()[name], name, 300);

    [Fact]
    public void A_huffman_table_with_too_many_short_codes_is_corrupt_before_it_is_built()
    {
        // Found by the random damage: three 1-bit codes (only two fit) wrote past the end of the lookup table.
        byte[] counts = new byte[16];
        counts[0] = 3;
        Assert.Throws<ImageFormatException>(() => Zorg.Imaging.Codecs.Jpeg.JpegHuffman.Create(counts, [1, 2, 3]));
        counts[0] = 2;
        counts[1] = 1;
        Assert.Throws<ImageFormatException>(() => Zorg.Imaging.Codecs.Jpeg.JpegHuffman.Create(counts, [1, 2, 3]));
    }

    private static void DecodeShrunk(byte[] data)
    {
        using Image image = Image.Decode(data, Shrunk);
    }

    private static Dictionary<string, byte[]> SampleFiles()
    {
        var files = new[] { "grey", "progressive", "progressive-grey", "progressive-restart", "restart", "rgb", "s411", "s420", "s422", "s440", "s444", "ycck", "ycck-subsampled" }
            .ToDictionary(name => name, JpegTestFiles.Sample);
        using Image photo = JpegEncoderTests.Photo(40, 30);
        photo.ColorSpace = ColorSpace.DisplayP3;
        files["encoded-p3-icc"] = JpegEncoderTests.Save(photo, new JpegEncoderOptions { ColorSpaceHandling = ColorSpaceHandling.Keep, Quality = 90 });
        files["metadata-exif-iptc"] = Metadata.MetadataReadingTests.Rich();
        return files;
    }
}
