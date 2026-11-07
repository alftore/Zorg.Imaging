using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Xunit;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Tests;

public class ImageBufferTests
{
    public static TheoryData<PixelFormat, int> Formats { get; } = new()
    {
        { PixelFormat.Rgba32, 4 },
        { PixelFormat.Bgra32, 4 },
        { PixelFormat.Rgb24, 3 },
        { PixelFormat.L8, 1 },
        { PixelFormat.Rgba64, 8 },
        { PixelFormat.Rgb48, 6 },
        { PixelFormat.L16, 2 },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void New_image_has_the_size_and_layout_asked_for(PixelFormat format, int bytesPerPixel)
    {
        using var image = new Image(7, 5, format);

        Assert.Equal(7, image.Width);
        Assert.Equal(5, image.Height);
        Assert.Equal(format, image.PixelFormat);
        Assert.Equal(bytesPerPixel, image.BytesPerPixel);
        Assert.Equal(7 * bytesPerPixel, image.Stride);
        Assert.Equal(7 * 5 * bytesPerPixel, image.GetPixelBytes().Length);
        Assert.Equal(image.Stride, image.GetRowBytes(4).Length);
        Assert.Equal($"{format} 7x5", image.ToString());
    }

    [Fact]
    public void New_image_is_zeroed_even_when_the_pool_hands_back_dirty_memory()
    {
        // Dirty a pooled array of the size the image will rent, then give it back.
        byte[] dirty = ArrayPool<byte>.Shared.Rent(64 * 64 * 4);
        dirty.AsSpan().Fill(0xAB);
        ArrayPool<byte>.Shared.Return(dirty);

        using var image = new Image(64, 64, PixelFormat.Rgba32);

        Assert.Equal(-1, image.GetPixelBytes().IndexOfAnyExcept((byte)0));
    }

    [Fact]
    public void Rows_are_laid_out_one_after_another()
    {
        using var image = new Image(3, 2, PixelFormat.Rgb24);
        image.GetRow<Rgb24>(1)[2] = new Rgb24(1, 2, 3);

        Assert.Equal([1, 2, 3], image.GetPixelBytes()[15..18].ToArray());
        Assert.Equal(new Rgb24(1, 2, 3), image.GetRow<Rgb24>(1)[2]);
    }

    [Fact]
    public void Channels_are_stored_in_the_order_the_format_names_them()
    {
        using var rgba = new Image(1, 1, PixelFormat.Rgba32);
        using var bgra = new Image(1, 1, PixelFormat.Bgra32);
        using var grey = new Image(1, 1, PixelFormat.L8);

        rgba.GetRow<Rgba32>(0)[0] = new Rgba32(10, 20, 30, 40);
        bgra.GetRow<Bgra32>(0)[0] = new Bgra32(30, 20, 10, 40);
        grey.GetRow<L8>(0)[0] = new L8(99);

        Assert.Equal([10, 20, 30, 40], rgba.GetPixelBytes().ToArray());
        Assert.Equal([30, 20, 10, 40], bgra.GetPixelBytes().ToArray());
        Assert.Equal([99], grey.GetPixelBytes().ToArray());
    }

    [Fact]
    public void Pixel_types_are_the_size_of_their_channels()
    {
        Assert.Equal(4, Unsafe.SizeOf<Rgba32>());
        Assert.Equal(4, Unsafe.SizeOf<Bgra32>());
        Assert.Equal(3, Unsafe.SizeOf<Rgb24>());
        Assert.Equal(1, Unsafe.SizeOf<L8>());
        Assert.Equal(8, Unsafe.SizeOf<Rgba64>());
        Assert.Equal(6, Unsafe.SizeOf<Rgb48>());
        Assert.Equal(2, Unsafe.SizeOf<L16>());
    }

    [Fact]
    public void Sixteen_bit_channels_are_in_the_machine_byte_order()
    {
        using var rgba = new Image(1, 1, PixelFormat.Rgba64);
        using var rgb = new Image(2, 1, PixelFormat.Rgb48);
        using var grey = new Image(1, 1, PixelFormat.L16);

        rgba.GetRow<Rgba64>(0)[0] = new Rgba64(0x0102, 0x0304, 0x0506, 0x0708);
        rgb.GetRow<Rgb48>(0)[1] = new Rgb48(1, 2, 65535);
        grey.GetRow<L16>(0)[0] = new L16(0xABCD);

        Assert.True(BitConverter.IsLittleEndian);
        Assert.Equal([2, 1, 4, 3, 6, 5, 8, 7], rgba.GetPixelBytes().ToArray());
        Assert.Equal([0, 0, 0, 0, 0, 0, 1, 0, 2, 0, 255, 255], rgb.GetPixelBytes().ToArray());
        Assert.Equal([0xCD, 0xAB], grey.GetPixelBytes().ToArray());
        Assert.Equal(new Rgba64(0x0102, 0x0304, 0x0506, 0x0708), rgba.Clone().GetRow<Rgba64>(0)[0]);
    }

    [Fact]
    public void Row_of_the_wrong_pixel_type_is_refused()
    {
        using var image = new Image(2, 2, PixelFormat.Rgba32);

        InvalidOperationException wrong = Assert.Throws<InvalidOperationException>(() => image.GetRow<Bgra32>(0));
        Assert.Contains("Rgba32", wrong.Message, StringComparison.Ordinal);
        InvalidOperationException notPixel = Assert.Throws<InvalidOperationException>(() => image.GetRow<int>(0));
        Assert.Contains("isn't a pixel type", notPixel.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_outside_the_image_are_refused()
    {
        using var image = new Image(2, 3, PixelFormat.L8);

        Assert.Throws<ArgumentOutOfRangeException>(() => image.GetRowBytes(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.GetRowBytes(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.GetRow<L8>(3));
    }

    [Fact]
    public void Sizes_that_make_no_sense_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Image(0, 1, PixelFormat.Rgba32));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Image(1, -1, PixelFormat.Rgba32));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Image(1, 1, PixelFormat.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Image(1, 1, (PixelFormat)99));

        // 65536 x 65536 x 4 bytes is 16 GiB: refused before anything is allocated.
        Assert.Throws<ArgumentOutOfRangeException>(() => new Image(65536, 65536, PixelFormat.Rgba32));
    }

    [Fact]
    public void Clone_has_its_own_pixels()
    {
        using var image = new Image(2, 2, PixelFormat.Rgba32);
        image.GetRow<Rgba32>(0)[0] = new Rgba32(1, 2, 3);

        using Image copy = image.Clone();
        copy.GetRow<Rgba32>(0)[0] = new Rgba32(9, 9, 9);

        Assert.Equal(new Rgba32(1, 2, 3), image.GetRow<Rgba32>(0)[0]);
        Assert.Equal(new Rgba32(9, 9, 9), copy.GetRow<Rgba32>(0)[0]);
        Assert.Equal(image.PixelFormat, copy.PixelFormat);
    }

    [Fact]
    public void Disposed_image_keeps_its_size_but_not_its_pixels()
    {
        var image = new Image(4, 3, PixelFormat.Rgb24);
        image.Dispose();
        image.Dispose();

        Assert.Equal(4, image.Width);
        Assert.Throws<ObjectDisposedException>(() => image.GetPixelBytes());
        Assert.Throws<ObjectDisposedException>(() => image.GetRowBytes(0));
        Assert.Throws<ObjectDisposedException>(() => image.GetRow<Rgb24>(0));
        Assert.Throws<ObjectDisposedException>(() => image.Clone());
    }

    [Fact]
    public void Pixels_compare_by_channel_values()
    {
        Assert.Equal(new Rgba32(1, 2, 3, 255), new Rgba32(1, 2, 3));
        Assert.True(new Bgra32(1, 2, 3) == new Bgra32(1, 2, 3, 255));
        Assert.True(new Rgb24(1, 2, 3) != new Rgb24(1, 2, 4));
        Assert.False(new L8(5).Equals((object)new Rgb24(5, 5, 5)));
        Assert.Equal(new L8(5).GetHashCode(), new L8(5).GetHashCode());
        Assert.Equal("Rgba32(1, 2, 3, 255)", new Rgba32(1, 2, 3).ToString());
        Assert.Equal("L8(7)", new L8(7).ToString());
        Assert.Equal(new Rgba64(1, 2, 3, 65535), new Rgba64(1, 2, 3));
        Assert.True(new Rgb48(1, 2, 3) != new Rgb48(1, 2, 4));
        Assert.True(new L16(500) == new L16(500));
        Assert.Equal(new Rgb48(9, 8, 7).GetHashCode(), new Rgb48(9, 8, 7).GetHashCode());
        Assert.Equal("Rgba64(1, 2, 3, 65535)", new Rgba64(1, 2, 3).ToString());
        Assert.Equal("Rgb48(1, 2, 3)", new Rgb48(1, 2, 3).ToString());
        Assert.Equal("L16(700)", new L16(700).ToString());
    }

    public static TheoryData<ImageException, IdentifyStatus> Statuses { get; } = new()
    {
        { ImageErrors.Truncated("The PNG file ends early."), IdentifyStatus.Truncated },
        { ImageErrors.Corrupt("The PNG file is bad."), IdentifyStatus.Corrupt },
        { ImageErrors.ExceedsLimits("Too big."), IdentifyStatus.ExceedsLimits },
        { new ImageFormatException("Bad."), IdentifyStatus.Corrupt },
        { new UnsupportedImageFormatException("TIFF isn't supported.", ImageFormat.Tiff), IdentifyStatus.UnsupportedFormat },
        { new ImageException(), IdentifyStatus.Corrupt },
    };

    [Theory]
    [MemberData(nameof(Statuses))]
    public void Every_exception_maps_to_an_identify_status(ImageException exception, IdentifyStatus status) =>
        Assert.Equal(status, exception.Status);

    [Fact]
    public void Exceptions_share_one_base_and_keep_their_details()
    {
        var unsupported = new UnsupportedImageFormatException("TIFF isn't supported.", ImageFormat.Tiff);
        var inner = new InvalidOperationException("inner");

        Assert.IsAssignableFrom<ImageException>(new ImageFormatException());
        Assert.IsAssignableFrom<ImageException>(new ImageLimitException());
        Assert.Same(ImageFormat.Tiff, unsupported.Format);
        Assert.Null(new UnsupportedImageFormatException().Format);
        Assert.Same(inner, new ImageLimitException("Too big.", inner).InnerException);
        Assert.False(string.IsNullOrEmpty(new ImageException().Message));
    }
}
