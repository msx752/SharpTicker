using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SharpTicker")]
[assembly: InternalsVisibleTo("SharpTicker.EntityFrameworkCore")]
[assembly: InternalsVisibleTo("SharpTicker.MongoDB")]
[assembly: InternalsVisibleTo("SharpTicker.Dashboard")]
[assembly: InternalsVisibleTo("SharpTicker.Tests")]
[assembly: InternalsVisibleTo("SharpTicker.Instrumentation.OpenTelemetry")]
[assembly: InternalsVisibleTo("SharpTicker.Caching.StackExchangeRedis")]
[assembly: InternalsVisibleTo("SharpTicker.EntityFrameworkCore.Tests")]
[assembly: InternalsVisibleTo("SharpTicker.Caching.StackExchangeRedis.Tests")]
[assembly: InternalsVisibleTo("SharpTicker.MongoDB.Tests")]
// To be testable using NSubsitute
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]