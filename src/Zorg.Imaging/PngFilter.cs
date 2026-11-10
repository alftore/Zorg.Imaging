namespace Zorg.Imaging;

/// <summary>
/// How the PNG encoder picks each row's filter, the prediction that turns pixels into small differences before
/// compression. Every choice is lossless; they differ only in file size and speed.
/// </summary>
/// <example><code>var options = new PngEncoderOptions { Filter = PngFilter.Entropy };</code></example>
public enum PngFilter
{
    /// <summary>
    /// <see cref="None"/> for palette images and grey packed below 8 bits, where prediction seldom helps; else, for each
    /// band of about a megabyte of rows, whichever of <see cref="None"/> (if clearly smallest), <see cref="Sub"/>,
    /// <see cref="Up"/>, <see cref="Paeth"/> and <see cref="MinimumSum"/> compresses a sample of its rows smallest. The
    /// default.
    /// </summary>
    /// <example><code>var options = new PngEncoderOptions { Filter = PngFilter.Adaptive };</code></example>
    Adaptive,

    /// <summary>No prediction: rows are stored as they are.</summary>
    /// <example><code>var options = new PngEncoderOptions { Filter = PngFilter.None };</code></example>
    None,

    /// <summary>Every row predicted from the pixel to its left.</summary>
    /// <example><code>var options = new PngEncoderOptions { Filter = PngFilter.Sub };</code></example>
    Sub,

    /// <summary>Every row predicted from the pixel above.</summary>
    /// <example><code>var options = new PngEncoderOptions { Filter = PngFilter.Up };</code></example>
    Up,

    /// <summary>Every row predicted from the mean of the pixels to the left and above.</summary>
    /// <example><code>var options = new PngEncoderOptions { Filter = PngFilter.Average };</code></example>
    Average,

    /// <summary>Every row predicted by Paeth's rule from the left, upper and upper-left pixels.</summary>
    /// <example><code>var options = new PngEncoderOptions { Filter = PngFilter.Paeth };</code></example>
    Paeth,

    /// <summary>
    /// Each row gets the filter whose output has the smallest sum of absolute values, read as signed bytes: the
    /// heuristic the PNG specification suggests.
    /// </summary>
    /// <example><code>var options = new PngEncoderOptions { Filter = PngFilter.MinimumSum };</code></example>
    MinimumSum,

    /// <summary>Each row gets the filter whose output has the lowest Shannon entropy, byte by byte.</summary>
    /// <example><code>var options = new PngEncoderOptions { Filter = PngFilter.Entropy };</code></example>
    Entropy,

    /// <summary>Each row gets the filter whose output has the fewest distinct pairs of neighbouring bytes.</summary>
    /// <example><code>var options = new PngEncoderOptions { Filter = PngFilter.Bigrams };</code></example>
    Bigrams,

    /// <summary>Each row gets the filter whose output has the lowest entropy over pairs of neighbouring bytes.</summary>
    /// <example><code>var options = new PngEncoderOptions { Filter = PngFilter.BigramEntropy };</code></example>
    BigramEntropy,
}
