using System;
using Xunit;
using Zorg.Imaging.Processing;

namespace Zorg.Imaging.Tests.Processing;

/// <summary>Writing ARGB words to image rows: the vector path against the scalar one.</summary>
public class ArgbTests
{
    [Theory]
    [InlineData(PixelFormat.Rgb24, false)]
    [InlineData(PixelFormat.Rgba32, false)]
    [InlineData(PixelFormat.Rgba32, true)]
    [InlineData(PixelFormat.Bgra32, false)]
    [InlineData(PixelFormat.Bgra32, true)]
    [InlineData(PixelFormat.Rgba64, true)]
    public void Vector_and_scalar_rows_are_the_same(PixelFormat format, bool hasAlpha)
    {
        var random = new Random(7);
        int bytesPerPixel = format switch { PixelFormat.Rgb24 => 3, PixelFormat.Rgba64 => 8, _ => 4 };
        for (int width = 1; width < 40; width++)
        {
            uint[] row = new uint[width];
            for (int x = 0; x < width; x++)
            {
                row[x] = (uint)random.NextInt64(1L << 32);
            }

            byte[] scalar = new byte[width * bytesPerPixel];
            byte[] vector = new byte[width * bytesPerPixel];
            Argb.WriteRow(row, scalar, format, hasAlpha, useVectors: false);
            Argb.WriteRow(row, vector, format, hasAlpha, useVectors: true);
            Assert.Equal(scalar, vector);
        }
    }
}
