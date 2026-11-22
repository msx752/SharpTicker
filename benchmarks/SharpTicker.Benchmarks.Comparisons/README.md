# SharpTicker.Benchmarks.Comparisons

[BenchmarkDotNet](https://benchmarkdotnet.org/) benchmarks that run the same work with SharpTicker, Hangfire and
Quartz.NET. The Hangfire and Quartz packages are referenced only by this project.

| Class | What is compared |
|---|---|
| `JobCreationComparison` | Scheduling one one-off job, due in an hour, into each library's in-memory store, with the same no-op job |
| `StartupRegistrationComparison` | Setting up each library with an empty in-memory store and registering N recurring jobs |
| `JobSerializationComparison` | Serializing and reading the data a job carries, with SharpTicker's and Hangfire's own serializers |
| `CronExpressionComparison` | Library level: NCrontab (the cron library SharpTicker uses) against Quartz's `CronExpression` |

Each class comment says exactly what each row does and which library has no row, and why. No job is executed in these
benchmarks, because nothing runs the stores' jobs.

## Running

From this folder:

```bash
dotnet run -c Release -- --filter * --job short
```

- `--filter` takes a pattern, for example `--filter *JobCreation*` for one class.
- The command line sets the job (`--job short`, `--job dry` to check that every benchmark starts, or no `--job` for the
  default job). A class only sets an invocation count where its stores must be emptied between iterations.
- Results are written to `BenchmarkDotNet.Artifacts/results` (markdown, csv, html).

## About the results

The results are for local comparison only, on the same machine. They depend on the machine, its load, the runtime and
the library versions, and a short job gives wide error margins. No numbers are published from these benchmarks.
