using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.Monitoring;
using GoldSrcOps.Contracts.Servers;
using GoldSrcOps.Domain.Servers;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.UnitTests.Api;

public sealed class PostgreSqlEndpointIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostServer_registers_server_through_migrated_postgresql_schema()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync();
        using var client = factory.CreateClient();
        var request = new RegisterServerRequest(
            "Dust2 Public",
            "127.0.0.1",
            QueryPort: 27015,
            RconPort: null,
            PollIntervalSeconds: 30,
            Notes: "postgres integration test");

        var response = await client.PostAsJsonAsync("/api/servers", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var server = await response.Content.ReadFromJsonAsync<ServerResponse>();
        server.Should().NotBeNull();
        var serverId = server!.Id;
        var persisted = await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var entity = await dbContext.Servers
                .Include(x => x.CurrentState)
                .SingleAsync(x => x.Id == serverId);

            return new PersistedServer(
                entity.Id,
                entity.Name,
                entity.Game,
                entity.Endpoint.Host,
                entity.Endpoint.QueryPort,
                entity.Endpoint.RconPort,
                entity.PollIntervalSeconds,
                entity.Notes,
                entity.CurrentState?.Status,
                entity.CurrentState?.IsReachable,
                entity.CurrentState?.ConsecutiveFailures);
        });

        persisted.Should().BeEquivalentTo(new PersistedServer(
            serverId,
            "Dust2 Public",
            GameServerKind.GoldSrc,
            "127.0.0.1",
            QueryPort: 27015,
            RconPort: null,
            PollIntervalSeconds: 30,
            Notes: "postgres integration test",
            CurrentStatus: ServerStatus.Unknown,
            IsReachable: false,
            ConsecutiveFailures: 0));
    }

    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task GetServerSnapshots_filters_snapshots_using_postgresql_provider()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync();
        using var client = factory.CreateClient();
        var fromUtc = new DateTimeOffset(2026, 4, 25, 9, 0, 0, TimeSpan.Zero);
        var toUtc = new DateTimeOffset(2026, 4, 25, 10, 0, 0, TimeSpan.Zero);
        var seed = await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var server = CreateServer("Dust2 Public", "127.0.0.1", createdAtUtc: fromUtc.AddHours(-1));
            var otherServer = CreateServer("Inferno Public", "127.0.0.2", createdAtUtc: fromUtc.AddHours(-1));
            var expectedSnapshot = PollSnapshot.Unreachable(server.Id, toUtc, "query timeout");

            dbContext.Servers.AddRange(server, otherServer);
            dbContext.PollSnapshots.AddRange(
                PollSnapshot.Reachable(server.Id, fromUtc.AddMinutes(-1), 18, "de_train", 8, 32, 0, "1.1.2.7/Stdio"),
                PollSnapshot.Reachable(server.Id, fromUtc.AddMinutes(30), 25, "de_dust2", 12, 32, 1, "1.1.2.7/Stdio"),
                expectedSnapshot,
                PollSnapshot.Reachable(otherServer.Id, toUtc.AddMinutes(1), 19, "de_inferno", 5, 24, 0, null));
            await dbContext.SaveChangesAsync();

            return new SnapshotSeed(server.Id, expectedSnapshot.Id);
        });

        var response = await client.GetAsync(
            $"/api/servers/{seed.ServerId}/snapshots?from={ToQueryValue(fromUtc)}&to={ToQueryValue(toUtc)}&limit=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var history = await response.Content.ReadFromJsonAsync<SnapshotHistoryResponse>();
        history.Should().NotBeNull();
        history!.Items.Should().ContainSingle();
        history.Items[0].Should().BeEquivalentTo(new
        {
            Id = seed.ExpectedSnapshotId,
            seed.ServerId,
            CheckedAtUtc = toUtc,
            IsReachable = false,
            LatencyMs = (int?)null,
            Map = (string?)null,
            Players = (int?)null,
            MaxPlayers = (int?)null,
            Bots = (int?)null,
            RawVersion = (string?)null,
            FailureReason = "query timeout"
        });
    }

    private static Server CreateServer(string name, string host, DateTimeOffset createdAtUtc)
    {
        return new Server(
            name,
            GameServerKind.GoldSrc,
            new ServerEndpoint(host, queryPort: 27015, rconPort: null),
            pollIntervalSeconds: 30,
            notes: null,
            createdAtUtc);
    }

    private static string ToQueryValue(DateTimeOffset value)
    {
        return Uri.EscapeDataString(value.ToString("O", CultureInfo.InvariantCulture));
    }

    private sealed record PersistedServer(
        Guid Id,
        string Name,
        GameServerKind Game,
        string Host,
        int QueryPort,
        int? RconPort,
        int PollIntervalSeconds,
        string? Notes,
        ServerStatus? CurrentStatus,
        bool? IsReachable,
        int? ConsecutiveFailures);

    private sealed record SnapshotSeed(Guid ServerId, Guid ExpectedSnapshotId);
}
