using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;
using Zorg.Imaging.Tests.Codecs.Png;

namespace Zorg.Imaging.Tests.Codecs;

/// <summary>
/// Fuzzing, first tier, shared by the decoders: truncations, single bytes set to edge values, random damage and damage
/// read through a stream. Whatever the damage, decoding either succeeds or throws an <see cref="ImageException"/>, and
/// never takes long (guarantee 1).
/// </summary>
internal static class DecoderFuzz
{
    /// <summary>Small enough that a damaged size field can't make a test allocate much.</summary>
    public static readonly DecodeOptions Small = new() { Limits = new ImageLimits { MaxPixels = 1_000_000, MaxFrames = 64 } };

    private static readonly byte[] EdgeValues = [0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF];

    /// <summary>
    /// <c>ZORG_FUZZ_SCALE</c> multiplies the random rounds for a longer hunt, such as 50 overnight; the seed changes
    /// with the scale so a long run tries new cases. 1 when unset.
    /// </summary>
    private static readonly int Scale = int.TryParse(Environment.GetEnvironmentVariable("ZORG_FUZZ_SCALE"), out int scale) && scale > 1 ? scale : 1;

    /// <summary>Every length through the first 4 KiB, then every 97th.</summary>
    public static void Truncations(byte[] file, string name, Action<byte[]> decode)
    {
        for (int length = 0; length < file.Length; length += length < 4096 ? 1 : 97)
        {
            Check(file[..length], $"{name} cut to {length}", decode);
            CheckIdentify(file[..length], $"{name} cut to {length}");
        }
    }

    /// <summary>Each byte (every one in the first 4 KiB, then every 31st) set to each of a few edge values in turn.</summary>
    public static void EdgeBytes(byte[] file, string name, Action<byte[]> decode)
    {
        for (int at = 0; at < file.Length; at += at < 4096 ? 1 : 31)
        {
            foreach (byte value in EdgeValues)
            {
                if (file[at] == value)
                {
                    continue;
                }

                byte[] copy = (byte[])file.Clone();
                copy[at] = value;
                Check(copy, $"{name} byte {at} = {value}", decode);
            }
        }
    }

    /// <summary>One to four bytes flipped or replaced at random, <paramref name="rounds"/> times, from a fixed seed.</summary>
    public static void RandomDamage(byte[] file, string name, int rounds, Action<byte[]> decode)
    {
        var random = new Random(name.Length * 7919 + file.Length + Scale);
        for (int round = 0; round < rounds * Scale; round++)
        {
            byte[] copy = (byte[])file.Clone();
            int flips = 1 + random.Next(4);
            for (int f = 0; f < flips; f++)
            {
                int at = random.Next(copy.Length);
                copy[at] = random.Next(3) == 0 ? (byte)random.Next(256) : (byte)(copy[at] ^ (1 << random.Next(8)));
            }

            Check(copy, $"{name} round {round}", decode);
            CheckIdentify(copy, $"{name} round {round}");
        }
    }

    /// <summary>Each field overwritten with each value, as the given writer stores it.</summary>
    public static void Fields(byte[] file, string name, IEnumerable<(int Offset, string Field)> fields, uint[] values, Action<byte[], int, uint> write, Action<byte[]> decode)
    {
        foreach ((int offset, string field) in fields)
        {
            foreach (uint value in values)
            {
                byte[] copy = (byte[])file.Clone();
                write(copy, offset, value);
                Check(copy, $"{name} {field} = {value}", decode);
            }
        }
    }

    /// <summary>A flipped bit and a cut, read through a stream that can't seek, so the streaming paths see the damage.</summary>
    public static async Task StreamDamage(byte[] file, string name, int rounds)
    {
        var random = new Random(name.Length + 11 + Scale);
        for (int round = 0; round < rounds * Scale; round++)
        {
            byte[] copy = (byte[])file.Clone();
            copy[random.Next(copy.Length)] ^= (byte)(1 << random.Next(8));
            byte[] cut = copy[..random.Next(copy.Length + 1)];
            long started = Stopwatch.GetTimestamp();
            Exception? error = await Record.ExceptionAsync(async () =>
            {
                using Image image = await Image.DecodeAsync(new ForwardOnlyStream(cut), Small, TestContext.Current.CancellationToken);
            });
            Assert.True(error is null or ImageException, $"{name} round {round}: {error}");
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5), $"{name} round {round} was slow");
        }
    }

    /// <summary>Decodes with <see cref="Small"/> limits at full size.</summary>
    public static void Decode(byte[] data)
    {
        using Image image = Image.Decode(data, Small);
    }

    /// <summary><see cref="Image.Identify"/> never throws for bad input: it reports it in the result.</summary>
    public static void CheckIdentify(byte[] data, string what)
    {
        Exception? error = Record.Exception(() => Image.Identify(data));
        Assert.True(error is null, $"Identify, {what}: {error}");
    }

    /// <summary>
    /// Runs the decode, which may only succeed or throw an <see cref="ImageException"/>. The time limit is loose so
    /// that a busy machine doesn't fail it; a hang shows as far longer.
    /// </summary>
    public static void Check(byte[] data, string what, Action<byte[]> decode)
    {
        long started = Stopwatch.GetTimestamp();
        Exception? error = Record.Exception(() => decode(data));
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        Assert.True(error is null or ImageException, $"{what}: {error}");
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"{what} took {elapsed}");
    }
}
