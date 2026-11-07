using System;
using System.Globalization;

namespace Zorg.Imaging;

/// <summary>
/// What an image's pixel values mean: which red, green and blue they mix (the primaries), and how a value maps to
/// brightness (the transfer function). Phone photos are often <see cref="DisplayP3"/>, which holds more saturated
/// colours than <see cref="Srgb"/>; HDR photos use <see cref="Rec2020Pq"/> or <see cref="Rec2020Hlg"/>.
/// </summary>
/// <remarks>
/// <para>
/// A colour space comes from the file: from CICP code points (ITU-T H.273, as in PNG's <c>cICP</c> chunk and in AVIF),
/// from an ICC profile, which is kept byte for byte, or from PNG's older <c>gAMA</c> and <c>cHRM</c> chunks. Encoders
/// write it back, so an image looks the same after it's saved. Colour spaces are immutable and compare by meaning.
/// </para>
/// <para>
/// An ICC profile is carried through unchanged. Reading what's inside one, and converting between colour spaces, come
/// later.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// using Image photo = Image.Decode(bytes);
/// if (photo.ColorSpace.IsHdr)
/// {
///     Console.WriteLine($"HDR photo in {photo.ColorSpace}");
/// }
/// </code>
/// </example>
public sealed class ColorSpace : IEquatable<ColorSpace>
{
    /// <summary>The largest ICC profile accepted, in bytes: 4 MiB. Real profiles are a few hundred bytes to a few hundred KiB.</summary>
    /// <example><code>bool fits = profile.Length &lt;= ColorSpace.MaxIccProfileBytes;</code></example>
    public const int MaxIccProfileBytes = 4 * 1024 * 1024;

    private readonly byte[]? _iccProfile;
    private readonly int[]? _chromaticities;

    private ColorSpace(string name, int? primaries, int? transfer, int matrix = 0, bool fullRange = true, byte[]? iccProfile = null, int pngGamma = 0, int[]? chromaticities = null)
    {
        Name = name;
        CicpPrimaries = primaries;
        CicpTransfer = transfer;
        CicpMatrix = matrix;
        CicpFullRange = fullRange;
        _iccProfile = iccProfile;
        PngGamma = pngGamma;
        _chromaticities = chromaticities;
    }

    /// <summary>sRGB (IEC 61966-2-1): the web's default, and what an image without colour information is taken to be.</summary>
    /// <example><code>image.ColorSpace = ColorSpace.Srgb;</code></example>
    public static ColorSpace Srgb { get; } = new("sRGB", 1, 13);

    /// <summary>Display P3: DCI-P3 primaries with sRGB's transfer function and white point. What most phones shoot in.</summary>
    /// <example><code>bool wide = image.ColorSpace == ColorSpace.DisplayP3;</code></example>
    public static ColorSpace DisplayP3 { get; } = new("Display P3", 12, 13);

    /// <summary>Rec. 2020 primaries with the Rec. 709 transfer function: wide-gamut SDR.</summary>
    /// <example><code>ColorSpace space = ColorSpace.Rec2020;</code></example>
    public static ColorSpace Rec2020 { get; } = new("Rec. 2020", 9, 1);

    /// <summary>Rec. 2100 PQ (SMPTE ST 2084): Rec. 2020 primaries with the perceptual quantiser. HDR.</summary>
    /// <example><code>bool pq = image.ColorSpace == ColorSpace.Rec2020Pq;</code></example>
    public static ColorSpace Rec2020Pq { get; } = new("Rec. 2100 PQ", 9, 16);

    /// <summary>Rec. 2100 HLG (hybrid log-gamma): Rec. 2020 primaries with the HLG transfer function. HDR.</summary>
    /// <example><code>bool hlg = image.ColorSpace == ColorSpace.Rec2020Hlg;</code></example>
    public static ColorSpace Rec2020Hlg { get; } = new("Rec. 2100 HLG", 9, 18);

    /// <summary>sRGB primaries with linear values: light adds up directly. Used for processing.</summary>
    /// <example><code>ColorSpace space = ColorSpace.LinearSrgb;</code></example>
    public static ColorSpace LinearSrgb { get; } = new("Linear sRGB", 1, 8);

    /// <summary>The name, such as <c>Display P3</c>, <c>CICP 9/14</c> or <c>ICC profile</c>.</summary>
    /// <example><code>Console.WriteLine(image.ColorSpace.Name);</code></example>
    public string Name { get; }

    /// <summary>The colour primaries code point from ITU-T H.273 (1 is Rec. 709 and sRGB, 9 Rec. 2020, 12 P3), or <see langword="null"/> when not known.</summary>
    /// <example><code>int? primaries = image.ColorSpace.CicpPrimaries;</code></example>
    public int? CicpPrimaries { get; }

