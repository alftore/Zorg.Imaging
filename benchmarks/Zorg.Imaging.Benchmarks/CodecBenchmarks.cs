using System;
using System.IO;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;

namespace Zorg.Imaging.Benchmarks;

/// <summary>
/// Every codec on the same 12-megapixel photo, and the pipeline's thumbnail and web sizes. The files aren't in the
/// repository: <c>scripts/make-bench-files.sh</c> makes them, with the native tools' times beside them, and
/// <c>ZORG_BENCH_JPEG_DIR</c> names their folder.
/// </summary>
[MemoryDiagnoser]
public class CodecBenchmarks
{
    private byte[] _baseline = [];
    private byte[] _progressive = [];
    private Image _decoded = null!;
    private byte[] _lossless = [];
    private byte[] _lossy = [];
    private byte[] _png = [];

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
        string lossy = Path.Combine(folder, "photo-lossy.webp");
        _lossy = File.Exists(lossy) ? File.ReadAllBytes(lossy) : [];
        string png = Path.Combine(folder, "photo.png");
        _png = File.Exists(png) ? File.ReadAllBytes(png) : [];
    }

    [Benchmark]
    public int Decode_Png()
    {
        if (_png.Length == 0)
        {
            return 0;
        }

        using Image image = Image.Decode(_png);
        return image.Width;
    }

    [Benchmark]
    public int Encode_Png()
    {
        using var output = new MemoryStream();
        _decoded.SaveAsPng(output);
        return (int)output.Length;
    }

    /// <summary>A lossy WebP of the same photo (<c>photo-lossy.webp</c>, from <c>cwebp -q 80</c>), when present.</summary>
    [Benchmark]
    public int Decode_WebpLossy()
    {
        if (_lossy.Length == 0)
        {
            return 0;
        }

        using Image image = Image.Decode(_lossy);
        return image.Width;
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
