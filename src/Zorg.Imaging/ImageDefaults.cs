using System;
using System.Threading;

namespace Zorg.Imaging;

/// <summary>Process-wide defaults, which per-call options override.</summary>
/// <example><code>ImageDefaults.MaxDegreeOfParallelism = 1; // one core per request, for a busy server</code></example>
public static class ImageDefaults
{
    private static readonly Lock CodecsLock = new();
    private static int _maxDegreeOfParallelism = Environment.ProcessorCount;
    private static ImageCodecs _codecs = ImageCodecs.Empty;
    private static bool _codecsUsed;

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
    /// <see cref="DecodeOptions.MaxDegreeOfParallelism"/>, <see cref="JpegEncoderOptions.MaxDegreeOfParallelism"/>,
    /// <see cref="PngEncoderOptions.MaxDegreeOfParallelism"/>, <see cref="WebpEncoderOptions.MaxDegreeOfParallelism"/>,
    /// <see cref="GifEncoderOptions.MaxDegreeOfParallelism"/> and
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

    /// <summary>
    /// The codecs from other packages that decoding and identifying use when the options don't name any. Set it once at
    /// start-up, before the first image is read: it can't change after a decode or identify has used it, so every call
    /// sees the same codecs. For apps; a library should pass <see cref="DecodeOptions.Codecs"/> instead.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">An image has already been read with the default codecs.</exception>
    /// <example><code>ImageDefaults.Codecs = ImageCodecs.Empty.Add(SystemCodecs.Heif, SystemCodecs.Avif);</code></example>
    public static ImageCodecs Codecs
    {
        get => Volatile.Read(ref _codecs);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (CodecsLock)
            {
                if (_codecsUsed)
                {
                    throw new InvalidOperationException("ImageDefaults.Codecs can't change after an image has been read with it. Set it at start-up.");
                }

                Volatile.Write(ref _codecs, value);
            }
        }
    }

    /// <summary>The default codecs, fixed from now on.</summary>
    /// <returns>The codecs.</returns>
    internal static ImageCodecs UseCodecs()
    {
        lock (CodecsLock)
        {
            _codecsUsed = true;
            return _codecs;
        }
    }

    /// <summary>For tests: lets the default codecs be set again.</summary>
    internal static void ResetCodecs()
    {
        lock (CodecsLock)
        {
            _codecsUsed = false;
            _codecs = ImageCodecs.Empty;
        }
    }
}
