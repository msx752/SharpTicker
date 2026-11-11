using Npgsql;
using SharpTicker.Scenarios.Harness;

namespace SharpTicker.Scenarios;

/// <summary>
/// Crons stored before the IsEnabled column existed. With the AddColumn edited as MIGRATING.md says
/// (defaultValue: true) they stay enabled and run; with the migration EF generates (false) they are disabled. The
/// control case shows the test can fail.
/// </summary>
public class MigrationScenarios
{
    // #28: with the migration edited as MIGRATING.md says, crons stored before the upgrade stay enabled and run.
    [Fact]
    public async Task TheDocumentedMigration_KeepsExistingCronsEnabled()
    {
        var (enabled, cronsThatRan) = await CronsAfterTheUpgrade("migration-documented", "documented");

        Assert.Equal(new[] { true, true, true }, enabled);
        Assert.Equal(3, cronsThatRan);
    }

    // The control case of #28: the migration EF Core generates, not edited, disables the stored crons.
    [Fact]
    public async Task TheUneditedMigration_DisablesThem()
    {
        var (enabled, cronsThatRan) = await CronsAfterTheUpgrade("migration-unedited", "unedited");

        Assert.Equal(new[] { false, false, false }, enabled);
        Assert.Equal(0, cronsThatRan);
    }

    /// <summary>
    /// Applies the first migration only, stores 3 crons with SQL in that schema, starts the node (which applies the
    /// rest), and returns IsEnabled per cron and how many of the crons ran within 15 s.
    /// </summary>
    private static async Task<(bool[] Enabled, int CronsThatRan)> CronsAfterTheUpgrade(string scenario, string migrations)
    {
        await using var env = await ScenarioEnvironment.StartAsync(scenario, Store.EfCorePostgres, nodes: 1, startedNodes: 0,
            nodeEnvironment: _ => new Dictionary<string, string> { ["SCENARIO_MIGRATIONS"] = migrations });

        var exit = await env.RunNodeToExitAsync(0,
            new Dictionary<string, string> { ["SCENARIO_MIGRATE_TO"] = "InitialSharpTickerOperationalStore" }, TimeSpan.FromSeconds(90));
        Assert.Equal(0, exit);

        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        await using (var db = new NpgsqlConnection(env.DirectStoreConnection))
        {
            await db.OpenAsync();
            foreach (var id in ids)
            {
                // Every column of CronTickers in the first migration.
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO ticker."CronTickers"
                        ("Id", "Expression", "Request", "Retries", "RetryIntervals", "Function", "Description", "InitIdentifier", "CreatedAt", "UpdatedAt")
                    VALUES (@id, '* * * * * *', NULL, 0, NULL, 'Scenario.Record', 'stored before the upgrade', NULL, now(), now())
                    """, db);
                insert.Parameters.AddWithValue("id", id);
                await insert.ExecuteNonQueryAsync();
            }
        }

        await env.StartNodeAsync(0);
        await Task.Delay(TimeSpan.FromSeconds(15));

        var enabled = new List<bool>();
        await using (var db = new NpgsqlConnection(env.DirectStoreConnection))
        {
            await db.OpenAsync();
            foreach (var id in ids)
            {
                await using var select = new NpgsqlCommand("""SELECT "IsEnabled" FROM ticker."CronTickers" WHERE "Id" = @id""", db);
                select.Parameters.AddWithValue("id", id);
                enabled.Add((bool)(await select.ExecuteScalarAsync())!);
            }
        }

        var ran = 0;
        foreach (var id in ids)
        {
            var occurrences = await env.Nodes[0].Dashboard.GetOccurrencesAsync(id);
            if (occurrences.Any(o => env.Runs.Starts(o.Id) > 0))
                ran++;
        }

        return (enabled.ToArray(), ran);
    }
}
