using System;
using System.Threading.Tasks;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Codecs.Jpeg;

/// <summary>
/// Streams a baseline scan that carries every component: coefficients are decoded a band of rows at a time into a
/// small buffer and transformed into the planes straight away, so the whole frame's coefficients (two bytes for
/// every sample, 36 MB for a 12-megapixel 4:2:0 photo) are never held at once.
/// </summary>
/// <remarks>
/// A band is about 512 KiB of coefficients, small enough to stay in cache between decoding and transforming. With
/// more than one core, two sets of buffers alternate: the inverse DCT of one band runs on the thread pool while the
/// next band is decoded, which overlaps the two phases the whole-frame path runs one after the other. Each block is
/// transformed exactly as the whole-frame path transforms it, so the output is the same.
/// </remarks>
internal sealed class JpegStream : IDisposable
{
    /// <summary>
    /// The coefficient bytes a band aims at. Tests make it tiny to run many bands on small files; every size gives the
    /// same output.
    /// </summary>
    internal static int BandBytes { get; set; } = 512 * 1024;

    private readonly JpegComponent[] _components;
    private readonly JpegOutput.Plane[] _planes;
    private readonly int[] _rowsPerUnit;
    private readonly short[][][] _buffers;
    private readonly Task?[] _pending;
    private int _set = -1;
    private int _unit;

    /// <param name="frame">The frame, whose components the scan carries, all of them.</param>
    /// <param name="planes">One plane for each of the frame's components, in the frame's order.</param>
    /// <param name="interleaved">
    /// Whether the scan is interleaved: then a unit is an MCU row, V block rows of each component; otherwise (one
    /// component) a unit is one block row.
    /// </param>
    public JpegStream(JpegFrame frame, JpegOutput.Plane[] planes, bool interleaved)
    {
        _components = frame.Components;
        _planes = planes;
        _rowsPerUnit = new int[_components.Length];
        long bytesPerUnit = 0;
        for (int i = 0; i < _components.Length; i++)
        {
            _rowsPerUnit[i] = interleaved ? _components[i].V : 1;
            bytesPerUnit += (long)_components[i].BlocksWide * _rowsPerUnit[i] * 64 * sizeof(short);
        }

        int units = interleaved ? frame.McusHigh : _components[0].UsedHigh;
        UnitsPerBand = (int)Math.Clamp(BandBytes / bytesPerUnit, 1, Math.Max(units, 1));

        int sets = Parallelism.Degree > 1 ? 2 : 1;
        _pending = new Task?[sets];
        _buffers = new short[sets][][];
        for (int set = 0; set < sets; set++)
        {
            _buffers[set] = new short[_components.Length][];
            for (int i = 0; i < _components.Length; i++)
            {
                _buffers[set][i] = new short[(long)_components[i].BlocksWide * _rowsPerUnit[i] * UnitsPerBand * 64];
            }
        }
    }

    /// <summary>How many units (MCU rows, or block rows of the one component) each band holds.</summary>
    public int UnitsPerBand { get; }

    /// <summary>Points every component at a cleared buffer for the band that starts at <paramref name="unit"/>.</summary>
    public void Begin(int unit)
    {
        _set = (_set + 1) % _buffers.Length;
        Wait(_set);
        _unit = unit;
        for (int i = 0; i < _components.Length; i++)
        {
            short[] buffer = _buffers[_set][i];

            // The decoder writes only the coefficients that aren't zero, so the buffer starts clear, as a new one would.
            Array.Clear(buffer);
            _components[i].Coefficients = buffer;
            _components[i].FirstRow = unit * _rowsPerUnit[i];
        }
    }

    /// <summary>Transforms the band begun last, of <paramref name="units"/> units, on the thread pool when there is one.</summary>
    public void End(int units)
    {
        short[][] buffers = _buffers[_set];
        int unit = _unit;
        void Transform()
        {
            for (int i = 0; i < _components.Length; i++)
            {
                int first = unit * _rowsPerUnit[i];
                _planes[i].Transform(_components[i], buffers[i], first, first, units * _rowsPerUnit[i]);
            }
        }

        if (_pending.Length > 1)
        {
            _pending[_set] = Task.Run(Transform);
        }
        else
        {
            Transform();
        }
    }

    /// <summary>Waits for the last bands' transforms; afterwards the planes are complete.</summary>
    public void Finish()
    {
        for (int set = 0; set < _pending.Length; set++)
        {
            Wait(set);
        }
    }

    /// <summary>Waits for any transform still running, so the planes can be returned safely after a failure.</summary>
    public void Dispose()
    {
        foreach (Task? task in _pending)
        {
            try
            {
                task?.Wait();
            }
            catch (AggregateException)
            {
                // Already failing: the decode's own exception is the one that matters.
            }
        }
    }

    private void Wait(int set)
    {
        if (_pending[set] is { } task)
        {
            _pending[set] = null;
            task.GetAwaiter().GetResult();
        }
    }
}
