using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Zorg.Imaging.Formats;
using Zorg.Imaging.Metadata;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Codecs.Jpeg;

/// <summary>
/// Decodes a baseline, extended sequential or progressive JPEG with Huffman coding (ITU-T T.81), as JFIF or EXIF.
/// </summary>
/// <remarks>
/// <para>
/// Grey and YCbCr (or RGB, when an Adobe marker or the component identifiers say so) with any whole-number chroma
/// subsampling. Chroma is upsampled with a triangle filter, centred as JFIF places it. 12-bit, lossless, hierarchical
/// and arithmetic-coded files, and CMYK, throw <see cref="UnsupportedImageFormatException"/>.
/// </para>
/// <para>
/// Hostile input: the frame's size is checked against <see cref="ImageLimits.MaxPixels"/> before the coefficients are
/// allocated; a scan's data may be no larger than its blocks could use; there are at most <see cref="MaxScans"/> scans;
/// an ICC profile may total <see cref="ColorSpace.MaxIccProfileBytes"/>. Data that ends early throws, as PNG does.
/// </para>
/// <para>
/// Shrink-on-load: given a target size, the inverse DCT runs at 1/2, 1/4 or 1/8 scale (<see cref="JpegIdct"/>), as long
/// as the result stays at least twice the target, so the resampler still does the final, careful step.
/// </para>
/// </remarks>
internal static class JpegDecoder
{
    /// <summary>More scans than any encoder writes (progressive files use about 10); each costs a pass over the blocks.</summary>
    public const int MaxScans = 256;

    private const byte Sof0 = 0xC0;
    private const byte Sof1 = 0xC1;
    private const byte Sof2 = 0xC2;
    private const byte Dht = 0xC4;
    private const byte Soi = 0xD8;
    private const byte Eoi = 0xD9;
    private const byte Sos = 0xDA;
    private const byte Dqt = 0xDB;
    private const byte Dnl = 0xDC;
    private const byte Dri = 0xDD;
    private const byte App1 = 0xE1;
    private const byte App2 = 0xE2;
    private const byte App14 = 0xEE;

    private static ReadOnlySpan<byte> IccPrefix => "ICC_PROFILE\0"u8;

