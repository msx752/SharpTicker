using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using SharpTicker.EntityFrameworkCore.Configurations;
using SharpTicker.EntityFrameworkCore.DbContextFactory;
using SharpTicker.Utilities.Entities;

namespace SharpTicker.Benchmarks;

/// <summary>
/// Benchmarks for DbContextLease: getting a context through an IDbContextFactory (pooled or not) against through a
/// service scope, and the same paths without the lease. The context is not used (no model build, no connection use),
/// so this is the cost of getting and returning a context, not of a first query.
/// "Pooled factory, registered by hand" is a PooledDbContextFactory registered as IDbContextFactory, as SharpTicker's
/// own registration does; "AddPooledDbContextFactory" is the EF Core registration method.
/// </summary>
[MemoryDiagnoser]
public class DbContextLeaseBenchmarks
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _factorySp = null!;
    private ServiceProvider _scopedSp = null!;
    private ServiceProvider _pooledSp = null!;
    private ServiceProvider _nonPooledSp = null!;

    [GlobalSetup]
    public void Setup()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<BenchmarkDbContext>()
            .UseSqlite(_connection)
            .Options;

        // Pooled factory, registered by hand
        var factoryServices = new ServiceCollection();
        factoryServices.AddSingleton<IDbContextFactory<BenchmarkDbContext>>(
            new PooledDbContextFactory<BenchmarkDbContext>(options));
        _factorySp = factoryServices.BuildServiceProvider();

        // Scoped path
        var scopedServices = new ServiceCollection();
        scopedServices.AddDbContext<BenchmarkDbContext>(opt => opt.UseSqlite(_connection));
        _scopedSp = scopedServices.BuildServiceProvider();

        // Pooled factory through AddPooledDbContextFactory
        var pooledServices = new ServiceCollection();
        pooledServices.AddPooledDbContextFactory<BenchmarkDbContext>(opt => opt.UseSqlite(_connection));
        _pooledSp = pooledServices.BuildServiceProvider();

        // Non-pooled factory through AddDbContextFactory
        var nonPooledServices = new ServiceCollection();
        nonPooledServices.AddDbContextFactory<BenchmarkDbContext>(opt => opt.UseSqlite(_connection));
        _nonPooledSp = nonPooledServices.BuildServiceProvider();

        using var ctx = new BenchmarkDbContext(options);
        ctx.Database.EnsureCreated();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _factorySp.Dispose();
        _scopedSp.Dispose();
        _pooledSp.Dispose();
        _nonPooledSp.Dispose();
        _connection.Dispose();
    }

    [Benchmark(Description = "DbContextLease: Pooled factory, registered by hand (async)")]
    public async Task<int> Lease_Factory_Async()
    {
        using var lease = await DbContextLease<BenchmarkDbContext>.CreateAsync(_factorySp, CancellationToken.None);
        return lease.Context.GetHashCode();
    }

    [Benchmark(Description = "DbContextLease: Pooled factory, registered by hand (sync)")]
    public int Lease_Factory_Sync()
    {
        using var lease = DbContextLease<BenchmarkDbContext>.Create(_factorySp);
        return lease.Context.GetHashCode();
    }

    [Benchmark(Description = "DbContextLease: Service scope, AddDbContext (async)")]
    public async Task<int> Lease_Scoped_Async()
    {
        using var lease = await DbContextLease<BenchmarkDbContext>.CreateAsync(_scopedSp, CancellationToken.None);
        return lease.Context.GetHashCode();
    }

    [Benchmark(Description = "DbContextLease: Service scope, AddDbContext (sync)")]
    public int Lease_Scoped_Sync()
    {
        using var lease = DbContextLease<BenchmarkDbContext>.Create(_scopedSp);
        return lease.Context.GetHashCode();
    }

    [Benchmark(Description = "DbContextLease: Pooled factory, AddPooledDbContextFactory (async)")]
    public async Task<int> Lease_Pooled_Async()
    {
        using var lease = await DbContextLease<BenchmarkDbContext>.CreateAsync(_pooledSp, CancellationToken.None);
        return lease.Context.GetHashCode();
    }

    [Benchmark(Description = "DbContextLease: Non-pooled factory, AddDbContextFactory (async)")]
    public async Task<int> Lease_NonPooled_Async()
    {
        using var lease = await DbContextLease<BenchmarkDbContext>.CreateAsync(_nonPooledSp, CancellationToken.None);
        return lease.Context.GetHashCode();
    }

    [Benchmark(Description = "DbContextLease: Non-pooled factory, AddDbContextFactory (sync)")]
    public int Lease_NonPooled_Sync()
    {
        using var lease = DbContextLease<BenchmarkDbContext>.Create(_nonPooledSp);
        return lease.Context.GetHashCode();
    }

    [Benchmark(Baseline = true, Description = "Raw: IDbContextFactory.CreateDbContext (pooled, registered by hand)")]
    public int Raw_Factory_Create()
    {
        var factory = _factorySp.GetRequiredService<IDbContextFactory<BenchmarkDbContext>>();
        using var ctx = factory.CreateDbContext();
        return ctx.GetHashCode();
    }

    [Benchmark(Description = "Raw: Service scope + resolve DbContext (AddDbContext)")]
    public int Raw_Scoped_Create()
    {
        using var scope = _scopedSp.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<BenchmarkDbContext>();
        return ctx.GetHashCode();
    }
}

public class BenchmarkDbContext : DbContext
{
    public BenchmarkDbContext(DbContextOptions<BenchmarkDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new TimeTickerConfigurations<TimeTickerEntity>("ticker"));
        modelBuilder.ApplyConfiguration(new CronTickerConfigurations<CronTickerEntity>("ticker"));
        base.OnModelCreating(modelBuilder);
    }
}
