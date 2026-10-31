## Release 1.0.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------
TQ001   | SharpTicker.SourceGenerator | Error | Class should be public or internal to be used with [TickerFunction]
TQ002   | SharpTicker.SourceGenerator | Error | Method should be public or internal to be used with [TickerFunction]
TQ003   | SharpTicker.SourceGenerator | Error | Invalid cron expression
TQ004   | SharpTicker.SourceGenerator | Error | Missing function name in [TickerFunction] attribute
TQ005   | SharpTicker.SourceGenerator | Error | Duplicate function name across [TickerFunction] methods
TQ006   | SharpTicker.SourceGenerator | Warning | Multiple constructors detected - first constructor will be used unless [SharpTickerConstructor] attribute is specified
TQ007   | SharpTicker.SourceGenerator | Error | Abstract class contains [TickerFunction] methods
TQ008   | SharpTicker.SourceGenerator | Error | Nested class contains [TickerFunction] methods - only allowed in top-level classes
TQ009   | SharpTicker.SourceGenerator | Error | Invalid TickerFunction parameter - only TickerFunctionContext, TickerFunctionContext<T>, CancellationToken, or no parameters allowed
TQ010   | SharpTicker.SourceGenerator | Error | Multiple constructors with [SharpTickerConstructor] attribute - only one constructor can be marked
TQ011   | SharpTicker.SourceGenerator | Error | Invalid maxConcurrency value - must be >= 0 (0 = unlimited)
