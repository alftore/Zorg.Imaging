using System;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Codecs.Bmp;

/// <summary>Reads the BMP headers for <see cref="Image.Identify"/>.</summary>
internal static class BmpHeaderReader
{
    /// <summary>Reads the headers. The reader is at offset 0, before the signature.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="options">The options.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>What the headers say.</returns>
    public static async ValueTask<ImageInfo> ReadAsync(HeaderReader reader, IdentifyOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);
        byte[] peek = new byte[34];
        int available = await reader.PeekAsync(peek, cancellationToken).ConfigureAwait(false);
        if (available < 18)
        {
            throw ImageErrors.Truncated("The BMP file ends inside its header.");
        }

        byte[] head = new byte[BmpHeader.LengthToRead(peek.AsSpan(0, available))];
        await reader.ReadExactlyAsync(head, cancellationToken).ConfigureAwait(false);
        BmpHeader header = BmpHeader.Parse(head);

        // Alpha may be there: 32-bit pixels whose alpha isn't all 0, or pixels a run-length file skips.
        bool alpha = header.HasAlphaMask || header.Compression is BmpHeader.Rle8 or BmpHeader.Rle4;
        return new ImageInfo(ImageFormat.Bmp, header.Width, header.Height)
        {
            HasAlpha = alpha,
            FrameCount = options.CountFrames ? 1 : null,
            HasIccProfile = header.Profile.Size > 0,
            ColorSpace = options.ReadColorSpace && header.Profile.Size == 0 ? ColorSpace.Srgb : null,
        };
    }
}
