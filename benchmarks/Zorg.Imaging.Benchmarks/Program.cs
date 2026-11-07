using BenchmarkDotNet.Running;

// dotnet run -c Release --project benchmarks/Zorg.Imaging.Benchmarks -- --filter '*'
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

/// <summary>The entry point, named so BenchmarkSwitcher can find the assembly.</summary>
internal sealed partial class Program;
