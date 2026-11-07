using System;
using System.Threading;

namespace Zorg.Imaging;

/// <summary>Process-wide defaults, which per-call options override.</summary>
/// <example><code>ImageDefaults.MaxDegreeOfParallelism = 1; // one core per request, for a busy server</code></example>
public static class ImageDefaults
{
    private static int _maxDegreeOfParallelism = Environment.ProcessorCount;

    /// <summary>
    /// How many cores one decode, resize or encode may use at once. Defaults to <see cref="Environment.ProcessorCount"/>,
    /// which .NET caps to a container's CPU limit (and <c>DOTNET_PROCESSOR_COUNT</c> overrides).
    /// </summary>
    /// <remarks>
    /// <para>
    /// 1 keeps all work on the calling thread: the right setting for a server that already handles one request per core,
    /// where more threads per request only add contention. Larger values split big images into bands of rows that run
    /// on the .NET thread pool; small images use one thread whatever the setting, so they never pay for threads.
    /// </para>
    /// <para>
    /// <see cref="DecodeOptions.MaxDegreeOfParallelism"/>, <see cref="JpegEncoderOptions.MaxDegreeOfParallelism"/> and
    /// <see cref="ImagePipeline.WithMaxDegreeOfParallelism"/> override it for one call. Results are the same, byte for
    /// byte, whatever the setting.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    /// <example><code>ImageDefaults.MaxDegreeOfParallelism = Environment.ProcessorCount / 2;</code></example>
    public static int MaxDegreeOfParallelism
    {
        get => Volatile.Read(ref _maxDegreeOfParallelism);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            Volatile.Write(ref _maxDegreeOfParallelism, value);
        }
    }
}
