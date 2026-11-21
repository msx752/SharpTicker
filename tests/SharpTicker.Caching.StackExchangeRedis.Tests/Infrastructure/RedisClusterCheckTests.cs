using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;

namespace SharpTicker.Caching.StackExchangeRedis.Tests.Infrastructure;

/// <summary>
/// <see cref="RedisClusterCheck"/> over a connection whose servers say what they are (#225): one Error for a cluster,
/// the first time only; nothing for a standalone server or with another job store; a check that throws logs at Debug.
/// </summary>
public class RedisClusterCheckTests
{
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static IConnectionMultiplexer ConnectionOf(params ServerType[] types)
    {
        var servers = types.Select(type =>
        {
            var server = Substitute.For<IServer>();
            server.ServerType.Returns(type);
            return server;
        }).ToArray();
        var connection = Substitute.For<IConnectionMultiplexer>();
        connection.GetServers().Returns(servers);
        return connection;
    }

    [Fact]
    public void OnACluster_TheFirstCallWritesOneError_AndLaterCallsWriteNothing()
    {
        var connection = ConnectionOf(ServerType.Cluster, ServerType.Cluster, ServerType.Cluster);
        var check = new RedisClusterCheck(StoreIsRedis: true, Connection: () => connection);
        var logger = new RecordingLogger();

        check.RunOnce(logger);
        check.RunOnce(logger);
        check.RunOnce(logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(RedisClusterCheck.ClusterNotSupportedMessage, entry.Text);
        connection.Received(1).GetServers();
    }

    [Fact]
    public void OnAStandaloneServer_NothingIsWritten()
    {
        var check = new RedisClusterCheck(StoreIsRedis: true, Connection: () => ConnectionOf(ServerType.Standalone));
        var logger = new RecordingLogger();

        check.RunOnce(logger);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void WithAnotherJobStore_TheConnectionIsNotLookedAt()
    {
        var connection = ConnectionOf(ServerType.Cluster);
        var check = new RedisClusterCheck(StoreIsRedis: false, Connection: () => connection);
        var logger = new RecordingLogger();

        check.RunOnce(logger);

        Assert.Empty(logger.Entries);
        connection.DidNotReceive().GetServers();
    }

    [Fact]
    public void ACheckThatThrows_IsLoggedAtDebug_AndDoesNotThrow()
    {
        var check = new RedisClusterCheck(StoreIsRedis: true, Connection: () => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        var logger = new RecordingLogger();

        check.RunOnce(logger);

        Assert.Equal(LogLevel.Debug, Assert.Single(logger.Entries).Level);
    }
}
