using System;
using Zorg.Imaging.Codecs.Png;

// Published with Native AOT and executed in CI. Trimming keeps only what's used, so every
// codec and operation has to be exercised here: a clean publish alone proves little.
int failures = 0;

void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
    if (!ok)
    {
        failures++;
    }
}

Check("png crc-32 check value", Crc32.Compute("123456789"u8) == 0xCBF43926u);

return failures == 0 ? 0 : 1;
