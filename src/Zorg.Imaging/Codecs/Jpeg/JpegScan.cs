using System;
using Zorg.Imaging.Formats;

namespace Zorg.Imaging.Codecs.Jpeg;

/// <summary>
/// Decodes one scan's entropy-coded data into the frame's coefficients: sequential (ITU-T T.81, F.2.2) or one of the
/// four progressive kinds (G.1.2): DC first, DC refinement, AC first and AC refinement.
/// </summary>
internal sealed class JpegScan
{
    // Coefficients are stored in natural (row-major) order, so the inverse DCT can load them as vectors; the
    // entropy-coded order is zigzag (T.81, Figure A.6), and this maps one to the other.
    private static ReadOnlySpan<byte> ZigZag => JpegIdct.ZigZag;

    private readonly JpegFrame _frame;
    private readonly JpegComponent[] _components;
    private readonly int _start;
    private readonly int _end;
    private readonly int _high;
    private readonly int _low;
    private readonly int _restartInterval;
    private int _endOfBandRun;

    public JpegScan(JpegFrame frame, JpegComponent[] components, int start, int end, int high, int low, int restartInterval)
    {
        _frame = frame;
        _components = components;
        _start = start;
        _end = end;
        _high = high;
        _low = low;
        _restartInterval = restartInterval;
    }

    /// <summary>The blocks the scan covers, which bounds how much data it can use.</summary>
    public long Blocks
    {
        get
        {
            if (_components.Length == 1)
            {
                return (long)_components[0].UsedWide * _components[0].UsedHigh;
            }

            long blocks = 0;
            foreach (JpegComponent component in _components)
            {
                blocks += (long)component.BlocksWide * component.BlocksHigh;
            }

            return blocks;
        }
    }

    public void Decode(JpegBitReader bits)
    {
        foreach (JpegComponent component in _components)
        {
            component.DcPredictor = 0;
        }

        if (_components.Length == 1)
        {
            // Not interleaved: the component's own blocks, row by row, one per MCU (A.2.2).
            JpegComponent component = _components[0];
            int wide = component.UsedWide;
            long count = (long)wide * component.UsedHigh;
            for (long i = 0; i < count; i++)
            {
                RestartIfDue(bits, i);
                DecodeBlock(bits, component, component.Block((int)(i % wide), (int)(i / wide)));
            }

            return;
        }

        // Interleaved: each MCU holds H x V blocks of each component in turn (A.2.3).
        long mcus = (long)_frame.McusWide * _frame.McusHigh;
        for (long m = 0; m < mcus; m++)
        {
            RestartIfDue(bits, m);
            int mx = (int)(m % _frame.McusWide);
            int my = (int)(m / _frame.McusWide);
            foreach (JpegComponent component in _components)
            {
                for (int v = 0; v < component.V; v++)
                {
                    for (int h = 0; h < component.H; h++)
                    {
                        DecodeBlock(bits, component, component.Block((mx * component.H) + h, (my * component.V) + v));
                    }
                }
            }
        }
    }

    private void RestartIfDue(JpegBitReader bits, long mcu)
    {
        if (_restartInterval > 0 && mcu > 0 && mcu % _restartInterval == 0)
        {
            bits.Restart();
            foreach (JpegComponent component in _components)
            {
                component.DcPredictor = 0;
            }

            _endOfBandRun = 0;
        }
    }

    private void DecodeBlock(JpegBitReader bits, JpegComponent component, Span<short> block)
    {
        DecodeBlockCore(bits, component, block);
        bits.CheckEnd();
    }

    private void DecodeBlockCore(JpegBitReader bits, JpegComponent component, Span<short> block)
    {
        if (!_frame.Progressive)
        {
            DecodeSequential(bits, component, block);
        }
        else if (_start == 0)
        {
            if (_high == 0)
            {
                DecodeDcFirst(bits, component, block);
            }
            else if (bits.ReadBit())
            {
                block[0] |= (short)(1 << _low);
            }
        }
        else if (_high == 0)
        {
            DecodeAcFirst(bits, component, block);
        }
        else
        {
            DecodeAcRefine(bits, component, block);
        }
    }

