using Microsoft.EntityFrameworkCore;

namespace SharpTicker.EntityFrameworkCore.Tests.Infrastructure;

/// <summary>
/// #257 on SQL Server (#247): the times are datetime2 columns, which carry no kind; the converter of the store reads them
/// back as UTC with the value written.
/// </summary>
[Collection(SqlServerTestFixture.CollectionName)]
public class SqlServerDateTimeKindTests(SqlServerTestFixture fixture)
{
    [Theory]
    [MemberData(nameof(EfCoreDateTimeKindTests.Setups), MemberType = typeof(EfCoreDateTimeKindTests))]
    public async Task OnSqlServer_EveryTimeOfTheStoresTables_ReadsBackAsUtc_WithTheValueWritten(string setup)
    {
        await fixture.ResetAsync();
        // The fixture created the tables under the "ticker" schema; every setup here uses that default schema.
        var (context, owner) = EfCoreDateTimeKindTests.Context(setup, o => o.UseSqlServer(fixture.ConnectionString()));
        using (owner)
        {
            var ids = await EfCoreDateTimeKindTests.SeedAsync(context);
            await EfCoreDateTimeKindTests.AssertEveryTimeReadsBackAsUtcAsync(context, ids);
        }

        await fixture.ResetAsync();
    }
}
