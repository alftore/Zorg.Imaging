using Xunit;
using Zorg.Imaging.Codecs.Png;

namespace Zorg.Imaging.Tests.Codecs.Png;

public class Crc32Tests
{
    // The standard check value for CRC-32/ISO-HDLC.
    [Fact]
    public void Check_value_matches_the_standard() => Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));

    // Every PNG ends with an empty IEND chunk whose CRC is AE 42 60 82.
    [Fact]
    public void Iend_chunk_matches_the_PNG_specification() => Assert.Equal(0xAE426082u, Crc32.Compute("IEND"u8));

    [Fact]
    public void Empty_input_is_zero() => Assert.Equal(0u, Crc32.Compute([]));

    [Fact]
    public void Append_in_parts_matches_one_call()
    {
        uint parts = Crc32.Append(Crc32.Compute("1234"u8), "56789"u8);
        Assert.Equal(Crc32.Compute("123456789"u8), parts);
    }
}
