using System;
using Xunit;

namespace Zorg.Imaging.Tests;

[CollectionDefinition(nameof(DefaultCodecsTests), DisableParallelization = true)]
public sealed class DefaultCodecsRunAlone;

/// <summary>
/// <see cref="ImageDefaults.Codecs"/> is process-wide, so these run alone: while they do, an AVIF file decodes.
/// </summary>
[Collection(nameof(DefaultCodecsTests))]
public sealed class DefaultCodecsTests
{
    [Fact]
    public void The_default_codecs_can_be_set_until_first_used()
    {
        try
        {
            ImageDefaults.ResetCodecs();
            ImageDefaults.Codecs = ImageCodecs.Empty.Add(new FakeAvif());
            ImageDefaults.Codecs = ImageCodecs.Empty.Add(new FakeAvif());

            using Image image = Image.Decode(FakeAvif.File(5, 4));
            Assert.Equal(5, image.Width);
            Assert.Equal(IdentifyStatus.Ok, Image.Identify(FakeAvif.File(5, 4)).Status);

            Assert.Throws<InvalidOperationException>(() => ImageDefaults.Codecs = ImageCodecs.Empty);
            Assert.Throws<ArgumentNullException>(() => ImageDefaults.Codecs = null!);
        }
        finally
        {
            ImageDefaults.ResetCodecs();
        }
    }

    [Fact]
    public void Options_override_the_default()
    {
        try
        {
            ImageDefaults.ResetCodecs();
            ImageDefaults.Codecs = ImageCodecs.Empty.Add(new FakeAvif());
            Assert.Throws<UnsupportedImageFormatException>(
                () => Image.Decode(FakeAvif.File(5, 4), new DecodeOptions { Codecs = ImageCodecs.Empty }));
        }
        finally
        {
            ImageDefaults.ResetCodecs();
        }
    }
}
