using System;

namespace Zorg.Imaging.Codecs.Png;

/// <summary>The CRC-32 that every PNG chunk carries (ISO 3309, reflected polynomial 0xEDB88320).</summary>
/// <remarks>
/// <c>System.Runtime.Intrinsics.X86.Sse42.Crc32</c> computes CRC-32C, a different polynomial, so it can't be used
/// here. <c>System.IO.Hashing.Crc32</c> is right but is a NuGet package, and the library takes no dependencies.
/// </remarks>
internal static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    /// <summary>Computes the CRC of a whole buffer.</summary>
    /// <param name="data">The bytes to check: for a PNG chunk, its type followed by its data.</param>
    /// <returns>The CRC-32 value.</returns>
    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);

    /// <summary>Continues a CRC over more bytes, so a chunk can be checked in parts.</summary>
    /// <param name="crc">The value returned for the bytes before <paramref name="data"/>, or 0 to start.</param>
    /// <param name="data">The next bytes.</param>
    /// <returns>The CRC-32 value of everything so far.</returns>
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        uint c = ~crc;
        foreach (byte b in data)
        {
            c = Table[(int)((c ^ b) & 0xFF)] ^ (c >> 8);
        }

        return ~c;
    }

    private static uint[] CreateTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
