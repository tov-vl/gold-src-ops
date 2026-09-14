using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using GoldSrcOps.Application.GameEvents;
using GoldSrcOps.Contracts.GameEvents;
using GoldSrcOps.Domain.GameEvents;
using GoldSrcOps.Domain.Servers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GoldSrcOps.UnitTests.Api;

public sealed class PostgreSqlGameEventInboxIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Inbox_is_race_safe_idempotent_and_retained_in_bounded_batches()
    {
        var server = CreateServer();
        await using var factory = await PostgreSqlGoldSrcOpsApiFactory.CreateAsync(
            principal: TestApiPrincipal.GameEventAgent(server.Id));
        await factory.ExecuteDbContextAsync(async dbContext =>
        {
            dbContext.Servers.Add(server);
            await dbContext.SaveChangesAsync();
        });
        using var firstClient = factory.CreateClient();
        using var secondClient = factory.CreateClient();
        var request = CreateRequest();

        var responses = await Task.WhenAll(
            firstClient.PostAsJsonAsync($"/api/servers/{server.Id}/game-events", request),
            secondClient.PostAsJsonAsync($"/api/servers/{server.Id}/game-events", request));

        responses.Select(static response => response.StatusCode).Should().BeEquivalentTo(
            [HttpStatusCode.Accepted, HttpStatusCode.OK]);
        foreach (var response in responses)
        {
            response.Dispose();
        }

        using var sequenceConflict = await firstClient.PostAsJsonAsync(
            $"/api/servers/{server.Id}/game-events",
            request with { EventId = Guid.NewGuid() });
        sequenceConflict.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var nowUtc = DateTimeOffset.UtcNow;
        var cutoffUtc = nowUtc.AddDays(-45);
        var oldEvent = CreateEntry(
            server.Id,
            sequenceNumber: 2,
            receivedAtUtc: cutoffUtc.AddMinutes(-1));
        var boundaryEvent = CreateEntry(
            server.Id,
            sequenceNumber: 3,
            receivedAtUtc: cutoffUtc);
        await factory.ExecuteDbContextAsync(async dbContext =>
        {
            dbContext.GameEventInbox.AddRange(oldEvent, boundaryEvent);
            await dbContext.SaveChangesAsync();
        });

        using var scope = factory.Services.CreateScope();
        var retention = scope.ServiceProvider.GetRequiredService<IGameEventInboxRetentionRepository>();
        var firstDeleted = await retention.DeleteBatchReceivedBeforeAsync(
            cutoffUtc,
            batchSize: 1,
            CancellationToken.None);
        var secondDeleted = await retention.DeleteBatchReceivedBeforeAsync(
            cutoffUtc,
            batchSize: 1,
            CancellationToken.None);

        firstDeleted.Should().Be(1);
        secondDeleted.Should().Be(0);
        var persisted = await factory.ExecuteDbContextAsync(async dbContext =>
            await dbContext.GameEventInbox
                .AsNoTracking()
                .OrderBy(entry => entry.SequenceNumber)
                .Select(entry => new
                {
                    entry.Id,
                    entry.SequenceNumber,
                    entry.IntentHash
                })
                .ToListAsync());
        persisted.Should().HaveCount(2);
        persisted.Should().NotContain(entry => entry.Id == oldEvent.Id);
        persisted.Should().Contain(entry => entry.Id == boundaryEvent.Id);
        persisted.Should().OnlyContain(entry => entry.IntentHash.Length == 64);
    }

    private static Server CreateServer() =>
        new(
            "PostgreSQL game event test",
            GameServerKind.GoldSrc,
            new ServerEndpoint("127.0.0.1", queryPort: 27015, rconPort: null),
            pollIntervalSeconds: 30,
            notes: null,
            createdAtUtc: DateTimeOffset.UtcNow,
            isEnabled: false);

    private static GameEventIngestRequest CreateRequest() =>
        new(
            GameEventInboxEntry.CurrentContractVersion,
            Guid.NewGuid(),
            Guid.NewGuid(),
            SequenceNumber: 1,
            Type: "round.ended",
            OccurredAtUtc: DateTimeOffset.UtcNow.AddSeconds(-1),
            Map: "de_dust2",
            Players: 12,
            Bots: 0);

    private static GameEventInboxEntry CreateEntry(
        Guid serverId,
        long sequenceNumber,
        DateTimeOffset receivedAtUtc) =>
        new(
            Guid.NewGuid(),
            serverId,
            Guid.NewGuid(),
            sequenceNumber,
            GameEventInboxEntry.CurrentContractVersion,
            GameEventType.ServerStopped,
            receivedAtUtc.AddSeconds(-1),
            receivedAtUtc,
            map: null,
            players: null,
            bots: null,
            new string('A', GameEventInboxEntry.MaxIntentHashLength));
}
