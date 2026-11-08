using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Zorg.Imaging.ComplianceTests;

/// <summary>
/// Finds a corpus that <c>scripts/fetch-corpora.sh</c> fetched into <c>.cache/corpora</c>. A missing corpus skips the
/// test, unless <c>ZORG_REQUIRE_CORPORA=1</c>, which the Linux gate sets, makes it a failure.
/// </summary>
internal static class Corpus
{
    /// <summary>The corpus folder, or a skip (or failure) when it hasn't been fetched.</summary>
    public static string Require(string name)
    {
        string? folder = Find(name);
        if (folder is not null)
        {
            return folder;
        }

        string message = $"The {name} corpus isn't fetched; run scripts/fetch-corpora.sh.";
        if (Environment.GetEnvironmentVariable("ZORG_REQUIRE_CORPORA") == "1")
        {
            Assert.Fail(message + " ZORG_REQUIRE_CORPORA=1 makes this a failure.");
        }

        Assert.Skip(message);
        return "";
    }

    /// <summary>The file names in a corpus with the extension, sorted, for theory data; empty when it isn't fetched.</summary>
    public static TheoryData<string> Files(string name, string extension, Func<string, bool>? where = null)
    {
        var data = new TheoryData<string>();
        if (Find(name) is not { } folder)
        {
            // One row, so the theory runs and reports the corpus as missing rather than having no cases.
            data.Add("(not fetched)");
            return data;
        }

        IEnumerable<string> names = Directory.EnumerateFiles(folder, "*" + extension).Select(Path.GetFileName).OfType<string>();
        foreach (string file in names.Where(where ?? (_ => true)).Order(StringComparer.Ordinal))
        {
            data.Add(file);
        }

        return data;
    }

    private static string? Find(string name)
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string candidate = Path.Combine(folder.FullName, ".cache", "corpora", name);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
