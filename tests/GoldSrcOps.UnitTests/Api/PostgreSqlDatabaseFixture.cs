using Npgsql;
using Testcontainers.PostgreSql;

namespace GoldSrcOps.UnitTests.Api;

public sealed class PostgreSqlDatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("goldsrcops_tests")
        .WithUsername("goldsrcops")
        .WithPassword("goldsrcops")
        .Build();

    internal string AdministrationConnectionString =>
        new NpgsqlConnectionStringBuilder(_database.GetConnectionString()) { Pooling = false }.ConnectionString;

    public Task InitializeAsync() => _database.StartAsync();

    public async Task DisposeAsync() => await _database.DisposeAsync();

    internal async Task<PostgreSqlDatabaseLease> CreateDatabaseAsync()
    {
        var database = new PostgreSqlDatabaseLease(AdministrationConnectionString);
        try
        {
            await database.InitializeAsync();
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }
}
