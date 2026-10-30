using BenchmarkDotNet.Running;
using SharpTicker.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
