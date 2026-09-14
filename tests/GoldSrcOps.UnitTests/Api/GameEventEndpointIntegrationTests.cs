using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using GoldSrcOps.Contracts.GameEvents;
using GoldSrcOps.Domain.GameEvents;
using GoldSrcOps.Domain.Servers;
using Microsoft.EntityFrameworkCore;

namespace GoldSrcOps.UnitTests.Api;

public sealed class GameEventEndpointIntegrationTests
{
    [Fact]
    public async Task Agent_can_ingest_once_and_replay_the_same_event_idempotently()
    {
        var server = CreateServer();
        await using var factory = new GoldSrcOpsApiFactory(
            principal: TestApiPrincipal.GameEventAgent(server.Id));
        await SeedServerAsync(factory, server);
        using var client = factory.CreateClient();
        var request = CreateRequest();

        using var acceptedResponse = await client.PostAsJsonAsync(
            $"/api/servers/{server.Id}/game-events",
            request);
        using var replayResponse = await client.PostAsJsonAsync(
            $"/api/servers/{server.Id}/game-events",
            request);

        acceptedResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        replayResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var accepted = await acceptedResponse.Content.ReadFromJsonAsync<GameEventIngestResponse>();
        var replayed = await replayResponse.Content.ReadFromJsonAsync<GameEventIngestResponse>();
        accepted.Should().NotBeNull();
        accepted!.Duplicate.Should().BeFalse();
        accepted.EventId.Should().Be(request.EventId);
        accepted.ServerId.Should().Be(server.Id);
        replayed.Should().Be(accepted with { Duplicate = true });

        var persisted = await factory.ExecuteDbContextAsync(async dbContext =>
            await dbContext.GameEventInbox
                .AsNoTracking()
                .SingleAsync());
        persisted.IntentHash.Should().HaveLength(GameEventInboxEntry.MaxIntentHashLength);
        persisted.Map.Should().Be("de_dust2");
        persisted.Players.Should().Be(10);
        persisted.Bots.Should().Be(0);
    }

