using Zorg.Imaging.Formats;
using Xunit;

namespace Zorg.Imaging.Tests.Formats;

public sealed class CodecTableTests
{
    [Fact]
    public void Every_supported_format_has_a_codec_and_no_other_does()
    {
        foreach (ImageFormat format in ImageFormats.Known)
        {
            Assert.Equal(format.IsSupported, CodecTable.For(format) is not null);
        }

        Assert.Null(CodecTable.For(null));
    }

    [Theory]
    [InlineData(".png", "PNG")]
    [InlineData(".JPG", "JPEG")]
    [InlineData(".jfif", "JPEG")]
    [InlineData(".webp", "WebP")]
    [InlineData(".gif", "GIF")]
    public void Extensions_of_formats_with_an_encoder_are_found(string extension, string name)
    {
        Assert.Equal(name, CodecTable.EncodableFormatOf(extension)?.Name);
    }

    [Theory]
    [InlineData(".tif")]
    [InlineData(".bmp")]
    [InlineData(".avif")]
    [InlineData(".txt")]
    public void Extensions_of_formats_without_an_encoder_are_not(string extension)
    {
        Assert.Null(CodecTable.EncodableFormatOf(extension));
    }
}
