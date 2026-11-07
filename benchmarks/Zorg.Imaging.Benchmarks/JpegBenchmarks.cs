using System;
using System.IO;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace Zorg.Imaging.Benchmarks;

/// <summary>
/// Decoding a 12-megapixel phone-sized JPEG, whole and as a thumbnail through the pipeline (shrink-on-load). The files
/// aren't in the repository: set <c>ZORG_BENCH_JPEG_DIR</c> to a folder with <c>photo.jpg</c> (baseline) and
/// <c>photo-progressive.jpg</c>, 4000 x 3000, for example made with ImageMagick and cjpeg in the gate image.
/// </summary>
[MemoryDiagnoser]
public class JpegBenchmarks
{
    private byte[] _baseline = [];
    private byte[] _progressive = [];
    private Image _decoded = null!;
    private byte[] _lossless = [];

    [GlobalSetup]
    public void Setup()
    {
        string folder = Environment.GetEnvironmentVariable("ZORG_BENCH_JPEG_DIR")
            ?? throw new InvalidOperationException("Set ZORG_BENCH_JPEG_DIR to a folder with photo.jpg and photo-progressive.jpg.");
        _baseline = File.ReadAllBytes(Path.Combine(folder, "photo.jpg"));
        _progressive = File.ReadAllBytes(Path.Combine(folder, "photo-progressive.jpg"));
        _decoded = Image.Decode(_baseline);
        string lossless = Path.Combine(folder, "photo-lossless.webp");
        _lossless = File.Exists(lossless) ? File.ReadAllBytes(lossless) : [];
    }

    /// <summary>A lossless WebP of the same photo (<c>photo-lossless.webp</c>, from <c>cwebp -lossless -z 6</c>), when present.</summary>
    [Benchmark]
    public int Decode_WebpLossless()
    {
        if (_lossless.Length == 0)
        {
            return 0;
        }

        using Image image = Image.Decode(_lossless);
        return image.Width;
    }

    [GlobalCleanup]
    public void Cleanup() => _decoded.Dispose();

    [Benchmark]
    public int Encode_Quality80()
    {
        using var output = new MemoryStream();
        _decoded.SaveAsJpeg(output);
        return (int)output.Length;
    }

    [Benchmark]
    public async Task<int> Web_JpegToJpeg()
    {
        using var output = new MemoryStream();
        await ImagePipeline.Load(_baseline).Fit(1600, 1600).SaveAsJpegAsync(output);
        return (int)output.Length;
    }

    [Benchmark]
    public int Decode_Baseline()
    {
        using Image image = Image.Decode(_baseline);
        return image.Width;
    }

    [Benchmark]
    public int Decode_Progressive()
    {
        using Image image = Image.Decode(_progressive);
        return image.Width;
    }

    [Benchmark]
    public async Task<int> Thumbnail_Baseline()
    {
        using Image image = await ImagePipeline.Load(_baseline).Fit(400, 400).ToImageAsync();
        return image.Width;
    }

    [Benchmark]
    public async Task<int> Web_Baseline()
    {
        using Image image = await ImagePipeline.Load(_baseline).Fit(1600, 1600).ToImageAsync();
        return image.Width;
    }
}
