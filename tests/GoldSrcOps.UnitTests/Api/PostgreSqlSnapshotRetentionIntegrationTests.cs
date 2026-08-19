using AwesomeAssertions;
using GoldSrcOps.Application.Monitoring;
using GoldSrcOps.Domain.Servers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GoldSrcOps.UnitTests.Api;

public sealed class PostgreSqlSnapshotRetentionIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task DeleteBatchOlderThanAsync_deletes_oldest_snapshots_in_bounded_batches_only()
    {
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync();
        var cutoffUtc = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        var seed = await factory.ExecuteDbContextAsync(async dbContext =>
        {
            var server = new Server(
                "Dust2 Public",
                GameServerKind.GoldSrc,
                new ServerEndpoint("127.0.0.1", queryPort: 27015, rconPort: null),
                pollIntervalSeconds: 30,
                notes: null,
                createdAtUtc: cutoffUtc.AddDays(-60));
            var oldestSnapshot = PollSnapshot.Unreachable(
                server.Id,
                cutoffUtc.AddMinutes(-2),
                "first timeout");
            var secondOldestSnapshot = PollSnapshot.Unreachable(
                server.Id,
                cutoffUtc.AddMinutes(-1),
                "second timeout");
            var boundarySnapshot = PollSnapshot.Reachable(
                server.Id,
                cutoffUtc,
                latencyMs: 20,
                map: "de_dust2",
                players: 10,
                maxPlayers: 32,
                bots: 0,
                rawVersion: "1.1.2.7/Stdio");
            var currentSnapshot = PollSnapshot.Reachable(
                server.Id,
                cutoffUtc.AddMinutes(1),
                latencyMs: 18,
                map: "de_inferno",
                players: 12,
                maxPlayers: 32,
                bots: 1,
                rawVersion: "1.1.2.7/Stdio");
            var incident = AvailabilityIncident.Open(
                server.Id,
                cutoffUtc.AddDays(-1),
                "Server query failed.",
                consecutiveFailures: 3);

            dbContext.Servers.Add(server);
            dbContext.PollSnapshots.AddRange(
                oldestSnapshot,
                secondOldestSnapshot,
                boundarySnapshot,
                currentSnapshot);
            dbContext.AvailabilityIncidents.Add(incident);
            await dbContext.SaveChangesAsync();

            return new RetentionSeed(
                server.Id,
                oldestSnapshot.Id,
                secondOldestSnapshot.Id,
                boundarySnapshot.Id,
                currentSnapshot.Id,
                incident.Id);
        });

        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPollSnapshotRetentionRepository>();

        var firstDeleted = await repository.DeleteBatchOlderThanAsync(
            cutoffUtc,
            batchSize: 1,
            CancellationToken.None);
        var idsAfterFirstPass = await ListSnapshotIdsAsync(factory, seed.ServerId);
        var secondDeleted = await repository.DeleteBatchOlderThanAsync(
            cutoffUtc,
            batchSize: 1,
            CancellationToken.None);
        var thirdDeleted = await repository.DeleteBatchOlderThanAsync(
            cutoffUtc,
            batchSize: 1,
            CancellationToken.None);

        firstDeleted.Should().Be(1);
        idsAfterFirstPass.Should().BeEquivalentTo(
            [seed.SecondOldestSnapshotId, seed.BoundarySnapshotId, seed.CurrentSnapshotId]);
        secondDeleted.Should().Be(1);
        thirdDeleted.Should().Be(0);

        var persisted = await factory.ExecuteDbContextAsync(async dbContext => new
        {
            SnapshotIds = await dbContext.PollSnapshots
                .Where(x => x.ServerId == seed.ServerId)
                .OrderBy(x => x.CheckedAtUtc)
                .Select(x => x.Id)
                .ToListAsync(),
            ServerExists = await dbContext.Servers.AnyAsync(x => x.Id == seed.ServerId),
            CurrentStateExists = await dbContext.ServerCurrentStates.AnyAsync(x => x.ServerId == seed.ServerId),
            IncidentExists = await dbContext.AvailabilityIncidents.AnyAsync(x => x.Id == seed.IncidentId)
        });

        persisted.SnapshotIds.Should().Equal(seed.BoundarySnapshotId, seed.CurrentSnapshotId);
        persisted.ServerExists.Should().BeTrue();
        persisted.CurrentStateExists.Should().BeTrue();
        persisted.IncidentExists.Should().BeTrue();
        persisted.SnapshotIds.Should().NotContain(seed.OldestSnapshotId);
        persisted.SnapshotIds.Should().NotContain(seed.SecondOldestSnapshotId);
    }

    private static async Task<IReadOnlyList<Guid>> ListSnapshotIdsAsync(
        PostgreSqlGoldSrcOpsApiFactory factory,
        Guid serverId)
    {
        return await factory.ExecuteDbContextAsync(async dbContext =>
            await dbContext.PollSnapshots
                .Where(x => x.ServerId == serverId)
                .OrderBy(x => x.CheckedAtUtc)
                .Select(x => x.Id)
                .ToListAsync());
    }

    private sealed record RetentionSeed(
        Guid ServerId,
        Guid OldestSnapshotId,
        Guid SecondOldestSnapshotId,
        Guid BoundarySnapshotId,
        Guid CurrentSnapshotId,
        Guid IncidentId);
}
