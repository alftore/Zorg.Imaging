using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Processing;
using Zorg.Imaging.Tests.Codecs.Jpeg;
using Zorg.Imaging.Tests.Codecs.Png;

namespace Zorg.Imaging.Tests.Processing;

public class ParallelismTests
{
    [Fact]
    public async Task Results_are_the_same_on_one_core_and_on_many()
    {
        using Image photo = JpegEncoderTests.Photo(900, 700);
        byte[] png = PngEncoderTests.Save(photo);

        byte[] one = await RunAsync(png, 1);
        byte[] many = await RunAsync(png, 8);
        Assert.Equal(one, many);

        // The same through the per-call options rather than the pipeline.
        byte[] jpegOne = JpegEncoderTests.Save(photo, new JpegEncoderOptions { MaxDegreeOfParallelism = 1 });
        byte[] jpegMany = JpegEncoderTests.Save(photo, new JpegEncoderOptions { MaxDegreeOfParallelism = 16 });
        Assert.Equal(jpegOne, jpegMany);
        using Image decodedOne = Image.Decode(jpegOne, new DecodeOptions { MaxDegreeOfParallelism = 1 });
        using Image decodedMany = Image.Decode(jpegOne, new DecodeOptions { MaxDegreeOfParallelism = 16 });
        Assert.Equal(decodedOne.GetPixelBytes().ToArray(), decodedMany.GetPixelBytes().ToArray());
    }

    [Fact]
    public void One_means_the_calling_thread_and_small_work_never_splits()
    {
        using (Parallelism.Use(1))
        {
            Assert.Equal(1, Parallelism.Degree);
            Assert.Equal(1, Parallelism.Bands(100_000_000, 64));
        }

        using (Parallelism.Use(6))
        {
            Assert.Equal(6, Parallelism.Degree);
            Assert.Equal(6, Parallelism.Bands(100_000_000, 64));
            Assert.Equal(1, Parallelism.Bands(1000, 64));
            Assert.Equal(3, Parallelism.Bands(100_000_000, 3));

            // 0 leaves the current setting alone.
            using (Parallelism.Use(0))
            {
                Assert.Equal(6, Parallelism.Degree);
            }
        }
    }

    [Fact]
    public void Settings_are_checked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageDefaults.MaxDegreeOfParallelism = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodeOptions { MaxDegreeOfParallelism = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JpegEncoderOptions { MaxDegreeOfParallelism = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => ImagePipeline.Load([1]).WithMaxDegreeOfParallelism(0));
        Assert.True(ImageDefaults.MaxDegreeOfParallelism >= 1);
        Assert.Equal(0, DecodeOptions.Default.MaxDegreeOfParallelism);
    }

    private static async Task<byte[]> RunAsync(byte[] png, int cores)
    {
        using var output = new MemoryStream();
        await ImagePipeline.Load(png).WithMaxDegreeOfParallelism(cores).Fit(500, 500).SaveAsJpegAsync(output, cancellationToken: TestContext.Current.CancellationToken);
        return output.ToArray();
    }
}
