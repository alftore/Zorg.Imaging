using System.Collections.Generic;
using System.Globalization;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Codecs.Jpeg;

/// <summary>
/// The ICC profile of a JPEG file, from APP2 segments that each hold a numbered part (ICC.1, B.4): collected in any
/// order, then joined once every part is there. Used by the decoder and, when asked, by the header reader.
/// </summary>
internal sealed class JpegIccChunks
{
    /// <summary>The signature that starts each part's segment data.</summary>
    public static readonly byte[] Prefix = "ICC_PROFILE\0"u8.ToArray();

    private readonly List<(int Sequence, int Count, byte[] Data)> _parts = [];
    private long _bytes;

    /// <summary>Takes a segment's data: the prefix, the part's number and the count, then the part.</summary>
    public void Add(byte[] data)
    {
        if (data.Length < Prefix.Length + 2)
        {
            throw ImageErrors.Corrupt("A JPEG ICC profile chunk is too short.");
        }

        _bytes += data.Length - Prefix.Length - 2;
        if (_bytes > ColorSpace.MaxIccProfileBytes)
        {
            throw ImageErrors.ExceedsLimits(string.Create(CultureInfo.InvariantCulture, $"The JPEG file's ICC profile is more than {ColorSpace.MaxIccProfileBytes} bytes."));
        }

        _parts.Add((data[Prefix.Length], data[Prefix.Length + 1], data[(Prefix.Length + 2)..]));
    }

    /// <summary>The colour space the profile describes, or sRGB when there is none.</summary>
    public ColorSpace Resolve()
    {
        if (_parts.Count == 0)
        {
            return ColorSpace.Srgb;
        }

        // Chunks are numbered 1 to the count; every one must be there, once.
        int count = _parts[0].Count;
        if (count != _parts.Count)
        {
            throw ImageErrors.Corrupt("The JPEG file's ICC profile is missing chunks.");
        }

        _parts.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        byte[] profile = new byte[_bytes];
        int at = 0;
        for (int i = 0; i < count; i++)
        {
            if (_parts[i].Sequence != i + 1 || _parts[i].Count != count)
            {
                throw ImageErrors.Corrupt("The JPEG file's ICC profile chunks are numbered wrongly.");
            }

            _parts[i].Data.CopyTo(profile, at);
            at += _parts[i].Data.Length;
        }

        string? problem = ColorSpace.CheckIccProfile(profile);
        return problem is null ? ColorSpace.FromCheckedIccProfile(profile) : throw ImageErrors.Corrupt(problem);
    }
}
