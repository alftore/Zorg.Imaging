using System.Collections.Generic;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Codecs.Jpeg;

/// <summary>
/// Reads bits, most significant first, from one scan's entropy-coded data with the stuffed zero bytes already removed.
/// </summary>
/// <remarks>
/// Restart markers are gone from the data too; <see cref="Restarts"/> holds where each one was, so a restart can jump to
/// the next interval's first byte. Up to eight bytes past the end read as zeros, which a correct file never uses; using
/// them throws a truncated <see cref="ImageFormatException"/>.
/// </remarks>
internal sealed class JpegBitReader
{
    private readonly byte[] _data;
    private readonly int _length;
    private int _position;
    private ulong _buffer;
    private int _count;
    private int _restart;

    public JpegBitReader(byte[] data, int length, List<int> restarts)
    {
        _data = data;
        _length = length;
        Restarts = restarts;
    }

    /// <summary>The offsets in the data where each restart interval after the first begins.</summary>
    public List<int> Restarts { get; }

    /// <summary>Looks at the next 16 bits.</summary>
    public int Peek16()
    {
        if (_count < 16)
        {
            Fill();
        }

        return (int)(_buffer >> 48);
    }

    /// <summary>Consumes bits already looked at.</summary>
    public void Skip(int count)
    {
        _buffer <<= count;
        _count -= count;
        if (_position > _length && ((long)_position * 8) - _count > (long)_length * 8)
        {
            throw ImageErrors.Truncated("The JPEG file's image data ends early.");
        }
    }

    /// <summary>Reads 0 to 16 bits as an unsigned number.</summary>
    public int Read(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        if (_count < count)
        {
            Fill();
        }

        int value = (int)(_buffer >> (64 - count));
        Skip(count);
        return value;
    }

    /// <summary>Reads one bit.</summary>
    public bool ReadBit() => Read(1) != 0;

    /// <summary>Reads a coefficient of <paramref name="size"/> bits and extends its sign (T.81, F.2.2.1, EXTEND).</summary>
    public int ReadSigned(int size)
    {
        int value = Read(size);
        return value < (1 << (size - 1)) ? value - (1 << size) + 1 : value;
    }

    /// <summary>Moves to the start of the next restart interval.</summary>
    /// <exception cref="ImageFormatException">The data has fewer restart markers than the image needs.</exception>
    public void Restart()
    {
        if (_restart >= Restarts.Count)
        {
            throw ImageErrors.Corrupt("The JPEG image data is missing a restart marker.");
        }

        _position = Restarts[_restart++];
        _buffer = 0;
        _count = 0;
    }

    private void Fill()
    {
        while (_count <= 56)
        {
            ulong value = 0;
            if (_position < _length)
            {
                value = _data[_position];
            }
            else if (_position > _length + 8)
            {
                throw ImageErrors.Truncated("The JPEG file's image data ends early.");
            }

            _position++;
            _buffer |= value << (56 - _count);
            _count += 8;
        }
    }
}
