using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Tests;

namespace Zorg.Imaging.ComplianceTests;

/// <summary>
/// The libwebp conformance vectors (libwebp-test-data). <c>libwebp_tests.md5</c> lists the MD5 of what libwebp's
/// <c>dwebp -pam</c> writes for each file, so a decode that writes the same PAM bytes matches libwebp exactly, with no
/// tool needed.
/// </summary>
public class WebpConformanceTests
{
    private const string Name = "webp";

    public static TheoryData<string> Vectors => Corpus.Files(Name, ".webp");

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Every_vector_decodes_to_exactly_what_libwebp_decodes(string file)
    {
        string folder = Corpus.Require(Name);
        byte[] webp = File.ReadAllBytes(Path.Combine(folder, file));
        Dictionary<string, string> expected = Checksums(folder);
        Assert.SkipUnless(expected.ContainsKey(file + ".pam"), "Not in libwebp_tests.md5; the dwebp test covers it.");
        string md5 = expected[file + ".pam"];

        using Image image = Image.Decode(webp, new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        // MD5 because the corpus lists MD5; it only compares outputs here, it protects nothing.
#pragma warning disable CA5351
        Assert.Equal(md5, Convert.ToHexStringLower(MD5.HashData(Pam(image))));
#pragma warning restore CA5351

        IdentifyResult identified = Image.Identify(webp);
        Assert.True(identified.Succeeded, identified.Message);
        Assert.Equal((image.Width, image.Height), (identified.Info.Width, identified.Info.Height));
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public async Task Every_vector_decodes_to_what_dwebp_decodes(string file)
    {
        string path = Path.Combine(Corpus.Require(Name), file);
        string dwebp = Oracle.Require("dwebp");
        byte[] webp = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        using Oracle.TempFolder folder = Oracle.CreateFolder();
        string pam = folder.File("out.pam");
        (int code, _, string errors) = await Oracle.RunAsync(dwebp, path, "-pam", "-o", pam);
        if (code != 0)
        {
            // What libwebp refuses, we refuse too, with our own exception.
            Assert.ThrowsAny<ImageException>(() => Image.Decode(webp).Dispose());
            return;
        }

        using Image image = Image.Decode(webp, new DecodeOptions { PixelFormat = PixelFormat.Rgba32 });
        byte[] reference = await File.ReadAllBytesAsync(pam, TestContext.Current.CancellationToken);
        Assert.True(Pam(image).AsSpan().SequenceEqual(reference), $"{file} differs from dwebp. {errors}");
    }

    /// <summary>The PAM file <c>dwebp -pam</c> writes: an RGBA header, then the pixels.</summary>
    private static byte[] Pam(Image image)
    {
        byte[] header = Encoding.ASCII.GetBytes($"P7\nWIDTH {image.Width}\nHEIGHT {image.Height}\nDEPTH 4\nMAXVAL 255\nTUPLTYPE RGB_ALPHA\nENDHDR\n");
        byte[] pam = new byte[header.Length + (image.Width * image.Height * 4)];
        header.CopyTo(pam, 0);
        int at = header.Length;
        for (int y = 0; y < image.Height; y++)
        {
            ReadOnlySpan<byte> row = image.GetRowBytes(y);
            row[..(image.Width * 4)].CopyTo(pam.AsSpan(at));
            at += image.Width * 4;
        }

        return pam;
    }

    private static Dictionary<string, string> Checksums(string folder) =>
        File.ReadLines(Path.Combine(folder, "libwebp_tests.md5"))
            .Select(line => line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[1].Trim(), parts => parts[0]);
}
