using System;
using System.Buffers.Binary;
using System.Threading;

namespace Zorg.Imaging.Tests;

/// <summary>How <see cref="FakeAvif"/> misbehaves.</summary>
public enum FakeAvifFailure
{
    None,
    Header,
    Decode,
    WrongSize,
    WrongFormat,
    BadHeader,
}

/// <summary>
/// A stand-in AVIF codec: the file is a real <c>ftyp avif</c> box, then the stored width and height (4 bytes each,
/// big-endian) and the orientation (1 byte). Decoding fills the picture with one colour.
/// </summary>
public sealed class FakeAvif : ImageCodec
{
    private int _decodes;

    public FakeAvifFailure Fail { get; init; }

    public string? Missing { get; init; }

    public ImageFormat Claims { get; init; } = ImageFormat.Avif;

    public int Decodes => _decodes;

    public (int Width, int Height)? LastShrink { get; private set; }

    public override ImageFormat Format => Claims;

    public override string Name => "Fake AVIF";

    public override CodecAvailability Availability => Missing is null ? CodecAvailability.Available : CodecAvailability.Unavailable(Missing);

    public static byte[] File(int width, int height, int orientation = 1, int padding = 0)
    {
        byte[] file = new byte[24 + 9 + padding];
        BinaryPrimitives.WriteUInt32BigEndian(file, 24);
        "ftypavif"u8.CopyTo(file.AsSpan(4));
        "mif1avif"u8.CopyTo(file.AsSpan(16));
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(24), width);
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(28), height);
        file[32] = (byte)orientation;
        return file;
    }

    public override ImageInfo ReadHeader(ReadOnlySpan<byte> head, CodecContext context)
    {
        if (Fail == FakeAvifFailure.Header)
        {
            throw new InvalidOperationException("The header reader broke.");
        }

        if (head.Length < 33)
        {
            throw new ImageFormatException("The fake AVIF file ends inside its header.");
        }

        int width = BinaryPrimitives.ReadInt32BigEndian(head[24..]);
        int height = BinaryPrimitives.ReadInt32BigEndian(head[28..]);
        return Fail == FakeAvifFailure.BadHeader
            ? new ImageInfo(ImageFormat.Heif, width, height)
            : new ImageInfo(ImageFormat.Avif, width, height) { Orientation = head[32] };
    }

    public override Image Decode(ReadOnlyMemory<byte> data, ImageInfo header, CodecContext context)
    {
        Interlocked.Increment(ref _decodes);
        if (Fail == FakeAvifFailure.Decode)
        {
            throw new InvalidOperationException("The decoder broke.");
        }

        LastShrink = context.ShrinkTo(header.Width, header.Height);
        (int width, int height) = LastShrink ?? (header.Width, header.Height);
        if (Fail == FakeAvifFailure.WrongSize)
        {
            width++;
        }

        PixelFormat format = Fail == FakeAvifFailure.WrongFormat ? PixelFormat.Bgra32
            : context.PixelFormat == PixelFormat.None ? PixelFormat.Rgba32 : context.PixelFormat;
        var image = new Image(width, height, format);
        image.GetPixelBytes().Fill(0x80);
        return image;
    }
}