    /// <summary>The transfer characteristics code point from ITU-T H.273 (13 is sRGB, 16 PQ, 18 HLG, 8 linear), or <see langword="null"/> when not known.</summary>
    /// <example><code>int? transfer = image.ColorSpace.CicpTransfer;</code></example>
    public int? CicpTransfer { get; }

    /// <summary>The ICC profile the file carried, byte for byte, or empty when there wasn't one.</summary>
    /// <example><code>byte[] profile = image.ColorSpace.IccProfile.ToArray();</code></example>
    public ReadOnlyMemory<byte> IccProfile => _iccProfile;

    /// <summary>Whether this is <see cref="Srgb"/>, so the image needs no colour information to show correctly.</summary>
    /// <example><code>bool plain = image.ColorSpace.IsSrgb;</code></example>
    public bool IsSrgb => Equals(Srgb);

    /// <summary>Whether the transfer function is an HDR one: PQ or HLG.</summary>
    /// <example><code>bool hdr = image.ColorSpace.IsHdr;</code></example>
    public bool IsHdr => CicpTransfer is 16 or 18;

    /// <summary>The H.273 matrix coefficients. Always 0 (RGB) except for a PNG <c>cICP</c> chunk that says otherwise.</summary>
    internal int CicpMatrix { get; }

    /// <summary>The H.273 full-range flag. Always set except for a PNG <c>cICP</c> chunk that says otherwise.</summary>
    internal bool CicpFullRange { get; }

    /// <summary>A PNG <c>gAMA</c> value (the file gamma times 100,000), or 0.</summary>
    internal int PngGamma { get; }

    /// <summary>A PNG <c>cHRM</c> chunk's eight values (white x, y, red x, y, green x, y, blue x, y, each times 100,000), or empty.</summary>
    internal ReadOnlySpan<int> PngChromaticities => _chromaticities;

    /// <summary>Whether two colour spaces mean the same thing.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns><see langword="true"/> when they're equal.</returns>
    /// <example><code>bool same = a == b;</code></example>
    public static bool operator ==(ColorSpace? left, ColorSpace? right) => left is null ? right is null : left.Equals(right);

    /// <summary>Whether two colour spaces differ.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    /// <example><code>bool changed = a != b;</code></example>
    public static bool operator !=(ColorSpace? left, ColorSpace? right) => !(left == right);

    /// <summary>The colour space for a pair of ITU-T H.273 code points, as in PNG's <c>cICP</c> chunk or an AVIF file.</summary>
    /// <param name="primaries">The colour primaries, 0 to 255.</param>
    /// <param name="transfer">The transfer characteristics, 0 to 255.</param>
    /// <returns>A built-in colour space when one matches, such as <see cref="DisplayP3"/> for 12 and 13; otherwise a new one.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A code point is outside 0 to 255.</exception>
    /// <example><code>ColorSpace p3 = ColorSpace.FromCicp(12, 13); // ColorSpace.DisplayP3</code></example>
    public static ColorSpace FromCicp(int primaries, int transfer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(primaries);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(primaries, 255);
        ArgumentOutOfRangeException.ThrowIfNegative(transfer);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(transfer, 255);
        return FromCicp(primaries, transfer, 0, fullRange: true, iccProfile: null);
    }

