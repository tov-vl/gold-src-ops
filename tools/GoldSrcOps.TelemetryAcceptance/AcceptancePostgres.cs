using System.Net;
using System.Security.Cryptography;
using Testcontainers.PostgreSql;

namespace GoldSrcOps.TelemetryAcceptance;

internal static class AcceptancePostgres
{
    private const string DatabaseName = "telemetry_acceptance";
    private const string Username = "acceptance";
    private const string DumpPath = "/tmp/telemetry-acceptance.dump";

    public static PostgreSqlContainer Build() => new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase(DatabaseName)
        .WithUsername(Username)
        .WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))
        .WithCreateParameterModifier(parameters =>
        {
            var hostConfig = parameters.HostConfig ?? throw new InvalidOperationException("Missing PostgreSQL host configuration.");
            var portBindings = hostConfig.PortBindings ?? throw new InvalidOperationException("Missing PostgreSQL port binding.");
            foreach (var bindings in portBindings.Values)
            {
                foreach (var binding in bindings)
                {
                    binding.HostIP = "127.0.0.1";
                }
            }

            hostConfig.Memory = 512 * 1024 * 1024;
            hostConfig.NanoCPUs = 1_000_000_000;
        })
        .Build();

    public static void RequireLocal(PostgreSqlContainer database) =>
        EntryPoint.Require(string.Equals(database.Hostname, "localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(database.Hostname, out var hostAddress) && IPAddress.IsLoopback(hostAddress)),
            "Acceptance requires a local Docker host.");

    public static async Task<byte[]> DumpAsync(PostgreSqlContainer database)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await database.ExecAsync(
            ["pg_dump", "--username", Username, "--dbname", DatabaseName, "--format=custom", "--no-owner", "--no-privileges", "--file", DumpPath],
            deadline.Token).ConfigureAwait(false);
        EntryPoint.Require(result.ExitCode == 0, "Synthetic PostgreSQL dump failed.");
        var dump = await database.ReadFileAsync(DumpPath, deadline.Token).ConfigureAwait(false);
        EntryPoint.Require(dump.Length is > 0 and <= 1024 * 1024, "Unexpected synthetic dump size.");
        return dump;
    }

    public static async Task RestoreAsync(PostgreSqlContainer database, byte[] dump, CancellationToken cancellationToken)
    {
        await database.CopyAsync(dump, DumpPath, ct: cancellationToken).ConfigureAwait(false);
        var copied = await database.ReadFileAsync(DumpPath, cancellationToken).ConfigureAwait(false);
        EntryPoint.Require(dump.AsSpan().SequenceEqual(copied), "Dump bytes changed during transfer.");
        var result = await database.ExecAsync(
            ["pg_restore", "--username", Username, "--dbname", DatabaseName, "--no-owner", "--no-privileges", "--single-transaction", "--exit-on-error", DumpPath],
            cancellationToken).ConfigureAwait(false);
        EntryPoint.Require(result.ExitCode == 0, "Synthetic PostgreSQL restore failed.");
    }
}
