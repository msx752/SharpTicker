# SharpTicker.Instrumentation.OpenTelemetry

Tracing for [SharpTicker](https://github.com/msx752/SharpTicker), a background job scheduler for .NET. It reports each job run as `System.Diagnostics` activities from the source named `SharpTicker`, so an OpenTelemetry setup can export them, and it keeps writing SharpTicker's log entries through `ILogger`.

## Install

```bash
dotnet add package SharpTicker.Instrumentation.OpenTelemetry
```

## Setup

Turn the instrumentation on in SharpTicker, and add the `SharpTicker` source to your tracing setup:

```csharp
using OpenTelemetry.Trace;
using SharpTicker.DependencyInjection;
using SharpTicker.Instrumentation.OpenTelemetry;

builder.Services.AddSharpTicker(options =>
{
    options.AddOpenTelemetryInstrumentation();
});

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource("SharpTicker")
        .AddConsoleExporter()); // an exporter from its own package, here OpenTelemetry.Exporter.Console
```

Activities are created only while a listener, such as the OpenTelemetry SDK, listens to the `SharpTicker` source. SharpTicker has no `Meter`; it reports traces and log entries, not metrics.

## Documentation

[Tracing](https://msx752.github.io/SharpTicker/guide/observability.html#tracing): activity names, tags and the log entries.
