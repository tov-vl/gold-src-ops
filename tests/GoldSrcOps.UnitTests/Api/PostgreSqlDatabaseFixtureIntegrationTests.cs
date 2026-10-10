using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Servers;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GoldSrcOps.UnitTests.Api;

public sealed class PostgreSqlDatabaseFixtureIntegrationTests : IClassFixture<PostgreSqlDatabaseFixture>
{
    private readonly PostgreSqlDatabaseFixture _databaseFixture;

    public PostgreSqlDatabaseFixtureIntegrationTests(PostgreSqlDatabaseFixture databaseFixture)
    {
        _databaseFixture = databaseFixture;
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Concurrent_factory_writes_are_isolated_and_disposal_removes_only_the_owned_database()
    {
        var initialDatabases = await ReadDatabaseNamesAsync();
        await using var first = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync(databaseFixture: _databaseFixture);
        await using var second = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync(databaseFixture: _databaseFixture);
        var firstDatabase = await ReadDatabaseNameAsync(first);
        var secondDatabase = await ReadDatabaseNameAsync(second);
        firstDatabase.Should().NotBe(secondDatabase);
        (await ReadDatabaseNamesAsync()).Should().Contain([firstDatabase, secondDatabase]);

        using var firstClient = first.CreateClient();
        using var secondClient = second.CreateClient();
        var firstRequest = firstClient.PostAsJsonAsync("/api/servers", Registration("First"));
        var secondRequest = secondClient.PostAsJsonAsync("/api/servers", Registration("Second"));
        using var firstResponse = await firstRequest;
        using var secondResponse = await secondRequest;
        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadServerNameAsync(first)).Should().Be("First");
        (await ReadServerNameAsync(second)).Should().Be("Second");

        await first.DisposeAsync();
        (await ReadDatabaseNamesAsync()).Should().NotContain(firstDatabase).And.Contain(secondDatabase);
        (await ReadServerNameAsync(second)).Should().Be("Second");
        await second.DisposeAsync();
        (await ReadDatabaseNamesAsync()).Should().Equal(initialDatabases);
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Failed_host_initialization_removes_its_database_and_allows_the_next_factory()
    {
        var initialDatabases = await ReadDatabaseNamesAsync();
        var failure = () => PostgreSqlGoldSrcOpsApiFactory.CreateAsync(
            _ => throw new InvalidOperationException("forced fixture startup failure"),
            databaseFixture: _databaseFixture);

        await failure.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("forced fixture startup failure");
        (await ReadDatabaseNamesAsync()).Should().Equal(initialDatabases);

        await using var recovered = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync(databaseFixture: _databaseFixture);
        var pendingMigrations = await recovered.ExecuteDbContextAsync(dbContext => dbContext.Database.GetPendingMigrationsAsync());
        pendingMigrations.Should().BeEmpty();
        var appliedMigrations = await recovered.ExecuteDbContextAsync(dbContext => dbContext.Database.GetAppliedMigrationsAsync());
        appliedMigrations.Should().NotBeEmpty();
        (await recovered.ExecuteDbContextAsync(dbContext => dbContext.Servers.CountAsync())).Should().Be(0);
    }

    private static RegisterServerRequest Registration(string name) =>
        new(name, "127.0.0.1", QueryPort: 27015, RconPort: null, PollIntervalSeconds: 30, Notes: null);

    private static Task<string> ReadServerNameAsync(PostgreSqlGoldSrcOpsApiFactory factory) =>
        factory.ExecuteDbContextAsync(dbContext => dbContext.Servers.Select(server => server.Name).SingleAsync());

    private static Task<string> ReadDatabaseNameAsync(PostgreSqlGoldSrcOpsApiFactory factory) =>
        factory.ExecuteDbContextAsync(dbContext =>
            dbContext.Database.SqlQuery<string>($"SELECT current_database() AS \"Value\"").SingleAsync());

    private async Task<string[]> ReadDatabaseNamesAsync()
    {
        await using var connection = new NpgsqlConnection(_databaseFixture.AdministrationConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT datname FROM pg_database WHERE NOT datistemplate ORDER BY datname;";
        await using var reader = await command.ExecuteReaderAsync();
        var databases = new List<string>();
        while (await reader.ReadAsync())
        {
            databases.Add(reader.GetString(0));
        }

        return [.. databases];
    }
}
