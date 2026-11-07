using System;
using System.Buffers.Binary;
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

    /// <summary>
    /// The reader's state, for a hot loop to copy into locals (where the JIT keeps it in registers) and give back with
    /// <see cref="Store"/>.
    /// </summary>
    public (ulong Buffer, int Count, int Position) State => (_buffer, _count, _position);

    public byte[] Data => _data;

    public int Length => _length;

    public void Store(ulong buffer, int count, int position)
    {
        _buffer = buffer;
        _count = count;
        _position = position;
        if (position > _length && ((long)position * 8) - count > (long)_length * 8)
        {
            throw ImageErrors.Truncated("The JPEG file's image data ends early.");
        }
    }

    /// <summary>The same refill as <see cref="Fill"/>, on a hot loop's locals.</summary>
    public static void Refill(ref ulong buffer, ref int count, ref int position, byte[] data, int length)
    {
        if (position > length + 8)
        {
            throw ImageErrors.Truncated("The JPEG file's image data ends early.");
        }

        ulong next = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(position, 8));
        int bytes = (63 - count) >> 3;
        int total = count + (bytes * 8);
        buffer |= (next >> count) & (~0UL << (64 - total));
        position += bytes;
        count = total;
    }
    private int _restart;

    /// <summary>How many zero bytes must follow the data in its array.</summary>
    public const int Padding = 16;

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
    }

    /// <summary>
    /// Throws when the scan used bits past its data. Checked once a block and at the end of the scan rather than on every
    /// read: the zero padding after the data can be read harmlessly, and Fill refuses to go further.
    /// </summary>
    public void CheckEnd()
    {
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
        _buffer <<= count;
        _count -= count;
        return value;
    }

    /// <summary>Reads one bit.</summary>
    public bool ReadBit()
    {
        if (_count == 0)
        {
            Fill();
        }

        bool bit = (long)_buffer < 0;
        _buffer <<= 1;
        _count--;
        return bit;
    }

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

    // Tops the buffer up with as many whole bytes as fit, from one 8-byte load. The data is followed by at least 16 zero
    // bytes (Padding), so the load never leaves the array; reading into them is caught by Skip.
    private void Fill()
    {
        if (_position > _length + 8)
        {
            throw ImageErrors.Truncated("The JPEG file's image data ends early.");
        }

        ulong next = BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(_position, 8));
        int bytes = (63 - _count) >> 3;
        int total = _count + (bytes * 8);
        _buffer |= (next >> _count) & (~0UL << (64 - total));
        _position += bytes;
        _count = total;
    }
}
