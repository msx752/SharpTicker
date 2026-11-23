# SharpTicker.Benchmarks

[BenchmarkDotNet](https://benchmarkdotnet.org/) benchmarks for parts of SharpTicker: chain building, cron parsing,
request serialization, `DbContextLease`, `InternalFunctionContext.SetProperty`, and the function registry collections.

Comparisons with Hangfire and Quartz are in a separate project, [SharpTicker.Benchmarks.Comparisons](../SharpTicker.Benchmarks.Comparisons/README.md).

## Running

From this folder:

```bash
dotnet run -c Release -- --filter * --job short
```

- `--filter` takes a pattern, for example `--filter *CronParsing*` for one class.
- The command line sets the job (`--job short`, `--job dry` to check that every benchmark starts, or no `--job` for the
  default job). No class sets a job of its own.
- `--list flat` lists the benchmarks without running them.
- Results are written to `BenchmarkDotNet.Artifacts/results` (markdown, csv, html).

## About the results

The results are for local comparison only, for example before and after a change, on the same machine. They depend on
the machine, its load and the runtime, and a short job gives wide error margins. No numbers are published from these
benchmarks.
