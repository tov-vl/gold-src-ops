using Npgsql;
using Testcontainers.PostgreSql;

namespace GoldSrcOps.AlertReceiver.Tests.Integration;

[CollectionDefinition(CollectionName)]
public sealed class AlertReceiverPostgreSqlTestGroup
    : ICollectionFixture<AlertReceiverPostgreSqlFixture>
{
    public const string CollectionName = "AlertReceiverPostgreSql";
}

public sealed class AlertReceiverPostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _database =
        new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("alert_receiver_tests")
            .WithUsername("alert_receiver")
            .WithPassword("alert_receiver")
            .Build();

    public string ConnectionString => _database.GetConnectionString();

    public Task InitializeAsync() => _database.StartAsync();

    public async Task DisposeAsync() => await _database.DisposeAsync();

    public async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "DROP SCHEMA IF EXISTS receiver CASCADE;";
        await command.ExecuteNonQueryAsync();
    }
}
