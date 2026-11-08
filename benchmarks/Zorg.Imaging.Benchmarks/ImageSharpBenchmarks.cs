using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SharpRgb24 = SixLabors.ImageSharp.PixelFormats.Rgb24;
using SixLabors.ImageSharp.Processing;
using Sharp = SixLabors.ImageSharp;

namespace Zorg.Imaging.Benchmarks;

/// <summary>
/// The same work as <see cref="CodecBenchmarks"/>, done by ImageSharp 2.1, the last release under Apache-2.0, as a
/// managed baseline. Its package has no .NET 5+ build, so .NET 10 loads its netcoreapp3.1 build: no Vector128 or AVX-512
/// paths written for newer runtimes. 2.1 has no shrink-on-load, so its thumbnail decodes the whole photo.
/// </summary>
[MemoryDiagnoser]
public class ImageSharpBenchmarks
{
    private byte[] _jpeg = [];
    private byte[] _progressive = [];
    private byte[] _png = [];
    private byte[] _gif = [];
    private byte[] _lossy = [];
    private byte[] _lossless = [];
    private Sharp.Image<SharpRgb24> _decoded = null!;

    [GlobalSetup]
    public void Setup()
    {
        string folder = Environment.GetEnvironmentVariable("ZORG_BENCH_JPEG_DIR")
            ?? throw new InvalidOperationException("Set ZORG_BENCH_JPEG_DIR; see scripts/make-bench-files.sh.");
        _jpeg = File.ReadAllBytes(Path.Combine(folder, "photo.jpg"));
        _progressive = File.ReadAllBytes(Path.Combine(folder, "photo-progressive.jpg"));
        _png = File.ReadAllBytes(Path.Combine(folder, "photo.png"));
        _gif = File.ReadAllBytes(Path.Combine(folder, "photo.gif"));
        _lossy = File.ReadAllBytes(Path.Combine(folder, "photo-lossy.webp"));
        _lossless = File.ReadAllBytes(Path.Combine(folder, "photo-lossless.webp"));
        _decoded = Sharp.Image.Load<SharpRgb24>(_jpeg);
    }

    [GlobalCleanup]
    public void Cleanup() => _decoded.Dispose();

    [Benchmark]
    public int Sharp_Decode_Baseline() => Decode(_jpeg);

    [Benchmark]
    public int Sharp_Decode_Progressive() => Decode(_progressive);

    [Benchmark]
    public int Sharp_Decode_Png() => Decode(_png);

    [Benchmark]
    public int Sharp_Decode_Gif() => Decode(_gif);

    [Benchmark]
    public int Sharp_Decode_WebpLossy() => Decode(_lossy);

    [Benchmark]
    public int Sharp_Decode_WebpLossless() => Decode(_lossless);

    [Benchmark]
    public long Sharp_Encode_Jpeg80()
    {
        using var output = new MemoryStream();
        _decoded.Save(output, new JpegEncoder { Quality = 80, ColorType = JpegColorType.YCbCrRatio420 });
        return output.Length;
    }

    [Benchmark]
    public long Sharp_Encode_Gif()
    {
        using var output = new MemoryStream();
        _decoded.Save(output, new SixLabors.ImageSharp.Formats.Gif.GifEncoder());
        return output.Length;
    }

    [Benchmark]
    public long Sharp_Encode_Png()
    {
        using var output = new MemoryStream();
        _decoded.Save(output, new PngEncoder());
        return output.Length;
    }

    [Benchmark]
    public long Sharp_Encode_WebpLossy80()
    {
        using var output = new MemoryStream();
        _decoded.Save(output, new WebpEncoder { FileFormat = WebpFileFormatType.Lossy, Quality = 80 });
        return output.Length;
    }

    [Benchmark]
    public long Sharp_Encode_WebpLossless()
    {
        using var output = new MemoryStream();
        _decoded.Save(output, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless });
        return output.Length;
    }

    /// <summary>A 400-pixel thumbnail: a full decode (2.1 cannot shrink on load), then a Mitchell resize.</summary>
    [Benchmark]
    public int Sharp_Thumbnail_Baseline()
    {
        using Sharp.Image<SharpRgb24> image = Sharp.Image.Load<SharpRgb24>(_jpeg);
        image.Mutate(x => x.Resize(400, 300, KnownResamplers.MitchellNetravali));
        return image.Width;
    }

    /// <summary>A 1600-pixel web size from the full decode, Mitchell, re-encoded as JPEG 80.</summary>
    [Benchmark]
    public long Sharp_Web_JpegToJpeg()
    {
        using Sharp.Image<SharpRgb24> image = Sharp.Image.Load<SharpRgb24>(_jpeg);
        image.Mutate(x => x.Resize(1600, 1200, KnownResamplers.MitchellNetravali));
        using var output = new MemoryStream();
        image.Save(output, new JpegEncoder { Quality = 80, ColorType = JpegColorType.YCbCrRatio420 });
        return output.Length;
    }

    private static int Decode(byte[] file)
    {
        using Sharp.Image<SharpRgb24> image = Sharp.Image.Load<SharpRgb24>(file);
        return image.Width;
    }
}
