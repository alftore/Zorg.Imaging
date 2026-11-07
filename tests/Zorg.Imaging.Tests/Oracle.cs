using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Zorg.Imaging.Tests;

/// <summary>
/// Runs reference tools (ImageMagick, libpng, libwebp...) as black boxes. A missing tool skips the test, unless
/// <c>ZORG_REQUIRE_ORACLES=1</c>, which the Linux gate and CI set, makes it a failure.
/// </summary>
internal static class Oracle
{
    /// <summary>Finds a tool on PATH, or skips (or fails) the test.</summary>
    public static string Require(string tool)
    {
        string? path = Find(tool);
        if (path is not null)
        {
            return path;
        }

        string message = $"{tool} isn't installed (it's in the Linux gate image).";
        if (Environment.GetEnvironmentVariable("ZORG_REQUIRE_ORACLES") == "1")
        {
            Assert.Fail(message + " ZORG_REQUIRE_ORACLES=1 makes this a failure.");
        }

        Assert.Skip(message);
        return tool;
    }

    /// <summary>Runs a tool and returns its exit code, standard output and standard error.</summary>
    public static async Task<(int ExitCode, byte[] Output, string Errors)> RunAsync(string path, params string[] arguments)
    {
        var start = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        using var output = new MemoryStream();
        Task copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        await copy;
        return (process.ExitCode, output.ToArray(), await errors);
    }

    /// <summary>A fresh folder for a test's files, deleted when disposed.</summary>
    public static TempFolder CreateFolder() => new();

    private static string? Find(string tool)
    {
        string[] names = OperatingSystem.IsWindows() ? [tool + ".exe", tool] : [tool];
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string name in names)
            {
                string candidate = Path.Combine(folder, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    internal sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zorg-imaging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
