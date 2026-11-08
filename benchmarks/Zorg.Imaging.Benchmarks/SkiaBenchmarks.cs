using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using SkiaSharp;

namespace Zorg.Imaging.Benchmarks;

/// <summary>
/// The same work as <see cref="CodecBenchmarks"/>, done by SkiaSharp (Skia with libjpeg-turbo, libpng, libwebp and
/// zlib underneath), as a native baseline in the same process. Skia decodes to 32-bit BGRA; ours decodes to the file's
/// own layout (24-bit RGB for these photos), so the comparison slightly favours us on memory traffic.
/// </summary>
[MemoryDiagnoser]
public class SkiaBenchmarks
{
    private byte[] _jpeg = [];
    private byte[] _progressive = [];
    private byte[] _png = [];
    private byte[] _lossy = [];
    private byte[] _lossless = [];
    private SKBitmap _decoded = null!;

    [GlobalSetup]
    public void Setup()
    {
        string folder = Environment.GetEnvironmentVariable("ZORG_BENCH_JPEG_DIR")
            ?? throw new InvalidOperationException("Set ZORG_BENCH_JPEG_DIR; see scripts/make-bench-files.sh.");
        _jpeg = File.ReadAllBytes(Path.Combine(folder, "photo.jpg"));
        _progressive = File.ReadAllBytes(Path.Combine(folder, "photo-progressive.jpg"));
        _png = File.ReadAllBytes(Path.Combine(folder, "photo.png"));
        _lossy = File.ReadAllBytes(Path.Combine(folder, "photo-lossy.webp"));
        _lossless = File.ReadAllBytes(Path.Combine(folder, "photo-lossless.webp"));
        _decoded = SKBitmap.Decode(_jpeg);
    }

    [GlobalCleanup]
    public void Cleanup() => _decoded.Dispose();

    [Benchmark]
    public int Skia_Decode_Baseline() => Decode(_jpeg);

    [Benchmark]
    public int Skia_Decode_Progressive() => Decode(_progressive);

    [Benchmark]
    public int Skia_Decode_Png() => Decode(_png);

    [Benchmark]
    public int Skia_Decode_WebpLossy() => Decode(_lossy);

    [Benchmark]
    public int Skia_Decode_WebpLossless() => Decode(_lossless);

    [Benchmark]
    public int Skia_Encode_Jpeg80()
    {
        using SKData data = _decoded.Encode(SKEncodedImageFormat.Jpeg, 80);
        return (int)data.Size;
    }

    [Benchmark]
    public int Skia_Encode_Png()
    {
        using SKData data = _decoded.Encode(SKEncodedImageFormat.Png, 100);
        return (int)data.Size;
    }

    /// <summary>A 400-pixel thumbnail: libjpeg-turbo's scaled decode at 1/4 (as ours shrinks on load), then a Mitchell resize.</summary>
    [Benchmark]
    public int Skia_Thumbnail_Baseline()
    {
        using var codec = SKCodec.Create(new MemoryStream(_jpeg));
        SKSizeI scaled = codec.GetScaledDimensions(0.25f);
        var info = new SKImageInfo(scaled.Width, scaled.Height);
        using var bitmap = new SKBitmap(info);
        codec.GetPixels(info, bitmap.GetPixels());
        using SKBitmap thumb = bitmap.Resize(new SKImageInfo(400, 300), new SKSamplingOptions(SKCubicResampler.Mitchell));
        return thumb.Width;
    }

    /// <summary>A 1600-pixel web size from the full decode, Mitchell, re-encoded as JPEG 80.</summary>
    [Benchmark]
    public int Skia_Web_JpegToJpeg()
    {
        using SKBitmap full = SKBitmap.Decode(_jpeg);
        using SKBitmap web = full.Resize(new SKImageInfo(1600, 1200), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using SKData data = web.Encode(SKEncodedImageFormat.Jpeg, 80);
        return (int)data.Size;
    }

    private static int Decode(byte[] file)
    {
        using SKBitmap bitmap = SKBitmap.Decode(file);
        return bitmap.Width;
    }
}
