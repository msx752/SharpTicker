using Microsoft.EntityFrameworkCore;

namespace SharpTicker.EntityFrameworkCore.Tests.Infrastructure;

/// <summary>
/// #257 on PostgreSQL: the times are timestamptz columns, which Npgsql already reads as UTC. With the converter of the
/// store they still read back as UTC and keep the value written: nothing is shifted.
/// </summary>
[Collection(PostgresTestFixture.CollectionName)]
public class PostgresDateTimeKindTests(PostgresTestFixture fixture)
{
    [Theory]
    [MemberData(nameof(EfCoreDateTimeKindTests.Setups), MemberType = typeof(EfCoreDateTimeKindTests))]
    public async Task OnPostgres_EveryTimeOfTheStoresTables_ReadsBackAsUtc_WithTheValueWritten(string setup)
    {
        await fixture.ResetAsync();
        // The fixture created the tables under the "ticker" schema; every setup here uses that default schema.
        var (context, owner) = EfCoreDateTimeKindTests.Context(setup, o => o.UseNpgsql(fixture.Container.GetConnectionString()));
        using (owner)
        {
            var ids = await EfCoreDateTimeKindTests.SeedAsync(context);
            await EfCoreDateTimeKindTests.AssertEveryTimeReadsBackAsUtcAsync(context, ids);
        }

        await fixture.ResetAsync();
    }
}
