using System;
using Xunit;

namespace Zorg.Imaging.OsCodecs.Tests;

/// <summary>The codecs through Zorg.Imaging's own calls, on whatever system the tests run on.</summary>
public sealed class SystemCodecsTests
{
    [Fact]
    public void Both_codecs_are_offered_and_use_native_code()
    {
        Assert.Equal(2, SystemCodecs.All.Count);
        Assert.Same(ImageFormat.Heif, SystemCodecs.Heif.Format);
        Assert.Same(ImageFormat.Avif, SystemCodecs.Avif.Format);
        Assert.True(SystemCodecs.Heif.UsesNativeCode);
    }

    [Fact]
    public void Availability_matches_the_system()
    {
        CodecAvailability heic = SystemCodecs.Heif.Availability;
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            Assert.False(heic.IsAvailable);
            Assert.Contains("Windows and macOS", heic.Reason, StringComparison.Ordinal);
        }
        else if (!heic.IsAvailable)
        {
            Assert.False(string.IsNullOrWhiteSpace(heic.Reason));
        }
    }

    [Fact]
    public void Identify_reads_the_boxes_when_the_codec_is_available()
    {
        byte[] file = HeifFile.Heic(4032, 3024).Property(1, HeifFile.Irot(3)).Build();
        IdentifyResult result = Image.Identify(file, new IdentifyOptions { Codecs = SystemCodecs.All });
        if (SystemCodecs.Heif.Availability.IsAvailable)
        {
            Assert.Equal(IdentifyStatus.Ok, result.Status);
            Assert.Equal((3024, 4032), (result.Info!.DisplayWidth, result.Info.DisplayHeight));
        }
        else
        {
            Assert.Equal(IdentifyStatus.UnsupportedFormat, result.Status);
            Assert.Equal(SystemCodecs.Heif.Availability.Reason, result.Message);
        }
    }

    [Fact]
    public void Limits_are_checked_before_the_system_decoder_sees_the_file()
    {
        byte[] file = HeifFile.Heic(20_000, 20_000).Build();
        var options = new DecodeOptions { Codecs = SystemCodecs.All, Limits = new ImageLimits { MaxPixels = 1_000_000 } };
        if (SystemCodecs.Heif.Availability.IsAvailable)
        {
            Assert.Throws<ImageLimitException>(() => Image.Decode(file, options));
        }
        else
        {
            Assert.Throws<UnsupportedImageFormatException>(() => Image.Decode(file, options));
        }
    }

    [Fact]
    public void A_file_with_no_coded_pixels_fails_with_an_image_exception()
    {
        // Real structure, no real HEVC data: whatever the system decoder says, it must come back as an ImageException.
        byte[] file = HeifFile.Heic(64, 48).Build();
        Assert.ThrowsAny<ImageException>(() => Image.Decode(file, new DecodeOptions { Codecs = SystemCodecs.All }));
        Assert.ThrowsAny<ImageException>(() => Image.Decode(HeifFile.Avif(64, 48).Build(), new DecodeOptions { Codecs = SystemCodecs.All }));
    }
}
