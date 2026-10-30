# SharpTicker.Instrumentation.OpenTelemetry

OpenTelemetry instrumentation package for SharpTicker job scheduler with distributed tracing support.

## Features

- **Distributed Tracing**: Full OpenTelemetry activity/span creation for job execution lifecycle
- **Structured Logging**: Rich logging with job context through ILogger integration
- **Parent-Child Relationships**: Maintains trace relationships between parent and child jobs
- **Retry Tracking**: Tracks retry attempts with detailed context
- **Performance Metrics**: Comprehensive execution time and outcome tracking
- **Error Tracking**: Detailed exception and cancellation tracking
- **Caller Information**: Automatic detection of where jobs are enqueued from

## Installation

```bash
dotnet add package SharpTicker.Instrumentation.OpenTelemetry
```

## Usage

### Basic Setup

```csharp
using SharpTicker.Instrumentation.OpenTelemetry;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Configure OpenTelemetry with SharpTicker ActivitySource
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSource("SharpTicker") // Add SharpTicker ActivitySource
               .AddConsoleExporter()
               .AddJaegerExporter();
    });

// Add SharpTicker with OpenTelemetry instrumentation
builder.Services.AddSharpTicker<MyTimeTicker, MyCronTicker>(options => { })
    .AddOperationalStore(ef => { })
    .AddOpenTelemetryInstrumentation(); // 👈 Enable tracing

var app = builder.Build();
app.Run();
```

### With Jaeger

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSharpTickerInstrumentation()
               .AddJaegerExporter(options =>
               {
                   options.Endpoint = new Uri("http://localhost:14268/api/traces");
               });
    });
```

### With Application Insights

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing.AddSharpTickerInstrumentation()
               .AddAzureMonitorTraceExporter();
    });
```

## Trace Structure

### Job Execution Activities
```
sharpticker.job.execute.timeticker (main job execution span)
├── sharpticker.job.enqueued (when job starts execution)
├── sharpticker.job.completed (on successful completion)
├── sharpticker.job.failed (on failure)
├── sharpticker.job.cancelled (on cancellation)
├── sharpticker.job.skipped (when skipped)
├── sharpticker.seeding.started (for data seeding)
└── sharpticker.seeding.completed (seeding completion)
```

### Tags Added to Activities

| Tag | Description | Example |
|-----|-------------|---------|
| `sharpticker.job.id` | Unique job identifier | `123e4567-e89b-12d3-a456-426614174000` |
| `sharpticker.job.type` | Type of ticker | `TimeTicker`, `CronTicker` |
| `sharpticker.job.function` | Function name being executed | `ProcessEmails` |
| `sharpticker.job.priority` | Job priority | `Normal`, `High`, `LongRunning` |
| `sharpticker.job.machine` | Machine executing the job | `web-server-01` |
| `sharpticker.job.parent_id` | Parent job ID (for child jobs) | `parent-job-guid` |
| `sharpticker.job.enqueued_from` | Where the job was enqueued from | `UserController.CreateUser (Program.cs:42)` |
| `sharpticker.job.is_due` | Whether the job was due | `true`, `false` |
| `sharpticker.job.is_child` | Whether this is a child job | `true`, `false` |
| `sharpticker.job.retries` | Maximum retry attempts | `3` |
| `sharpticker.job.current_attempt` | Current retry attempt | `1`, `2`, `3` |
| `sharpticker.job.final_status` | Final execution status | `Done`, `Failed`, `Cancelled`, `Skipped` |
| `sharpticker.job.final_retry_count` | Final retry count reached | `2` |
| `sharpticker.job.execution_time_ms` | Execution time in milliseconds | `1250` |
| `sharpticker.job.success` | Whether execution was successful | `true`, `false` |
| `sharpticker.job.error_type` | Exception type for failures | `SqlException`, `TimeoutException` |
| `sharpticker.job.error_message` | Error message | `Connection timeout` |
| `sharpticker.job.error_stack_trace` | Full stack trace | `at MyService.ProcessData()...` |
| `sharpticker.job.cancellation_reason` | Reason for cancellation | `Task was cancelled` |
| `sharpticker.job.skip_reason` | Reason for skipping | `Another instance is already running` |

## Logging Output

The instrumentation provides structured logging for all job events:

```
[INF] SharpTicker Job enqueued: TimeTicker - ProcessEmails (123e4567-e89b-12d3-a456-426614174000) from ExecutionTaskHandler
[INF] SharpTicker Job completed: ProcessEmails (123e4567-e89b-12d3-a456-426614174000) in 1250ms - Success: True
[ERR] SharpTicker Job failed: ProcessEmails (123e4567-e89b-12d3-a456-426614174000) - Retry 1 - Connection timeout
[INF] SharpTicker Job completed: ProcessEmails (123e4567-e89b-12d3-a456-426614174000) in 2500ms - Success: False
[WRN] SharpTicker Job cancelled: ProcessEmails (123e4567-e89b-12d3-a456-426614174000) - Task was cancelled
[INF] SharpTicker Job skipped: ProcessEmails (123e4567-e89b-12d3-a456-426614174000) - Another CronOccurrence is already running!
[INF] SharpTicker start seeding data: TimeTicker (production-node-01)
[INF] SharpTicker completed seeding data: TimeTicker (production-node-01)
```

## Integration with Logging Frameworks

This package works seamlessly with any logging framework that integrates with `ILogger`:

### Serilog
```csharp
builder.Host.UseSerilog((context, config) =>
{
    config.WriteTo.Console()
          .WriteTo.File("logs/sharpticker-.txt", rollingInterval: RollingInterval.Day)
          .Enrich.FromLogContext();
});
```

### NLog
```csharp
builder.Logging.ClearProviders();
builder.Logging.AddNLog();
```

## Performance Impact

- **Minimal Overhead**: Activities are only created when OpenTelemetry listeners are active
- **Efficient Logging**: Uses structured logging with minimal string allocations
- **Conditional Tracing**: No performance impact when tracing is disabled

## Requirements

- .NET 8.0 or later
- OpenTelemetry 1.15.3 or later
- SharpTicker.Utilities (automatically included)
