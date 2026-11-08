using System;

namespace Zorg.Imaging.Processing;

/// <summary>
/// Turns linear light (0 to 1) into 8-bit or 16-bit codes of a transfer curve, rounding in the encoded values, with
/// no power function per sample.
/// </summary>
/// <remarks>
/// Code <c>k</c> is right when the value lies between the linear values of <c>k - 0.5</c> and <c>k + 0.5</c>, so the
/// code is the number of those midpoints at or below the value. The midpoints are sorted into buckets of equal linear
/// width; a value's bucket bounds the count, and a short binary search inside it finds the exact one.
/// </remarks>
internal sealed class Quantizer
{
    private readonly float[] _midpoints;
    private readonly int[] _first;
    private readonly int _buckets;

    // For 8-bit codes whose every step is wider than 1/16384 in linear light (sRGB's narrowest, near black, is about
    // five times that; a pure gamma curve's aren't): the code at the start of each of 16384 buckets. A value is that
    // code or the next, and one comparison with the next midpoint says which.
    private readonly byte[]? _start8;

    public Quantizer(TransferFunction transfer, int max)
    {
        _midpoints = new float[max];
        for (int k = 0; k < max; k++)
        {
            _midpoints[k] = (float)transfer.ToLinear((k + 0.5) / max);
        }

        // _first[b]: how many midpoints fall in buckets before b. The bucket of a midpoint is found exactly as Encode
        // finds a value's, so float rounding can't put the answer outside [_first[b], _first[b + 1]].
        _buckets = max == 255 ? 4096 : 65536;
        _first = new int[_buckets + 1];
        foreach (float midpoint in _midpoints)
        {
            _first[Bucket(midpoint) + 1]++;
        }

        for (int b = 1; b <= _buckets; b++)
        {
            _first[b] += _first[b - 1];
        }

        if (max == 255 && NarrowestCode() > 1.0 / Narrow)
        {
            _start8 = new byte[Narrow];
            for (int b = 0; b < Narrow; b++)
            {
                _start8[b] = (byte)Search((float)b / Narrow);
            }
        }
    }

    private const int Narrow = 16384;

    // The smallest distance between neighbouring midpoints, counting 0 as the first.
    private double NarrowestCode()
    {
        double narrowest = _midpoints[0];
        for (int k = 1; k < _midpoints.Length; k++)
        {
            narrowest = Math.Min(narrowest, _midpoints[k] - _midpoints[k - 1]);
        }

        return narrowest;
    }

    /// <summary>The code for a linear value; below 0 (or NaN) is 0, above 1 the top code.</summary>
    public int Encode(float value)
    {
        if (!(value > 0))
        {
            return 0;
        }

        if (_start8 is { } start)
        {
            if (value >= 1)
            {
                return 255;
            }

            int code = start[(int)(value * Narrow)];
            return code < 255 && _midpoints[code] <= value ? code + 1 : code;
        }

        return Search(value);
    }

    private int Search(float value)
    {
        if (!(value > 0))
        {
            return 0;
        }

        int b = Bucket(value);
        int low = _first[b];
        int high = _first[b + 1];
        while (low < high)
        {
            int mid = (low + high) >> 1;
            if (_midpoints[mid] <= value)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private int Bucket(float value) => Math.Clamp((int)(value * _buckets), 0, _buckets - 1);
}
