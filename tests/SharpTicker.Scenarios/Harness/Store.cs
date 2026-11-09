namespace SharpTicker.Scenarios.Harness;

public enum Store
{
    EfCorePostgres,
    MongoDb,
    Redis,
}

public static class StoreNames
{
    /// <summary>The value of the node's SCENARIO_STORE variable.</summary>
    public static string HostValue(this Store store) => store switch
    {
        Store.EfCorePostgres => "efcore-postgres",
        Store.MongoDb => "mongodb",
        Store.Redis => "redis",
        _ => throw new ArgumentOutOfRangeException(nameof(store)),
    };

    /// <summary>A short name for folders and identifiers.</summary>
    public static string Short(this Store store) => store switch
    {
        Store.EfCorePostgres => "efcore",
        Store.MongoDb => "mongo",
        Store.Redis => "redis",
        _ => throw new ArgumentOutOfRangeException(nameof(store)),
    };
}