    // The hot loop of baseline decoding: the bit buffer lives in locals, codes of up to 9 bits (and most AC
    // coefficients with their value bits) decode with one table lookup, and the reader is only called for longer codes.
    private static void DecodeSequential(JpegBitReader bits, JpegComponent component, Span<short> block)
    {
        (ulong buffer, int count, int position) = bits.State;
        byte[] data = bits.Data;
        int length = bits.Length;
        const int Shift = 64 - JpegHuffman.FastBits;

        // DC: a size category, then that many bits of difference.
        if (count < 32)
        {
            JpegBitReader.Refill(ref buffer, ref count, ref position, data, length);
        }

        JpegHuffman dc = component.Dc!;
        int dcSize;
        int fastDc = dc.Fast[(int)(buffer >> Shift)];
        if (fastDc != 0)
        {
            buffer <<= fastDc >> 8;
            count -= fastDc >> 8;
            dcSize = fastDc & 0xFF;
        }
        else
        {
            bits.Store(buffer, count, position);
            dcSize = dc.Decode(bits);
            (buffer, count, position) = bits.State;
        }

        if (dcSize > 11)
        {
            throw ImageErrors.Corrupt("The JPEG image data has a DC difference of more than 11 bits.");
        }

        if (dcSize > 0)
        {
            if (count < dcSize)
            {
                JpegBitReader.Refill(ref buffer, ref count, ref position, data, length);
            }

            int raw = (int)(buffer >> (64 - dcSize));
            buffer <<= dcSize;
            count -= dcSize;
            component.DcPredictor += raw < (1 << (dcSize - 1)) ? raw - (1 << dcSize) + 1 : raw;
        }

        block[0] = (short)component.DcPredictor;
        JpegHuffman ac = component.Ac!;
        int[] fast = ac.FastAc;
        ReadOnlySpan<byte> zigZag = ZigZag;
        for (int k = 1; k < 64;)
        {
            if (count < 32)
            {
                JpegBitReader.Refill(ref buffer, ref count, ref position, data, length);
            }

            int entry = fast[(int)(buffer >> Shift)];
            if (entry != 0)
            {
                int used = entry & 0xFF;
                buffer <<= used;
                count -= used;
                k += (entry >> 8) & 15;
                if (k > 63)
                {
                    throw ImageErrors.Corrupt("The JPEG image data has a coefficient outside its block.");
                }

                block[zigZag[k++]] = (short)(entry >> 16);
                continue;
            }

            bits.Store(buffer, count, position);
            int rs = ac.Decode(bits);
            (buffer, count, position) = bits.State;
            int run = rs >> 4;
            int size = rs & 15;
            if (size == 0)
            {
                if (run != 15)
                {
                    break;
                }

                k += 16;
                continue;
            }

            k += run;
            if (k > 63 || size > 10)
            {
                throw ImageErrors.Corrupt("The JPEG image data has a coefficient outside its block.");
            }

            // At most 16 bits went on the code, so 16 or more remain for the value's 10.
            if (count < size)
            {
                JpegBitReader.Refill(ref buffer, ref count, ref position, data, length);
            }

            int value = (int)(buffer >> (64 - size));
            buffer <<= size;
            count -= size;
            block[zigZag[k++]] = (short)(value < (1 << (size - 1)) ? value - (1 << size) + 1 : value);
        }

        bits.Store(buffer, count, position);
    }

    private static int DecodeDcDifference(JpegBitReader bits, JpegHuffman dc)
    {
        int size = dc.Decode(bits);
        if (size > 11)
        {
            throw ImageErrors.Corrupt("The JPEG image data has a DC difference of more than 11 bits.");
        }

        return size == 0 ? 0 : bits.ReadSigned(size);
    }

    private void DecodeDcFirst(JpegBitReader bits, JpegComponent component, Span<short> block)
    {
        component.DcPredictor += DecodeDcDifference(bits, component.Dc!);
        block[0] = (short)(component.DcPredictor << _low);
    }

    private void DecodeAcFirst(JpegBitReader bits, JpegComponent component, Span<short> block)
    {
        if (_endOfBandRun > 0)
        {
            _endOfBandRun--;
            return;
        }

        JpegHuffman ac = component.Ac!;
        for (int k = _start; k <= _end;)
        {
            int rs = ac.Decode(bits);
            int run = rs >> 4;
            int size = rs & 15;
            if (size == 0)
            {
                if (run < 15)
                {
                    // EOBn: this block and 2^run + more bits - 1 after it end here (G.1.2.2).
                    _endOfBandRun = (1 << run) - 1 + bits.Read(run);
                    break;
                }

                k += 16;
                continue;
            }

            k += run;
            if (k > _end || size > 10)
            {
                throw ImageErrors.Corrupt("The JPEG image data has a coefficient outside its spectral band.");
            }

            block[ZigZag[k++]] = (short)(bits.ReadSigned(size) * (1 << _low));
        }
    }

    // G.1.2.3: each symbol places one new coefficient of ±1 << Al after skipping `run` zero-history coefficients, and
    // every coefficient already non-zero that it passes gets one correction bit.
    private void DecodeAcRefine(JpegBitReader bits, JpegComponent component, Span<short> block)
    {
        int plus = 1 << _low;
        int minus = -1 << _low;
        int k = _start;
        if (_endOfBandRun == 0)
        {
            JpegHuffman ac = component.Ac!;
            while (k <= _end)
            {
                int rs = ac.Decode(bits);
                int run = rs >> 4;
                int size = rs & 15;
                int value = 0;
                if (size == 0)
                {
                    if (run < 15)
                    {
                        _endOfBandRun = (1 << run) + bits.Read(run);
                        break;
                    }
                }
                else if (size == 1)
                {
                    value = bits.ReadBit() ? plus : minus;
                }
                else
                {
                    throw ImageErrors.Corrupt("The JPEG refinement scan has a coefficient of more than one bit.");
                }

                while (k <= _end)
                {
                    if (block[ZigZag[k]] != 0)
                    {
                        Correct(bits, ref block[ZigZag[k]], plus, minus);
                    }
                    else
                    {
                        if (run == 0)
                        {
                            if (value != 0)
                            {
                                block[ZigZag[k]] = (short)value;
                            }

                            k++;
                            break;
                        }

                        run--;
                    }

                    k++;
                }
            }
        }

        if (_endOfBandRun > 0)
        {
            // The rest of the band has no new coefficients, only corrections to those already there.
            for (; k <= _end; k++)
            {
                if (block[ZigZag[k]] != 0)
                {
                    Correct(bits, ref block[ZigZag[k]], plus, minus);
                }
            }

            _endOfBandRun--;
        }
    }

    private static void Correct(JpegBitReader bits, ref short coefficient, int plus, int minus)
    {
        if (bits.ReadBit() && (coefficient & plus) == 0)
        {
            coefficient += (short)(coefficient >= 0 ? plus : minus);
        }
    }
}
