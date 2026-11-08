using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using NetVips;
using VipsImage = NetVips.Image;

namespace Zorg.Imaging.Benchmarks;

/// <summary>
/// The pipeline's main use done by libvips through NetVips: the engine behind sharp, and the design the pipeline follows
/// (<c>thumbnail</c> loads and resizes in one step so it can shrink on load). A native baseline in the same process.
/// libvips keeps its own operation cache and thread pool; the cache is turned off so every run does the work.
/// </summary>
[MemoryDiagnoser]
public class NetVipsBenchmarks
{
    private byte[] _jpeg = [];

    [GlobalSetup]
    public void Setup()
    {
        string folder = Environment.GetEnvironmentVariable("ZORG_BENCH_JPEG_DIR")
            ?? throw new InvalidOperationException("Set ZORG_BENCH_JPEG_DIR; see scripts/make-bench-files.sh.");
        _jpeg = File.ReadAllBytes(Path.Combine(folder, "photo.jpg"));
        Cache.Max = 0;

        // libvips sizes its pool from the machine, not from DOTNET_PROCESSOR_COUNT, so follow .NET's count here.
        NetVips.NetVips.Concurrency = Environment.ProcessorCount;
    }

    /// <summary>A 400-pixel thumbnail into memory: shrink-on-load in libjpeg-turbo, then libvips' own resize.</summary>
    [Benchmark]
    public int Vips_Thumbnail_Baseline()
    {
        using VipsImage thumb = VipsImage.ThumbnailBuffer(_jpeg, 400, height: 400);
        using VipsImage copy = thumb.CopyMemory();
        return copy.Width;
    }

    /// <summary>The thumbnail above, encoded by libwebp at quality 80.</summary>
    [Benchmark]
    public int Vips_Thumbnail_JpegToWebp()
    {
        using VipsImage thumb = VipsImage.ThumbnailBuffer(_jpeg, 400, height: 400);
        byte[] webp = thumb.WebpsaveBuffer(q: 80);
        return webp.Length;
    }

    /// <summary>A 1600-pixel web size, re-encoded as JPEG 80.</summary>
    [Benchmark]
    public int Vips_Web_JpegToJpeg()
    {
        using VipsImage web = VipsImage.ThumbnailBuffer(_jpeg, 1600, height: 1600);
        byte[] jpeg = web.JpegsaveBuffer(q: 80);
        return jpeg.Length;
    }
}