    /// <summary>A colour space described by an ICC profile, which is copied and kept byte for byte.</summary>
    /// <param name="profile">The whole profile.</param>
    /// <returns>The colour space.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="profile"/> is shorter than an ICC header (128 bytes), lacks the <c>acsp</c> signature, or is
    /// longer than <see cref="MaxIccProfileBytes"/>.
    /// </exception>
    /// <example><code>image.ColorSpace = ColorSpace.FromIccProfile(File.ReadAllBytes("DisplayP3.icc"));</code></example>
    public static ColorSpace FromIccProfile(ReadOnlySpan<byte> profile)
    {
        string? problem = CheckIccProfile(profile);
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(profile));
        }

        return new ColorSpace("ICC profile", null, null, iccProfile: profile.ToArray());
    }

    /// <summary>Whether <paramref name="other"/> means the same: the same code points, profile bytes and PNG values.</summary>
    /// <param name="other">The colour space to compare with.</param>
    /// <returns><see langword="true"/> when they're equal.</returns>
    /// <example><code>bool same = image.ColorSpace.Equals(ColorSpace.Srgb);</code></example>
    public bool Equals(ColorSpace? other) =>
        other is not null
        && CicpPrimaries == other.CicpPrimaries
        && CicpTransfer == other.CicpTransfer
        && CicpMatrix == other.CicpMatrix
        && CicpFullRange == other.CicpFullRange
        && PngGamma == other.PngGamma
        && PngChromaticities.SequenceEqual(other.PngChromaticities)
        && IccProfile.Span.SequenceEqual(other.IccProfile.Span);

    /// <summary>Whether <paramref name="obj"/> is a <see cref="ColorSpace"/> that means the same.</summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns><see langword="true"/> when they're equal.</returns>
    /// <example><code>bool same = image.ColorSpace.Equals((object)ColorSpace.Srgb);</code></example>
    public override bool Equals(object? obj) => Equals(obj as ColorSpace);

    /// <summary>A hash of the code points, the profile's length and the PNG gamma.</summary>
    /// <returns>The hash code.</returns>
    /// <example><code>var seen = new HashSet&lt;ColorSpace&gt; { image.ColorSpace };</code></example>
    public override int GetHashCode() => HashCode.Combine(CicpPrimaries, CicpTransfer, CicpMatrix, CicpFullRange, PngGamma, IccProfile.Length);

    /// <summary>The name, such as <c>Display P3</c>.</summary>
    /// <returns>The name.</returns>
    /// <example><code>Console.WriteLine(image.ColorSpace); // Display P3</code></example>
    public override string ToString() => Name;

    /// <summary>Checks the parts of an ICC profile this library relies on.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>What's wrong, or <see langword="null"/> when it's acceptable.</returns>
    internal static string? CheckIccProfile(ReadOnlySpan<byte> profile)
    {
        if (profile.Length > MaxIccProfileBytes)
        {
            return string.Create(CultureInfo.InvariantCulture, $"The ICC profile is {profile.Length} bytes, more than the {MaxIccProfileBytes}-byte limit.");
        }

        return profile.Length < 128 || !profile.Slice(36, 4).SequenceEqual("acsp"u8)
            ? "The data isn't an ICC profile: it has no 128-byte header with the 'acsp' signature."
            : null;
    }

    /// <summary>The colour space from any combination of what a file says, without the public range checks.</summary>
    /// <param name="primaries">The primaries code point.</param>
    /// <param name="transfer">The transfer code point.</param>
    /// <param name="matrix">The matrix coefficients code point.</param>
    /// <param name="fullRange">The full-range flag.</param>
    /// <param name="iccProfile">An ICC profile the file also carries, or <see langword="null"/>.</param>
    /// <returns>The colour space.</returns>
    internal static ColorSpace FromCicp(int primaries, int transfer, int matrix, bool fullRange, byte[]? iccProfile)
    {
        if (matrix == 0 && fullRange && iccProfile is null)
        {
            foreach (ColorSpace known in (ReadOnlySpan<ColorSpace>)[Srgb, DisplayP3, Rec2020, Rec2020Pq, Rec2020Hlg, LinearSrgb])
            {
                if (known.CicpPrimaries == primaries && known.CicpTransfer == transfer)
                {
                    return known;
                }
            }
        }

        string name = string.Create(CultureInfo.InvariantCulture, $"CICP {primaries}/{transfer}");
        if (matrix != 0 || !fullRange)
        {
            name += string.Create(CultureInfo.InvariantCulture, $" (matrix {matrix}, {(fullRange ? "full" : "narrow")} range)");
        }

        return new ColorSpace(name, primaries, transfer, matrix, fullRange, iccProfile);
    }

    /// <summary>A colour space from an ICC profile already checked by <see cref="CheckIccProfile"/>, without copying it.</summary>
    /// <param name="profile">The profile, which the colour space takes over.</param>
    /// <returns>The colour space.</returns>
    internal static ColorSpace FromCheckedIccProfile(byte[] profile) => new("ICC profile", null, null, iccProfile: profile);

    /// <summary>A colour space from PNG's <c>gAMA</c> and <c>cHRM</c> chunks.</summary>
    /// <param name="gamma">The file gamma times 100,000, or 0 when there's no <c>gAMA</c>.</param>
    /// <param name="chromaticities">The eight <c>cHRM</c> values, or <see langword="null"/>.</param>
    /// <returns>
    /// <see cref="Srgb"/> for gamma 1/2.2 with sRGB's primaries or none, which is how browsers show such files;
    /// otherwise a colour space that keeps the values so an encoder can write them back.
    /// </returns>
    internal static ColorSpace FromPngGamma(int gamma, int[]? chromaticities)
    {
        ReadOnlySpan<int> srgbPrimaries = [31270, 32900, 64000, 33000, 30000, 60000, 15000, 6000];
        bool srgbLike = chromaticities is null || IsClose(chromaticities, srgbPrimaries);
        if (srgbLike && (gamma == 0 || Math.Abs(gamma - 45455) <= 500))
        {
            return Srgb;
        }

        string name = gamma == 0
            ? "PNG chromaticities"
            : string.Create(CultureInfo.InvariantCulture, $"PNG gamma {100_000.0 / gamma:0.##}");
        return new ColorSpace(name, null, null, pngGamma: gamma, chromaticities: chromaticities);
    }

    private static bool IsClose(ReadOnlySpan<int> values, ReadOnlySpan<int> expected)
    {
        for (int i = 0; i < expected.Length; i++)
        {
            if (Math.Abs(values[i] - expected[i]) > 1000)
            {
                return false;
            }
        }

        return true;
    }
}
