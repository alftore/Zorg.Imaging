using System;
using BenchmarkDotNet.Attributes;

namespace Zorg.Imaging.Benchmarks;

/// <summary>A 12-megapixel phone photo made into thumbnails and web sizes, and a small image enlarged.</summary>
[MemoryDiagnoser]
public class ResizeBenchmarks
{
    private Image _photo = null!;
    private Image _photo16 = null!;
    private Image _photoRgb = null!;
    private Image _small = null!;

    [GlobalSetup]
    public void Setup()
    {
        _photo = Noise(4000, 3000, PixelFormat.Rgba32);
        _photo16 = Noise(4000, 3000, PixelFormat.Rgba64);
        _photoRgb = Noise(4000, 3000, PixelFormat.Rgb24);
        _small = Noise(400, 300, PixelFormat.Rgba32);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _photo.Dispose();
        _photo16.Dispose();
        _photoRgb.Dispose();
        _small.Dispose();
    }

    [Benchmark]
    public int Thumbnail_Rgba32_Lanczos3() => Run(_photo, 400, 300, Resampler.Lanczos3);

    [Benchmark]
    public int Web_Rgba32_Lanczos3() => Run(_photo, 1600, 1200, Resampler.Lanczos3);

    [Benchmark]
    public int Web_Rgb24_Lanczos3() => Run(_photoRgb, 1600, 1200, Resampler.Lanczos3);

    [Benchmark]
    public int Web_Rgba64_Lanczos3() => Run(_photo16, 1600, 1200, Resampler.Lanczos3);

    [Benchmark]
    public int Web_Rgba32_Bilinear() => Run(_photo, 1600, 1200, Resampler.Bilinear);

    [Benchmark]
    public int Enlarge_Rgba32_Mitchell() => Run(_small, 1200, 900, Resampler.Mitchell);

    private static int Run(Image image, int width, int height, Resampler resampler)
    {
        using Image result = image.Resize(width, height, resampler);
        return result.GetPixelBytes()[0];
    }

    private static Image Noise(int width, int height, PixelFormat format)
    {
        var image = new Image(width, height, format);
        new Random(width).NextBytes(image.GetPixelBytes());
        return image;
    }
}
