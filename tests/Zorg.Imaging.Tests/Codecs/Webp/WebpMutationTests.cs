using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using static Zorg.Imaging.Tests.Codecs.DecoderFuzz;

namespace Zorg.Imaging.Tests.Codecs.Webp;

/// <summary>Fuzzing, first tier, for the WebP decoder: VP8, VP8L, ALPH and animation (guarantee 1).</summary>
public class WebpMutationTests
{
    public static TheoryData<string> Samples => new(SampleFiles().Keys);

    [Theory]
    [MemberData(nameof(Samples))]
    public void Every_truncation_throws_only_our_exceptions(string name) => Truncations(SampleFiles()[name], name, Decode);

    [Theory]
    [MemberData(nameof(Samples))]
    public void Edge_values_in_every_byte_throw_only_our_exceptions(string name) => EdgeBytes(SampleFiles()[name], name, Decode);

    [Theory]
    [MemberData(nameof(Samples))]
    public void Random_byte_damage_throws_only_our_exceptions(string name) => RandomDamage(SampleFiles()[name], name, 2000, Decode);

    [Theory]
    [MemberData(nameof(Samples))]
    public void Corrupted_chunk_sizes_throw_only_our_exceptions(string name)
    {
        byte[] webp = SampleFiles()[name];
        uint[] values = [0, 1, 2, 3, 9, 0x100, 0x7FFF_FFFF, 0x8000_0000, 0xFFFF_FFFE, 0xFFFF_FFFF];
        Fields(webp, name, Chunks(webp), values, (copy, at, value) => BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(at), value), Decode);

        // The 24-bit canvas and frame sizes in VP8X and ANMF.
        var sizes = new List<(int, string)>();
        foreach ((int at, string field) in Chunks(webp))
        {
            if (field is "VP8X size")
            {
                sizes.Add((at + 8, "VP8X width"));
                sizes.Add((at + 11, "VP8X height"));
            }
            else if (field is "ANMF size")
            {
                sizes.Add((at + 4, "ANMF x"));
                sizes.Add((at + 10, "ANMF width"));
                sizes.Add((at + 13, "ANMF height"));
            }
        }

        Fields(webp, name, sizes, [0, 1, 0x3FFF, 0x4000, 0xFF_FFFF], (copy, at, value) =>
        {
            copy[at] = (byte)value;
            copy[at + 1] = (byte)(value >> 8);
            copy[at + 2] = (byte)(value >> 16);
        }, Decode);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Damage_through_a_stream_throws_only_our_exceptions(string name) => await StreamDamage(SampleFiles()[name], name, 300);

    /// <summary>The offset of every chunk's size field, the RIFF header's included, and the frames' chunks inside ANMF.</summary>
    private static List<(int Offset, string Field)> Chunks(byte[] webp)
    {
        var fields = new List<(int, string)> { (4, "RIFF size") };
        Walk(webp, 12, webp.Length, fields);
        return fields;
    }

    private static void Walk(byte[] webp, int at, int end, List<(int, string)> fields)
    {
        while (at + 8 <= end)
        {
            string fourCc = Encoding.ASCII.GetString(webp, at, 4);
            int size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(at + 4)), int.MaxValue);
            fields.Add((at + 4, fourCc.TrimEnd() + " size"));
            if (fourCc == "ANMF" && at + 24 <= end)
            {
                Walk(webp, at + 24, Math.Min(end, at + 8 + size), fields);
            }

            at += 8 + size + (size & 1);
        }
    }

    private static Dictionary<string, byte[]> SampleFiles()
    {
        var files = new[] { "animated", "lossless-alpha", "lossless-logo", "lossless-palette12", "lossless-palette3", "lossless-z6", "lossy", "lossy-alpha", "near-lossless" }
            .ToDictionary(name => name, WebpDecoderTests.Sample);
        using Image graphic = WebpEncoderTests.Graphic(48, 32, 40, alpha: true);
        files["encoded-lossy-alpha"] = WebpEncoderTests.Save(graphic, new WebpEncoderOptions { Quality = 70 });
        files["encoded-lossless"] = WebpEncoderTests.Save(graphic, new WebpEncoderOptions { Lossless = true });
        return files;
    }
}
