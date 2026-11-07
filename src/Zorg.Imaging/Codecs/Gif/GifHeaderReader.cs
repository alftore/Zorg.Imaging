using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Codecs.Gif;

/// <summary>Reads the GIF headers for <see cref="Image.Identify"/>: the first frame, or every frame when counting.</summary>
internal static class GifHeaderReader
{
    private const byte Extension = 0x21;
    private const byte ImageDescriptor = 0x2C;
    private const byte Trailer = 0x3B;
    private const byte GraphicControl = 0xF9;
    private const byte Application = 0xFF;

    /// <summary>Reads the headers. The reader is at offset 0, before the signature.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="options">The options.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What the headers say.</returns>
    public static async ValueTask<ImageInfo> ReadAsync(HeaderReader reader, IdentifyOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);

        await reader.SkipAsync(6, cancellationToken).ConfigureAwait(false);

        byte[] screen = new byte[7];
        await reader.ReadExactlyAsync(screen, cancellationToken).ConfigureAwait(false);
        int width = BinaryPrimitives.ReadUInt16LittleEndian(screen);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(screen.AsSpan(2));
        await SkipColourTableAsync(reader, screen[4], cancellationToken).ConfigureAwait(false);

        bool countFrames = options.CountFrames;
        bool hasAlpha = false;
        bool hasLoop = false;
        bool hasIcc = false;
        int frames = 0;
        byte[] block = new byte[255];
        byte[] one = new byte[1];
        byte[] descriptor = new byte[9];

        while (true)
        {
            if (frames > 0 && await reader.PeekAsync(one, cancellationToken).ConfigureAwait(false) == 0)
            {
                break;
            }

            byte introducer = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (introducer == Trailer)
            {
                break;
            }

            if (introducer == Extension)
            {
                byte label = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
                int size = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (size > 0)
                {
                    await reader.ReadExactlyAsync(block.AsMemory(0, size), cancellationToken).ConfigureAwait(false);
                    if (label == GraphicControl && (block[0] & 1) != 0)
                    {
                        hasAlpha = true;
                    }
                    else if (label == Application && size == 11)
                    {
                        ReadOnlySpan<byte> name = block.AsSpan(0, 11);
                        if (name.SequenceEqual("NETSCAPE2.0"u8) || name.SequenceEqual("ANIMEXTS1.0"u8))
                        {
                            hasLoop = true;
                        }
                        else if (name.SequenceEqual("ICCRGBG1012"u8))
                        {
                            hasIcc = true;
                        }
                    }

                    await SkipSubBlocksAsync(reader, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            if (introducer != ImageDescriptor)
            {
                throw ImageErrors.Corrupt("The GIF file has a block that isn't an extension, an image or the trailer.");
            }

            frames++;
            bool needDescriptor = countFrames || width == 0 || height == 0;
            if (!needDescriptor)
            {
                break;
            }

            await reader.ReadExactlyAsync(descriptor, cancellationToken).ConfigureAwait(false);
            if (frames == 1 && (width == 0 || height == 0))
            {
                width = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(4));
                height = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(6));
            }

            if (!countFrames)
            {
                break;
            }

            if (frames > options.Limits.MaxFrames)
            {
                throw ImageErrors.ExceedsLimits(Identifier.FramesOverLimit(options.Limits));
            }

            await SkipColourTableAsync(reader, descriptor[8], cancellationToken).ConfigureAwait(false);
            await reader.SkipAsync(1, cancellationToken).ConfigureAwait(false);
            await SkipSubBlocksAsync(reader, cancellationToken).ConfigureAwait(false);
        }

        if (frames == 0)
        {
            throw ImageErrors.Corrupt("The GIF file has no image.");
        }

        return new ImageInfo(ImageFormat.Gif, width, height)
        {
            HasAlpha = hasAlpha,
            IsAnimated = hasLoop || (countFrames && frames > 1),
            FrameCount = countFrames ? frames : null,
            HasIccProfile = hasIcc,
        };
    }

    private static ValueTask SkipColourTableAsync(HeaderReader reader, byte packed, CancellationToken cancellationToken)
    {
        if ((packed & 0x80) == 0)
        {
            return ValueTask.CompletedTask;
        }

        return reader.SkipAsync(3L << ((packed & 7) + 1), cancellationToken);
    }

    // Skips sub-blocks up to and including the zero-length terminator. Each pass reads at least one byte.
    private static async ValueTask SkipSubBlocksAsync(HeaderReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            byte length = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (length == 0)
            {
                return;
            }

            await reader.SkipAsync(length, cancellationToken).ConfigureAwait(false);
        }
    }
}
