using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Codecs.Webp;

/// <summary>Reads a VP8L bit stream: least significant bit first (RFC 9649, section 3.1).</summary>
/// <remarks>
/// Up to eight bytes past the end read as zeros, which a correct stream never uses; using them throws a truncated
/// <see cref="ImageFormatException"/>.
/// </remarks>
internal sealed class Vp8lBitReader(ReadOnlyMemory<byte> data)
{
    private readonly ReadOnlyMemory<byte> _data = data;
    private int _position;
    private ulong _buffer;
    private int _count;

    /// <summary>The state, for a hot loop to keep in locals (registers) and give back with <see cref="Store"/>.</summary>
    public (ulong Buffer, int Count, int Position) State => (_buffer, _count, _position);

    /// <summary>The data the reader reads.</summary>
    public ReadOnlySpan<byte> Data => _data.Span;

    /// <summary>Takes back the state from a hot loop, and throws when it used bits past the data.</summary>
    public void Store(ulong buffer, int count, int position)
    {
        _buffer = buffer;
        _count = count;
        _position = position;
        if (_position > _data.Length && ((long)_position * 8) - _count > (long)_data.Length * 8)
        {
            throw ImageErrors.Truncated("The WebP lossless data ends early.");
        }
    }

    /// <summary>Tops a hot loop's buffer up to at least 57 bits: whole bytes from one 8-byte load where it can.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Refill(ref ulong buffer, ref int count, ref int position, ReadOnlySpan<byte> data)
    {
        if (position + 8 <= data.Length)
        {
            ulong next = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(position, 8));
            int bytes = (63 - count) >> 3;
            buffer |= (next & ((1UL << (bytes * 8)) - 1)) << count;
            position += bytes;
            count += bytes * 8;
            return;
        }

        RefillTail(ref buffer, ref count, ref position, data);
    }

    private static void RefillTail(ref ulong buffer, ref int count, ref int position, ReadOnlySpan<byte> data)
    {
        while (count <= 56)
        {
            ulong value = 0;
            if (position < data.Length)
            {
                value = data[position];
            }
            else if (position > data.Length + 8)
            {
                throw ImageErrors.Truncated("The WebP lossless data ends early.");
            }

            position++;
            buffer |= value << count;
            count += 8;
        }
    }

    /// <summary>Reads 0 to 32 bits as an unsigned number.</summary>
    public uint Read(int bits)
    {
        if (bits == 0)
        {
            return 0;
        }

        if (_count < bits)
        {
            Fill();
        }

        uint value = (uint)(_buffer & ((1UL << bits) - 1));
        Skip(bits);
        return value;
    }

    public bool ReadBit() => Read(1) != 0;

    /// <summary>Looks at the next 16 bits, least significant first.</summary>
    public int Peek16()
    {
        if (_count < 16)
        {
            Fill();
        }

        return (int)(_buffer & 0xFFFF);
    }

    public void Skip(int bits)
    {
        _buffer >>= bits;
        _count -= bits;
        if (_position > _data.Length && ((long)_position * 8) - _count > (long)_data.Length * 8)
        {
            throw ImageErrors.Truncated("The WebP lossless data ends early.");
        }
    }

    private void Fill()
    {
        ReadOnlySpan<byte> data = _data.Span;
        while (_count <= 56)
        {
            ulong value = 0;
            if (_position < data.Length)
            {
                value = data[_position];
            }
            else if (_position > data.Length + 8)
            {
                throw ImageErrors.Truncated("The WebP lossless data ends early.");
            }

            _position++;
            _buffer |= value << _count;
            _count += 8;
        }
    }
}
