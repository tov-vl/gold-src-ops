using System.Net;
using System.Security.Cryptography;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Containers;
using Testcontainers.PostgreSql;

namespace GoldSrcOps.TelemetryAcceptance;

internal static class AcceptancePostgres
{
    internal const string Image = "postgres:16-alpine";
    internal const string DatabaseName = "telemetry_acceptance";
    internal const string Username = "acceptance";
    private const string DumpPath = "/tmp/telemetry-acceptance.dump";

    public static PostgreSqlContainer Build(bool archiveWal = false)
    {
        var builder = new PostgreSqlBuilder(Image)
            .WithDatabase(DatabaseName)
            .WithUsername(Username)
            .WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))
            .WithCreateParameterModifier(BindLoopback);
        return (archiveWal ? builder.WithCommand("-c", "archive_mode=on", "-c", PitrPostgres.ArchiveCommand) : builder).Build();
    }

    internal static void BindLoopback(CreateContainerParameters parameters)
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
    }

    public static void RequireLocal(IContainer database) =>
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
