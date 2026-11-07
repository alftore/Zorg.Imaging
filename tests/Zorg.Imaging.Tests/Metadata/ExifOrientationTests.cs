using System;
using System.Buffers.Binary;
using Xunit;
using Zorg.Imaging.Metadata;

namespace Zorg.Imaging.Tests.Metadata;

public class ExifOrientationTests
{
    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(true, 4)]
    [InlineData(true, 5)]
    [InlineData(true, 6)]
    [InlineData(true, 7)]
    [InlineData(true, 8)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(false, 4)]
    [InlineData(false, 5)]
    [InlineData(false, 6)]
    [InlineData(false, 7)]
    [InlineData(false, 8)]
    public void Values_1_to_8_are_returned_in_either_byte_order(bool littleEndian, int value) =>
        Assert.Equal(value, ExifOrientation.Read(Block.Build(littleEndian, (ushort)value)));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_leading_Exif_prefix_is_accepted(bool littleEndian) =>
        Assert.Equal(6, ExifOrientation.Read(Block.Build(littleEndian, 6, exifPrefix: true)));

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(0xFFFF)]
    public void Values_outside_1_to_8_read_as_1(int value) =>
        Assert.Equal(1, ExifOrientation.Read(Block.Build(true, (ushort)value)));

    [Fact]
    public void A_type_other_than_SHORT_reads_as_1() =>
        Assert.Equal(1, ExifOrientation.Read(Block.Build(true, 6, type: 4)));

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    public void A_count_other_than_1_reads_as_1(uint count) =>
        Assert.Equal(1, ExifOrientation.Read(Block.Build(true, 6, count: count)));

    [Theory]
    [InlineData(0u)]
    [InlineData(7u)]
    [InlineData(1000u)]
    [InlineData(uint.MaxValue)]
    public void An_IFD_offset_out_of_range_reads_as_1(uint offset) =>
        Assert.Equal(1, ExifOrientation.Read(Block.Build(true, 6, ifdOffset: offset)));

    [Fact]
    public void An_entry_count_running_past_the_end_reads_as_1()
    {
        // Claims 500 entries but only one (not the orientation) is present.
        byte[] block = Block.Build(true, 6, tag: 0x0100, entryCount: 500);
        Assert.Equal(1, ExifOrientation.Read(block));
    }

    [Fact]
    public void The_orientation_entry_may_follow_other_entries()
    {
        byte[] block = Block.Build(false, 8, entryCount: 3, leadingEntries: 2);
        Assert.Equal(8, ExifOrientation.Read(block));
    }

    [Fact]
    public void Data_cut_off_inside_the_entry_reads_as_1()
    {
        byte[] block = Block.Build(true, 6);
        for (int length = 0; length < 10 + 12; length++)
        {
            Assert.Equal(1, ExifOrientation.Read(block.AsSpan(0, length)));
        }
    }

    [Fact]
    public void Empty_data_reads_as_1() => Assert.Equal(1, ExifOrientation.Read([]));

    [Fact]
    public void An_unknown_byte_order_reads_as_1()
    {
        byte[] block = Block.Build(true, 6);
        block[0] = (byte)'X';
        Assert.Equal(1, ExifOrientation.Read(block));
    }

    [Fact]
    public void A_block_without_the_orientation_tag_reads_as_1() =>
        Assert.Equal(1, ExifOrientation.Read(Block.Build(true, 6, tag: 0x011A)));

    private static class Block
    {
        public static byte[] Build(
            bool littleEndian,
            ushort value,
            bool exifPrefix = false,
            ushort tag = 0x0112,
            ushort type = 3,
            uint count = 1,
            uint ifdOffset = 8,
            ushort? entryCount = null,
            int leadingEntries = 0)
        {
            int entries = 1 + leadingEntries;
            byte[] tiff = new byte[8 + 2 + (entries * 12) + 4];
            tiff[0] = tiff[1] = littleEndian ? (byte)'I' : (byte)'M';
            if (littleEndian)
            {
                tiff[2] = 42;
            }
            else
            {
                tiff[3] = 42;
            }

            WriteUInt32(tiff.AsSpan(4), ifdOffset, littleEndian);
            WriteUInt16(tiff.AsSpan(8), entryCount ?? (ushort)entries, littleEndian);

            // Leading entries use another tag (ImageWidth, 0x0100).
            for (int i = 0; i < leadingEntries; i++)
            {
                Span<byte> lead = tiff.AsSpan(10 + (i * 12), 12);
                WriteUInt16(lead, 0x0100, littleEndian);
                WriteUInt16(lead[2..], 3, littleEndian);
                WriteUInt32(lead[4..], 1, littleEndian);
                WriteUInt16(lead[8..], 100, littleEndian);
            }

            Span<byte> field = tiff.AsSpan(10 + (leadingEntries * 12), 12);
            WriteUInt16(field, tag, littleEndian);
            WriteUInt16(field[2..], type, littleEndian);
            WriteUInt32(field[4..], count, littleEndian);
            WriteUInt16(field[8..], value, littleEndian);

            if (!exifPrefix)
            {
                return tiff;
            }

            byte[] withPrefix = new byte[6 + tiff.Length];
            "Exif\0\0"u8.CopyTo(withPrefix);
            tiff.CopyTo(withPrefix, 6);
            return withPrefix;
        }

        private static void WriteUInt16(Span<byte> target, ushort value, bool littleEndian)
        {
            if (littleEndian)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(target, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(target, value);
            }
        }

        private static void WriteUInt32(Span<byte> target, uint value, bool littleEndian)
        {
            if (littleEndian)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(target, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(target, value);
            }
        }
    }
}