    [Fact]
    public async Task Agent_receives_stable_conflicts_for_reused_event_id_or_source_sequence()
    {
        var server = CreateServer();
        await using var factory = new GoldSrcOpsApiFactory(
            principal: TestApiPrincipal.GameEventAgent(server.Id));
        await SeedServerAsync(factory, server);
        using var client = factory.CreateClient();
        var request = CreateRequest();
        using var accepted = await client.PostAsJsonAsync(
            $"/api/servers/{server.Id}/game-events",
            request);
        accepted.EnsureSuccessStatusCode();

        using var eventIdConflict = await client.PostAsJsonAsync(
            $"/api/servers/{server.Id}/game-events",
            request with { Players = 11 });
        using var sequenceConflict = await client.PostAsJsonAsync(
            $"/api/servers/{server.Id}/game-events",
            request with { EventId = Guid.NewGuid() });

        eventIdConflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await eventIdConflict.Content.ReadAsStringAsync()).Should()
            .Contain("game_event.event_id_conflict");
        sequenceConflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await sequenceConflict.Content.ReadAsStringAsync()).Should()
            .Contain("game_event.source_sequence_conflict");
        var count = await factory.ExecuteDbContextAsync(async dbContext =>
            await dbContext.GameEventInbox.CountAsync());
        count.Should().Be(1);
    }

    [Fact]
    public async Task Agent_is_forbidden_from_another_server_and_from_human_read_endpoints()
    {
        var server = CreateServer();
        await using var factory = new GoldSrcOpsApiFactory(
            principal: TestApiPrincipal.GameEventAgent(server.Id));
        await SeedServerAsync(factory, server);
        using var client = factory.CreateClient();

        using var crossServer = await client.PostAsJsonAsync(
            $"/api/servers/{Guid.NewGuid()}/game-events",
            CreateRequest());
        using var humanRead = await client.GetAsync("/api/servers");
        using var gameEventRead = await client.GetAsync(
            $"/api/servers/{server.Id}/game-events");

        crossServer.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        humanRead.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        gameEventRead.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var count = await factory.ExecuteDbContextAsync(async dbContext =>
            await dbContext.GameEventInbox.CountAsync());
        count.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Human_application_roles_cannot_ingest_game_events(bool operatorRole)
    {
        var server = CreateServer();
        var principal = operatorRole
            ? TestApiPrincipal.Operator()
            : TestApiPrincipal.Reader();
        await using var factory = new GoldSrcOpsApiFactory(principal: principal);
        await SeedServerAsync(factory, server);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/servers/{server.Id}/game-events",
            CreateRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Human_reader_roles_can_read_only_a_bounded_round_projection(bool operatorRole)
    {
        var server = CreateServer();
        var otherServer = CreateServer();
        var principal = operatorRole
            ? TestApiPrincipal.Operator()
            : TestApiPrincipal.Reader();
        await using var factory = new GoldSrcOpsApiFactory(principal: principal);
        var occurredAtUtc = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        var olderRound = CreateEntry(
            server.Id,
            sequenceNumber: 1,
            GameEventType.RoundEnded,
            occurredAtUtc.AddMinutes(-10),
            "de_dust2",
            players: 10,
            bots: 1);
        var newerRound = CreateEntry(
            server.Id,
            sequenceNumber: 2,
            GameEventType.RoundEnded,
            occurredAtUtc,
            "de_train",
            players: 12,
            bots: 0);
        var excludedRoundStart = CreateEntry(
            server.Id,
            sequenceNumber: 3,
            GameEventType.RoundStarted,
            occurredAtUtc.AddMinutes(1),
            "de_train",
            players: 12,
            bots: 0);
        var excludedOtherServer = CreateEntry(
            otherServer.Id,
            sequenceNumber: 1,
            GameEventType.RoundEnded,
            occurredAtUtc.AddMinutes(2),
            "de_nuke",
            players: 8,
            bots: 0);
        await factory.ExecuteDbContextAsync(async dbContext =>
        {
            dbContext.Servers.AddRange(server, otherServer);
            dbContext.GameEventInbox.AddRange(
                olderRound,
                newerRound,
                excludedRoundStart,
                excludedOtherServer);
            await dbContext.SaveChangesAsync();
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/api/servers/{server.Id}/game-events?limit=2");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var history = JsonSerializer.Deserialize<GameEventHistoryResponse>(
            body,
            JsonSerializerOptions.Web);
        history.Should().NotBeNull();
        history!.ServerId.Should().Be(server.Id);
        history.Limit.Should().Be(2);
        history.Items.Select(static item => item.OccurredAtUtc).Should().Equal(
            newerRound.OccurredAtUtc,
            olderRound.OccurredAtUtc);
        history.Items.Should().OnlyContain(static item => item.Type == "round.ended");
        history.Items[0].Map.Should().Be("de_train");
        history.Items[0].Players.Should().Be(12);
        history.Items[0].Bots.Should().Be(0);

        using var document = JsonDocument.Parse(body);
        var firstItemProperties = document.RootElement
            .GetProperty("items")[0]
            .EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
        firstItemProperties.Should().BeEquivalentTo(
            ["type", "occurredAtUtc", "map", "players", "bots"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Reader_projection_rejects_an_out_of_range_limit(int limit)
    {
        await using var factory = new GoldSrcOpsApiFactory(
            principal: TestApiPrincipal.Reader());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/api/servers/{Guid.NewGuid()}/game-events?limit={limit}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("limit");
    }

    [Fact]
    public async Task Reader_projection_returns_not_found_for_an_unknown_server()
    {
        await using var factory = new GoldSrcOpsApiFactory(
            principal: TestApiPrincipal.Reader());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/api/servers/{Guid.NewGuid()}/game-events");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Endpoint_rejects_invalid_contract_map_and_population_without_writing()
    {
        var server = CreateServer();
        await using var factory = new GoldSrcOpsApiFactory(
            principal: TestApiPrincipal.GameEventAgent(server.Id));
        await SeedServerAsync(factory, server);
        using var client = factory.CreateClient();
        var request = CreateRequest() with
        {
            ContractVersion = 2,
            Map = "../private",
            Bots = 11
        };

        using var response = await client.PostAsJsonAsync(
            $"/api/servers/{server.Id}/game-events",
            request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(nameof(request.ContractVersion));
        body.Should().Contain(nameof(request.Map));
        body.Should().Contain(nameof(request.Bots));
        var count = await factory.ExecuteDbContextAsync(async dbContext =>
            await dbContext.GameEventInbox.CountAsync());
        count.Should().Be(0);
    }

    [Fact]
    public async Task Endpoint_rejects_request_body_larger_than_four_kibibytes()
    {
        var server = CreateServer();
        await using var factory = new GoldSrcOpsApiFactory(
            principal: TestApiPrincipal.GameEventAgent(server.Id));
        await SeedServerAsync(factory, server);
        using var client = factory.CreateClient();
        var request = CreateRequest() with { Type = new string('x', 5_000) };

        using var response = await client.PostAsJsonAsync(
            $"/api/servers/{server.Id}/game-events",
            request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        var count = await factory.ExecuteDbContextAsync(async dbContext =>
            await dbContext.GameEventInbox.CountAsync());
        count.Should().Be(0);
    }

    private static Server CreateServer() =>
        new(
            "Game event endpoint test",
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
            Type: "round.started",
            OccurredAtUtc: DateTimeOffset.UtcNow.AddSeconds(-1),
            Map: "de_dust2",
            Players: 10,
            Bots: 0);

    private static GameEventInboxEntry CreateEntry(
        Guid serverId,
        long sequenceNumber,
        GameEventType type,
        DateTimeOffset occurredAtUtc,
        string? map,
        int? players,
        int? bots) =>
        new(
            Guid.NewGuid(),
            serverId,
            Guid.NewGuid(),
            sequenceNumber,
            GameEventInboxEntry.CurrentContractVersion,
            type,
            occurredAtUtc,
            occurredAtUtc.AddSeconds(1),
            map,
            players,
            bots,
            new string('A', GameEventInboxEntry.MaxIntentHashLength));

    private static Task SeedServerAsync(GoldSrcOpsApiFactory factory, Server server) =>
        factory.ExecuteDbContextAsync(async dbContext =>
        {
            dbContext.Servers.Add(server);
            await dbContext.SaveChangesAsync();
        });
}
