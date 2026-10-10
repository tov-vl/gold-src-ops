using Npgsql;

namespace GoldSrcOps.UnitTests.Api;

internal sealed class PostgreSqlDatabaseLease : IAsyncDisposable
{
    private readonly string _administrationConnectionString;
    private readonly string _databaseName = $"test_{Guid.NewGuid():N}";
    private bool _disposed;

    public PostgreSqlDatabaseLease(string administrationConnectionString)
    {
        _administrationConnectionString = administrationConnectionString;
        ConnectionString = new NpgsqlConnectionStringBuilder(administrationConnectionString)
        {
            Database = _databaseName,
            Pooling = true
        }.ConnectionString;
    }

    public string ConnectionString { get; }

    // The identifier is generated here, never supplied by a caller.
    public Task InitializeAsync() => ExecuteAdministrationAsync($"CREATE DATABASE \"{_databaseName}\";");

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        using var poolConnection = new NpgsqlConnection(ConnectionString);
        NpgsqlConnection.ClearPool(poolConnection);
        await ExecuteAdministrationAsync($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE);");
        _disposed = true;
    }

    private async Task ExecuteAdministrationAsync(string sql)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = new NpgsqlConnection(_administrationConnectionString);
        await connection.OpenAsync(timeout.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(timeout.Token);
    }
}
