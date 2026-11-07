using System;
using System.Threading;
using System.Threading.Tasks;

namespace Zorg.Imaging.Processing;

/// <summary>
/// How many cores the current call may use: a per-call override (from options or a pipeline) that flows through async
/// calls, else <see cref="ImageDefaults.MaxDegreeOfParallelism"/>.
/// </summary>
internal static class Parallelism
{
    /// <summary>Below this much work (pixels, or the like), threads cost more than they save.</summary>
    public const long Threshold = 1 << 18;

    private static readonly AsyncLocal<int> CallOverride = new();

    public static int Degree => CallOverride.Value > 0 ? CallOverride.Value : ImageDefaults.MaxDegreeOfParallelism;

    /// <summary>Sets the degree for the current call and what it calls, until disposed; 0 leaves it as it is.</summary>
    public static Scope Use(int degree)
    {
        int previous = CallOverride.Value;
        if (degree > 0)
        {
            CallOverride.Value = degree;
        }

        return new Scope(previous, degree > 0);
    }

    /// <summary>How many bands to split work into: 1 for small work, else up to the degree and <paramref name="most"/>.</summary>
    public static int Bands(long work, int most) => work < Threshold ? 1 : Math.Clamp(Math.Min(Degree, most), 1, 64);

    /// <summary>Runs <paramref name="body"/> for each band, on the calling thread alone when there is one.</summary>
    public static void For(int bands, Action<int> body)
    {
        if (bands <= 1)
        {
            body(0);
            return;
        }

        Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(bands, Degree) }, body);
    }

    public readonly struct Scope(int previous, bool changed) : IDisposable
    {
        public void Dispose()
        {
            if (changed)
            {
                CallOverride.Value = previous;
            }
        }
    }
}