    public static async ValueTask<Image> DecodeAsync(DataReader reader, DecodeOptions options, CancellationToken cancellationToken)
    {
        reader.FormatName = "JPEG";
        byte[] two = new byte[2];
        await reader.ReadExactlyAsync(two, cancellationToken).ConfigureAwait(false);
        if (two[0] != 0xFF || two[1] != Soi)
        {
            throw ImageErrors.Corrupt("The JPEG file doesn't start with a start-of-image marker.");
        }

        var state = new State();
        while (true)
        {
            byte marker = await ReadMarkerAsync(reader, two, cancellationToken).ConfigureAwait(false);
            if (marker == Eoi)
            {
                break;
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                // TEM, or a restart marker outside a scan: no segment follows.
                continue;
            }

            if (marker == Soi)
            {
                throw ImageErrors.Corrupt("The JPEG file has a second start-of-image marker.");
            }

            await reader.ReadExactlyAsync(two, cancellationToken).ConfigureAwait(false);
            int length = (two[0] << 8) | two[1];
            if (length < 2)
            {
                throw ImageErrors.Corrupt("The JPEG file has a segment with an invalid length.");
            }

            byte[] data = new byte[length - 2];
            await reader.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);

            switch (marker)
            {
                case Sof0 or Sof1 or Sof2:
                    if (state.Frame is not null)
                    {
                        throw ImageErrors.Corrupt("The JPEG file has a second frame header.");
                    }

                    state.Frame = JpegFrame.Parse(data, marker == Sof2, options.Limits);
                    break;

                case >= 0xC3 and <= 0xCF when marker is not (0xC4 or 0xC8 or 0xCC):
                    throw new UnsupportedImageFormatException(
                        marker is 0xC3 or 0xC7 or 0xCB or 0xCF ? "Lossless JPEG isn't supported."
                        : marker >= 0xC9 ? "Arithmetic-coded JPEG isn't supported."
                        : "Hierarchical JPEG isn't supported.",
                        ImageFormat.Jpeg);

                case Dht:
                    state.ReadHuffmanTables(data);
                    break;

                case Dqt:
                    state.ReadQuantisationTables(data);
                    break;

                case Dri:
                    if (data.Length != 2)
                    {
                        throw ImageErrors.Corrupt("The JPEG restart interval segment has the wrong length.");
                    }

                    state.RestartInterval = (data[0] << 8) | data[1];
                    break;

                case Dnl:
                    throw new UnsupportedImageFormatException("The JPEG file declares its height after the image data (DNL marker), which isn't supported.", ImageFormat.Jpeg);

                case App1 when !state.SawExif && data.AsSpan().StartsWith("Exif\0\0"u8):
                    state.SawExif = true;
                    state.Orientation = ExifOrientation.Read(data);
                    break;

                case App2 when data.AsSpan().StartsWith(IccPrefix):
                    state.AddIccChunk(data);
                    break;

                case App14 when data.Length >= 12 && data.AsSpan().StartsWith("Adobe"u8):
                    state.AdobeTransform = data[11];
                    break;

                case Sos:
                    await DecodeScanAsync(reader, state, data, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    // APPn, COM and the rest: nothing to decode.
                    break;
            }
        }

        if (state.Frame is null || state.Scans == 0)
        {
            throw ImageErrors.Corrupt("The JPEG file has no image data.");
        }

        reader.Finish();
        ColorSpace colorSpace = state.ResolveColorSpace();
        int scale = ChooseScale(state.Frame, options, state.Orientation);
        Image image = JpegOutput.Build(state.Frame, scale, state.IsRgb, options.PixelFormat);
        image.ColorSpace = colorSpace;
        if (options.ApplyOrientation && state.Orientation != 1)
        {
            try
            {
                Image upright = Orientation.Apply(image, state.Orientation);
                image.Dispose();
                image = upright;
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        return image;
    }

    // The samples a side for the inverse DCT: 8 at full size, down to 1, keeping at least twice the target size.
    private static int ChooseScale(JpegFrame frame, DecodeOptions options, int orientation)
    {
        if (options.ShrinkTarget is not { } target)
        {
            return 8;
        }

        bool swap = options.ApplyOrientation && orientation >= 5;
        int width = swap ? frame.Height : frame.Width;
        int height = swap ? frame.Width : frame.Height;
        (int targetWidth, int targetHeight) = target(width, height);
        int scale = 8;
        foreach (int size in (ReadOnlySpan<int>)[4, 2, 1])
        {
            if (JpegFrame.DivideUp((long)width * size, 8) >= 2L * targetWidth && JpegFrame.DivideUp((long)height * size, 8) >= 2L * targetHeight)
            {
                scale = size;
            }
        }

        return scale;
    }

    private static async ValueTask DecodeScanAsync(DataReader reader, State state, byte[] header, CancellationToken cancellationToken)
    {
        if (state.Frame is null)
        {
            throw ImageErrors.Corrupt("The JPEG file has image data before its frame header.");
        }

        if (++state.Scans > MaxScans)
        {
            throw ImageErrors.ExceedsLimits(string.Create(CultureInfo.InvariantCulture, $"The JPEG file has more than {MaxScans} scans."));
        }

        JpegScan scan = state.ParseScan(header);

        // A block can't use more than about 216 bytes of data (64 codes of up to 16 bits, each with up to 11 more).
        long cap = (scan.Blocks * 256) + (1 << 20);
        var data = new EntropyData();
        try
        {
            while (true)
            {
                ReadOnlyMemory<byte> chunk = await reader.PeekAsync(4096, cancellationToken).ConfigureAwait(false);
                (int consumed, bool done) = data.Append(chunk.Span, chunk.Length < 4096, cap);
                await reader.SkipAsync(consumed, cancellationToken).ConfigureAwait(false);
                if (done)
                {
                    break;
                }
            }

            scan.Decode(new JpegBitReader(data.Buffer, data.Length, data.Restarts));
        }
        finally
        {
            data.Dispose();
        }
    }

    private static async ValueTask<byte> ReadMarkerAsync(DataReader reader, byte[] scratch, CancellationToken cancellationToken)
    {
        await reader.ReadExactlyAsync(scratch.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (scratch[0] != 0xFF)
        {
            throw ImageErrors.Corrupt("The JPEG file has data where a marker was expected.");
        }

        // Any number of 0xFF fill bytes may come before the marker code.
        do
        {
            await reader.ReadExactlyAsync(scratch.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        }
        while (scratch[0] == 0xFF);

        return scratch[0];
    }

    /// <summary>Everything read from the markers so far.</summary>
    private sealed class State
    {
        private readonly ushort[]?[] _quantisation = new ushort[4][];
        private readonly JpegHuffman?[] _dc = new JpegHuffman[4];
        private readonly JpegHuffman?[] _ac = new JpegHuffman[4];
        private readonly List<(int Sequence, int Count, byte[] Data)> _icc = [];
        private long _iccBytes;

        public JpegFrame? Frame { get; set; }

        public int RestartInterval { get; set; }

        public int Scans { get; set; }

        public bool SawExif { get; set; }

        public int Orientation { get; set; } = 1;

        public int AdobeTransform { get; set; } = -1;

        /// <summary>Three components stored as RGB, not YCbCr: an Adobe marker with transform 0, or identifiers R, G, B.</summary>
        public bool IsRgb => Frame is { Components.Length: 3 } frame
            && (AdobeTransform == 0 || (AdobeTransform < 0 && frame.Components[0].Id == 'R' && frame.Components[1].Id == 'G' && frame.Components[2].Id == 'B'));

        public void ReadHuffmanTables(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                if (data.Length < 17)
                {
                    throw ImageErrors.Corrupt("A JPEG Huffman table segment is too short.");
                }

                int kind = data[0] >> 4;
                int slot = data[0] & 15;
                if (kind > 1 || slot > 3)
                {
                    throw ImageErrors.Corrupt("A JPEG Huffman table has an invalid class or identifier.");
                }

                ReadOnlySpan<byte> counts = data.Slice(1, 16);
                int total = 0;
                foreach (byte count in counts)
                {
                    total += count;
                }

                if (total > 256 || data.Length < 17 + total)
                {
                    throw ImageErrors.Corrupt("A JPEG Huffman table has more values than its segment holds.");
                }

                JpegHuffman table = JpegHuffman.Create(counts, data.Slice(17, total));
                (kind == 0 ? _dc : _ac)[slot] = table;
                data = data[(17 + total)..];
            }
        }

        public void ReadQuantisationTables(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                int precision = data[0] >> 4;
                int slot = data[0] & 15;
                int size = precision == 0 ? 64 : 128;
                if (precision > 1 || slot > 3 || data.Length < 1 + size)
                {
                    throw ImageErrors.Corrupt("A JPEG quantisation table is invalid.");
                }

                ushort[] table = new ushort[64];
                for (int k = 0; k < 64; k++)
                {
                    table[k] = precision == 0 ? data[1 + k] : (ushort)((data[1 + (2 * k)] << 8) | data[2 + (2 * k)]);
                }

                _quantisation[slot] = table;
                data = data[(1 + size)..];
            }
        }

        public void AddIccChunk(byte[] data)
        {
            if (data.Length < IccPrefix.Length + 2)
            {
                throw ImageErrors.Corrupt("A JPEG ICC profile chunk is too short.");
            }

            _iccBytes += data.Length - IccPrefix.Length - 2;
            if (_iccBytes > ColorSpace.MaxIccProfileBytes)
            {
                throw ImageErrors.ExceedsLimits(string.Create(CultureInfo.InvariantCulture, $"The JPEG file's ICC profile is more than {ColorSpace.MaxIccProfileBytes} bytes."));
            }

            _icc.Add((data[IccPrefix.Length], data[IccPrefix.Length + 1], data[(IccPrefix.Length + 2)..]));
        }

        public ColorSpace ResolveColorSpace()
        {
            if (_icc.Count == 0)
            {
                return ColorSpace.Srgb;
            }

            // Chunks are numbered 1 to the count (ICC.1, B.4); every one must be there, once.
            int count = _icc[0].Count;
            if (count != _icc.Count)
            {
                throw ImageErrors.Corrupt("The JPEG file's ICC profile is missing chunks.");
            }

            _icc.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
            byte[] profile = new byte[_iccBytes];
            int at = 0;
            for (int i = 0; i < count; i++)
            {
                if (_icc[i].Sequence != i + 1 || _icc[i].Count != count)
                {
                    throw ImageErrors.Corrupt("The JPEG file's ICC profile chunks are numbered wrongly.");
                }

                _icc[i].Data.CopyTo(profile, at);
                at += _icc[i].Data.Length;
            }

            string? problem = ColorSpace.CheckIccProfile(profile);
            return problem is null ? ColorSpace.FromCheckedIccProfile(profile) : throw ImageErrors.Corrupt(problem);
        }

        public JpegScan ParseScan(ReadOnlySpan<byte> data)
        {
            JpegFrame frame = Frame!;
            int count = data.Length > 0 ? data[0] : 0;
            if (count is < 1 or > 4 || data.Length != 1 + (2 * count) + 3)
            {
                throw ImageErrors.Corrupt("The JPEG scan header is invalid.");
            }

            var components = new JpegComponent[count];
            int blocksPerMcu = 0;
            for (int i = 0; i < count; i++)
            {
                int id = data[1 + (2 * i)];
                JpegComponent? component = Array.Find(frame.Components, c => c.Id == id);
                if (component is null || Array.IndexOf(components, component) >= 0)
                {
                    throw ImageErrors.Corrupt("A JPEG scan names a component that isn't in the frame, or names one twice.");
                }

                int tables = data[2 + (2 * i)];
                if ((tables >> 4) > 3 || (tables & 15) > 3)
                {
                    throw ImageErrors.Corrupt("A JPEG scan names a Huffman table that can't exist.");
                }

                component.Dc = _dc[tables >> 4];
                component.Ac = _ac[tables & 15];
                component.Quantisation ??= _quantisation[component.QuantisationTable]?.Clone() as ushort[]
                    ?? throw ImageErrors.Corrupt("A JPEG component's quantisation table isn't defined.");
                components[i] = component;
                blocksPerMcu += component.H * component.V;
            }

            if (count > 1 && blocksPerMcu > 10)
            {
                throw ImageErrors.Corrupt("A JPEG scan has more than 10 blocks in each MCU.");
            }

            int start = data[^3];
            int end = data[^2];
            int high = data[^1] >> 4;
            int low = data[^1] & 15;
            bool dcNeeded;
            bool acNeeded;
            if (!frame.Progressive)
            {
                if (start != 0 || end != 63 || high != 0 || low != 0)
                {
                    throw ImageErrors.Corrupt("A sequential JPEG scan has a spectral selection or approximation.");
                }

                dcNeeded = acNeeded = true;
            }
            else
            {
                if (end > 63 || start > end || (start == 0 && end != 0) || (start > 0 && count != 1) || high > 13 || low > 13)
                {
                    throw ImageErrors.Corrupt("A progressive JPEG scan has an invalid spectral selection or approximation.");
                }

                dcNeeded = start == 0 && high == 0;
                acNeeded = start > 0;
            }

            foreach (JpegComponent component in components)
            {
                if ((dcNeeded && component.Dc is null) || (acNeeded && component.Ac is null))
                {
                    throw ImageErrors.Corrupt("A JPEG scan uses a Huffman table that isn't defined.");
                }
            }

            return new JpegScan(frame, components, start, end, high, low, RestartInterval);
        }
    }

    /// <summary>A scan's entropy-coded data, with stuffed zero bytes removed and restart markers noted.</summary>
    private sealed class EntropyData : IDisposable
    {
        public byte[] Buffer { get; private set; } = ArrayPool<byte>.Shared.Rent(64 * 1024);

        public int Length { get; private set; }

        public List<int> Restarts { get; } = [];

        /// <summary>Takes bytes up to the next marker that isn't a restart.</summary>
        /// <returns>How many bytes of <paramref name="chunk"/> to consume, and whether the marker was reached.</returns>
        public (int Consumed, bool Done) Append(ReadOnlySpan<byte> chunk, bool atEnd, long cap)
        {
            int i = 0;
            while (true)
            {
                int ff = chunk[i..].IndexOf((byte)0xFF);
                if (ff < 0)
                {
                    if (atEnd)
                    {
                        throw ImageErrors.Truncated("The JPEG file ends inside its image data.");
                    }

                    Add(chunk[i..], cap);
                    return (chunk.Length, false);
                }

                Add(chunk.Slice(i, ff), cap);
                i += ff;
                if (i + 1 >= chunk.Length)
                {
                    // 0xFF is the last byte here: look again from it, with what follows.
                    if (atEnd)
                    {
                        throw ImageErrors.Truncated("The JPEG file ends inside its image data.");
                    }

                    return (i, false);
                }

                byte next = chunk[i + 1];
                if (next == 0x00)
                {
                    Add([0xFF], cap);
                    i += 2;
                }
                else if (next is >= 0xD0 and <= 0xD7)
                {
                    Restarts.Add(Length);
                    i += 2;
                }
                else
                {
                    // A marker (or a fill byte before one): the scan's data ends here.
                    return (i, true);
                }
            }
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(Buffer);
            Buffer = [];
        }

        private void Add(ReadOnlySpan<byte> bytes, long cap)
        {
            if (Length + (long)bytes.Length > cap)
            {
                throw ImageErrors.Corrupt("A JPEG scan has more data than its blocks could use.");
            }

            if (Length + bytes.Length > Buffer.Length)
            {
                byte[] larger = ArrayPool<byte>.Shared.Rent((int)Math.Min(Math.Max((long)Buffer.Length * 2, Length + bytes.Length), Array.MaxLength));
                Buffer.AsSpan(0, Length).CopyTo(larger);
                ArrayPool<byte>.Shared.Return(Buffer);
                Buffer = larger;
            }

            bytes.CopyTo(Buffer.AsSpan(Length));
            Length += bytes.Length;
        }
    }
}
