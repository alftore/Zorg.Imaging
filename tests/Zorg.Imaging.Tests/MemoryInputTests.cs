using System;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Tests.Codecs.Png;

namespace Zorg.Imaging.Tests;

/// <summary>The <see cref="ReadOnlyMemory{T}"/> inputs read a file inside a larger buffer as the array inputs read it alone.</summary>
public class MemoryInputTests
{
    private static (byte[] File, ReadOnlyMemory<byte> Slice) Embedded()
    {
        using Image source = PngEncoderTests.Smooth(40, 30, PixelFormat.Rgba32, seed: 9);
        byte[] file = PngEncoderTests.Save(source);
        byte[] buffer = new byte[file.Length + 20];
        buffer.AsSpan().Fill(0xA5);
        file.CopyTo(buffer, 7);
        return (file, new ReadOnlyMemory<byte>(buffer, 7, file.Length));
    }

    [Fact]
    public void Decode_reads_a_slice_as_the_array()
    {
        (byte[] file, ReadOnlyMemory<byte> slice) = Embedded();
        using Image fromArray = Image.Decode(file);
        using Image fromSlice = Image.Decode(slice);
        Assert.Equal(fromArray.ToString(), fromSlice.ToString());
        Assert.Equal(fromArray.GetPixelBytes().ToArray(), fromSlice.GetPixelBytes().ToArray());
    }

    [Fact]
    public void Identify_reads_a_slice_as_the_array()
    {
        (byte[] file, ReadOnlyMemory<byte> slice) = Embedded();
        IdentifyResult fromArray = Image.Identify(file);
        IdentifyResult fromSlice = Image.Identify(slice);
        Assert.True(fromSlice.Succeeded);
        ImageInfo a = fromArray.Info!, b = fromSlice.Info!;
        Assert.Equal((a.Format, a.Width, a.Height, a.HasAlpha, a.ColorSpace), (b.Format, b.Width, b.Height, b.HasAlpha, b.ColorSpace));
    }

    [Fact]
    public void Open_reads_a_slice_as_the_array()
    {
        (byte[] file, ReadOnlyMemory<byte> slice) = Embedded();
        using Image expected = Image.Decode(file);
        AnimationReader reader = AnimationReader.Open(slice);
        using (AnimationFrame? frame = reader.ReadFrame())
        {
            Assert.NotNull(frame);
            Assert.Equal(expected.GetPixelBytes().ToArray(), frame.Image.GetPixelBytes().ToArray());
        }

        Assert.Null(reader.ReadFrame());
    }

    [Fact]
    public async Task Load_reads_a_slice_as_the_array()
    {
        (byte[] file, ReadOnlyMemory<byte> slice) = Embedded();
        using Image fromArray = await ImagePipeline.Load(file).Fit(20, 20).ToImageAsync(TestContext.Current.CancellationToken);
        using Image fromSlice = await ImagePipeline.Load(slice).Fit(20, 20).ToImageAsync(TestContext.Current.CancellationToken);
        Assert.Equal(fromArray.GetPixelBytes().ToArray(), fromSlice.GetPixelBytes().ToArray());
    }

    [Fact]
    public void Empty_memory_is_not_an_image()
    {
        Assert.Equal(IdentifyStatus.NotAnImage, Image.Identify(ReadOnlyMemory<byte>.Empty).Status);
        Assert.Throws<UnsupportedImageFormatException>(() => Image.Decode(ReadOnlyMemory<byte>.Empty));
        Assert.Throws<UnsupportedImageFormatException>(() => AnimationReader.Open(ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void Arrays_and_collection_expressions_still_pick_the_array_overload()
    {
        // These must compile and keep throwing as before: a null array is refused, not read as empty memory.
        Assert.Throws<ArgumentNullException>(() => Image.Decode((byte[])null!));
        Assert.Throws<ArgumentNullException>(() => Image.Identify((byte[])null!));
        Assert.Throws<UnsupportedImageFormatException>(() => Image.Decode([1, 2, 3]));
        Assert.Equal(IdentifyStatus.NotAnImage, Image.Identify([1, 2, 3]).Status);
        Assert.Throws<UnsupportedImageFormatException>(() => Image.Decode(new Memory<byte>(new byte[3])));
        Assert.Throws<UnsupportedImageFormatException>(() => Image.Decode(new ArraySegment<byte>(new byte[3])));
    }
}
